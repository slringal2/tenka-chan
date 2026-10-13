using Microsoft.CodeAnalysis;

namespace TenkaChan.Mcp.Analysis;

public sealed record NativeRoslynCandidate(MethodSummary Summary, IMethodSymbol Symbol);

/// <summary>
/// Best-effort, fail-closed matching of RyuJIT method headers to Roslyn symbols.
/// Matches declaring type, method and primitive/array parameter signatures.
/// Never asserts a machine instruction corresponds to an IL offset/source line.
/// </summary>
public static class NativeMethodCorrelator
{
    public static NativeCodeReport Correlate(
        NativeCodeReport native, IReadOnlyList<NativeRoslynCandidate> candidates,
        string snapshotId)
    {
        var mapped = native.Methods.Select(method =>
        {
            var matches = candidates.Where(x =>
                x.Symbol.Name.Equals(method.MethodName, StringComparison.Ordinal) &&
                DeclaringTypeMatches(x.Symbol.ContainingType, method.DeclaringType))
                .ToArray();

            var (jitParameters, recognized) = ExtractParameterTypes(method.JitSignature);
            if (recognized)
            {
                matches = matches.Where(candidate =>
                    candidate.Symbol.Parameters.Length == jitParameters.Count &&
                    candidate.Symbol.Parameters.Select(parameter =>
                            Normalize(parameter.Type.ToDisplayString(
                                SymbolDisplayFormat.CSharpErrorMessageFormat)))
                        .SequenceEqual(jitParameters, StringComparer.Ordinal))
                    .ToArray();
            }
            else if (matches.Length > 1)
            {
                // No usable signature information; never guess an overload.
                return method with { MatchStatus = "ambiguous_no_parameter_signature" };
            }

            return matches.Length switch
            {
                1 => method with
                {
                    RoslynMethodId = matches[0].Summary.Id,
                    MatchStatus = "matched_method_signature",
                    RoslynSourcePath = matches[0].Summary.File,
                    RoslynSourceLine = matches[0].Summary.Line,
                    RoslynSignature = matches[0].Summary.Signature
                },
                > 1 => method with
                {
                    RoslynMethodId = null,
                    MatchStatus = "ambiguous_overloads"
                },
                _ => method with
                {
                    RoslynMethodId = null,
                    MatchStatus = "unmatched_or_unsupported_signature"
                }
            };
        }).ToArray();

        return native with
        {
            Methods = mapped,
            RoslynSnapshotId = snapshotId
        };
    }

    private static bool DeclaringTypeMatches(INamedTypeSymbol symbol, string jitType)
    {
        if (jitType.Length == 0) return false;
        var full = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        var nested = symbol.ToDisplayString();
        var withMetadata = symbol.MetadataName;
        if (!symbol.ContainingNamespace.IsGlobalNamespace)
            withMetadata = symbol.ContainingNamespace.ToDisplayString() + "." + withMetadata;
        return jitType == full || jitType == nested || jitType == withMetadata;
    }

    private static (IReadOnlyList<string> Parameters, bool Valid) ExtractParameterTypes(string jit)
    {
        var colon = jit.IndexOf(':');
        if (colon < 0) return ([], false);
        var open = jit.IndexOf('(', colon + 1);
        if (open < 0) return ([], false);
        var close = FindClosing(jit, open);
        if (close < 0) return ([], false);
        var inner = jit[(open + 1)..close];
        if (string.IsNullOrWhiteSpace(inner)) return ([], true);

        var tokens = new List<string>();
        var start = 0;
        var brackets = 0;
        var angle = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            switch (inner[i])
            {
                case '[': brackets++; break;
                case ']': brackets--; break;
                case '<': angle++; break;
                case '>': angle--; break;
                case ',' when brackets == 0 && angle == 0:
                    tokens.Add(inner[start..i]);
                    start = i + 1;
                    break;
            }
            if (brackets < 0 || angle < 0) return ([], false);
        }
        if (brackets != 0 || angle != 0) return ([], false);
        tokens.Add(inner[start..]);
        var normalized = tokens.Select(Normalize).ToArray();
        if (normalized.Any(string.IsNullOrWhiteSpace)) return ([], false);
        return (normalized, true);
    }

    private static int FindClosing(string source, int open)
    {
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '(') depth++;
            if (source[i] == ')')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static string Normalize(string value)
    {
        var normalized = value.Replace(" ", "", StringComparison.Ordinal)
            .Replace("System.Int32", "int", StringComparison.Ordinal)
            .Replace("System.Int64", "long", StringComparison.Ordinal)
            .Replace("System.Int16", "short", StringComparison.Ordinal)
            .Replace("System.UInt32", "uint", StringComparison.Ordinal)
            .Replace("System.UInt64", "ulong", StringComparison.Ordinal)
            .Replace("System.Boolean", "bool", StringComparison.Ordinal)
            .Replace("System.Char", "char", StringComparison.Ordinal)
            .Replace("System.String", "string", StringComparison.Ordinal)
            .Replace("System.Single", "float", StringComparison.Ordinal)
            .Replace("System.Double", "double", StringComparison.Ordinal)
            .Replace("System.Object", "object", StringComparison.Ordinal);
        return normalized;
    }
}

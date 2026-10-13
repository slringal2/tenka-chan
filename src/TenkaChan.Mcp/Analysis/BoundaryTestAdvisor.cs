using Microsoft.CodeAnalysis;

namespace TenkaChan.Mcp.Analysis;

public sealed record SuggestedTestInput(string Parameter, string Type,
    IReadOnlyList<string> CSharpExpressions, string Reason);

public sealed record SuggestedTestPlan(
    string MethodId, string Signature, bool ExternallyCallable,
    IReadOnlyList<SuggestedTestInput> Inputs,
    IReadOnlyList<string> Notes, string OracleStatus);

/// <summary>
/// Generate boundary-input suggestions from resolved parameter types.
/// It deliberately cannot invent expected outcomes or prove contracts.
/// </summary>
public static class BoundaryTestAdvisor
{
    public static SuggestedTestPlan Suggest(MethodSummary method, IMethodSymbol symbol)
    {
        var external = symbol.DeclaredAccessibility == Accessibility.Public
            && symbol.ContainingType.DeclaredAccessibility == Accessibility.Public
            && !symbol.IsGenericMethod;

        var cases = symbol.Parameters.Select(parameter =>
        {
            var type = parameter.Type;
            var expressionSet = type.SpecialType switch
            {
                SpecialType.System_Boolean => new[] { "false", "true" },
                SpecialType.System_Int32 => new[] { "0", "1", "-1", "int.MinValue", "int.MaxValue" },
                SpecialType.System_Int64 => new[] { "0L", "1L", "-1L", "long.MinValue", "long.MaxValue" },
                SpecialType.System_UInt32 => new[] { "0u", "1u", "uint.MaxValue" },
                SpecialType.System_UInt64 => new[] { "0UL", "1UL", "ulong.MaxValue" },
                SpecialType.System_String => new[]
                    { "null", "\"\"", "\" \"", "\"a\"", "new string('x', 10000)" },
                SpecialType.System_Double => new[]
                    { "0d", "1d", "-1d", "double.NaN", "double.PositiveInfinity", "double.NegativeInfinity" },
                SpecialType.System_Single => new[]
                    { "0f", "1f", "-1f", "float.NaN", "float.PositiveInfinity" },
                _ => Array.Empty<string>()
            };

            string reason;
            if (type is IArrayTypeSymbol array)
            {
                var name = array.ElementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                expressionSet = ["null", $"System.Array.Empty<{name}>()"];
                reason = "null and empty array boundary candidates";
            }
            else if (type.ToDisplayString() == "System.Threading.CancellationToken")
            {
                expressionSet =
                    ["default(System.Threading.CancellationToken)",
                     "new System.Threading.CancellationToken(true)"];
                reason = "normal versus pre-cancelled operation";
            }
            else if (expressionSet.Length > 0)
            {
                reason = "semantic type boundaries; null/NaN/Infinity may be outside the public contract";
            }
            else
            {
                expressionSet = ["default(" + type.ToDisplayString(
                    SymbolDisplayFormat.FullyQualifiedFormat) + ")"];
                reason = "Only a placeholder was inferred; obtain a valid object fixture manually";
            }

            return new SuggestedTestInput(parameter.Name,
                type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                expressionSet, reason);
        }).ToArray();

        var notes = new List<string>
        {
            "Generate independent assertions only after obtaining a specification, invariant or trusted reference implementation.",
            "A parameter combination is a candidate input, not proof of expected behavior.",
            "Set up instance state, dependency injection, and async assertions when required."
        };
        if (!external)
            notes.Add("Method may not be externally callable; prefer its public API or use an isolated test adapter.");
        if (symbol.Parameters.Any(p => p.RefKind is not RefKind.None))
            notes.Add("ref, in and out parameters require special test invocation syntax.");
        if (symbol.IsGenericMethod)
            notes.Add("Generic method type parameters require usable concrete type arguments.");

        return new SuggestedTestPlan(method.Id, method.Signature, external,
            cases, notes, "unresolved");
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TenkaChan.Mcp.Analysis;

/// <summary>
/// Conservative discovery rules. A finding means "measure or investigate",
/// never "this definitely makes the application slower".
/// </summary>
public static class RuleScanner
{
    public static IReadOnlyList<RuleFinding> Scan(
        SyntaxNode root, SemanticModel model, string file, int maxFindings = 500)
    {
        var result = new List<RuleFinding>();
        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var symbol = model.GetSymbolInfo(call).Symbol as IMethodSymbol;
            var methodName = symbol?.Name;
            if (InsideLoop(call) && (methodName is "ToList" or "ToArray")
                && IsLinq(symbol))
            {
                Add("PERF1001", "allocation",
                    "LINQ materialization inside a loop; verify whether it is required.",
                    call, "benchmark time and allocated bytes");
            }

            if (InsideLoop(call) && methodName == "Count" && IsLinq(symbol)
                && call.Parent is BinaryExpressionSyntax binary
                && (binary.IsKind(SyntaxKind.GreaterThanExpression)
                    || binary.IsKind(SyntaxKind.NotEqualsExpression)))
            {
                Add("PERF1004", "enumeration",
                    "LINQ Count() in a comparison may enumerate unnecessarily; check semantics and consider Any().",
                    call, "behavioral tests plus enumeration-count benchmark");
            }
        }

        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (InsideLoop(assignment)
                && assignment.IsKind(SyntaxKind.AddAssignmentExpression)
                && model.GetTypeInfo(assignment.Left).Type?.SpecialType == SpecialType.System_String)
            {
                Add("PERF1002", "allocation",
                    "String += inside a loop; investigate temporary strings and possible StringBuilder use.",
                    assignment, "benchmark increasing input sizes and allocated bytes");
            }
        }

        foreach (var created in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var symbol = model.GetSymbolInfo(created).Symbol as IMethodSymbol;
            if (InsideLoop(created)
                && symbol?.ContainingType.ToDisplayString() == "System.Text.RegularExpressions.Regex")
            {
                Add("PERF1003", "allocation",
                    "Regex constructed inside a loop; consider reuse or GeneratedRegex where semantics allow.",
                    created, "benchmark matching behavior and execution time");
            }
        }

        foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            var containingMethod = access.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            if (containingMethod?.Modifiers.Any(SyntaxKind.AsyncKeyword) != true)
                continue;

            var symbol = model.GetSymbolInfo(access).Symbol;
            if (access.Name.Identifier.ValueText == "Result"
                && symbol is IPropertySymbol property
                && property.ContainingType.ToDisplayString().StartsWith("System.Threading.Tasks.Task<", StringComparison.Ordinal))
            {
                Add("ASYNC1001", "async-blocking",
                    "Task.Result in an async method may block; check whether await preserves semantics.",
                    access, "async/concurrency test and timeout under load");
            }
        }

        return result.Take(Math.Clamp(maxFindings, 1, 5000)).ToArray();

        void Add(string rule, string category, string message, SyntaxNode node, string verification)
        {
            if (result.Count >= Math.Clamp(maxFindings, 1, 5000)) return;
            var line = node.GetLocation().GetLineSpan().StartLinePosition;
            result.Add(new RuleFinding(rule, category, "info", message,
                file, line.Line + 1, line.Character + 1, null, verification));
        }
    }

    private static bool InsideLoop(SyntaxNode node) =>
        node.Ancestors().Any(parent => parent is ForStatementSyntax or ForEachStatementSyntax
            or ForEachVariableStatementSyntax or WhileStatementSyntax or DoStatementSyntax);

    private static bool IsLinq(IMethodSymbol? symbol) =>
        symbol?.ContainingType.ToDisplayString() is "System.Linq.Enumerable" or "System.Linq.Queryable";
}

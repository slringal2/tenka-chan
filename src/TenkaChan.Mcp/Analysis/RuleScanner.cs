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

            if (InsideAsyncMethod(call)
                && methodName == "Wait"
                && symbol?.ContainingType.OriginalDefinition.ToDisplayString()
                    .StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal) == true)
            {
                Add("ASYNC1002", "async-blocking",
                    "Task.Wait() inside an async method can block threads; inspect synchronization context and cancellation.",
                    call, "async concurrency and timeout tests");
            }

            if (InsideAsyncMethod(call) && methodName == "Sleep"
                && symbol?.ContainingType.ToDisplayString() == "System.Threading.Thread")
            {
                Add("ASYNC1003", "async-blocking",
                    "Thread.Sleep inside an async method blocks a thread; evaluate Task.Delay semantics.",
                    call, "latency and concurrency measurements");
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
                && symbol?.ContainingType.ToDisplayString() == "System.Net.Http.HttpClient")
            {
                Add("PERF1005", "resource",
                    "HttpClient created inside a loop; investigate handler lifetime and connection reuse.",
                    created, "network fixture and resource/latency measurements");
            }

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

        foreach (var statement in root.DescendantNodes().OfType<CatchClauseSyntax>())
        {
            if (statement.Block.Statements.Count == 0)
            {
                Add("CORRECT1001", "correctness",
                    "Empty catch block silently swallows exceptions; verify the failure policy.",
                    statement, "fault-injection tests for exceptional conditions");
            }
        }

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (method.Modifiers.Any(SyntaxKind.AsyncKeyword)
                && (model.GetDeclaredSymbol(method) as IMethodSymbol)?.ReturnsVoid == true)
            {
                Add("ASYNC1004", "async-error-handling",
                    "async void method may lose awaitable error propagation; check whether this is an event handler.",
                    method, "exception propagation test");
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

    private static bool InsideAsyncMethod(SyntaxNode node) =>
        node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()
            ?.Modifiers.Any(SyntaxKind.AsyncKeyword) == true;

    private static bool InsideLoop(SyntaxNode node) =>
        node.Ancestors().Any(parent => parent is ForStatementSyntax or ForEachStatementSyntax
            or ForEachVariableStatementSyntax or WhileStatementSyntax or DoStatementSyntax);

    private static bool IsLinq(IMethodSymbol? symbol) =>
        symbol?.ContainingType.ToDisplayString() is "System.Linq.Enumerable" or "System.Linq.Queryable";
}

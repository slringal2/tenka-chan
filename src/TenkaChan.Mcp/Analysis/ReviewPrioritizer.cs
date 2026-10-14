using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TenkaChan.Mcp.Analysis;

public sealed record ReviewTarget(
    MethodSummary Method, double Priority, int ApproximateComplexity, int SourceLines,
    int LoopCount, int ConditionalCount, int AwaitCount, int ObjectCreationSites,
    IReadOnlyList<string> RiskHints);

/// <summary>
/// Deterministic prioritization, not defect probability or measured performance.
/// Syntax-only complexity is deliberately approximate.
/// </summary>
public static class ReviewPrioritizer
{
    public static ReviewTarget Assess(MethodSummary method, SyntaxNode syntax)
    {
        var descendants = CallableSyntax.OwnNodes(syntax).ToArray();
        var loops = descendants.Count(node => node is ForStatementSyntax or ForEachStatementSyntax
            or ForEachVariableStatementSyntax or WhileStatementSyntax or DoStatementSyntax);
        var conditionalCount = descendants.Count(node =>
            node is IfStatementSyntax or ConditionalExpressionSyntax or CatchClauseSyntax
                or SwitchExpressionArmSyntax or CaseSwitchLabelSyntax
            || node is BinaryExpressionSyntax binary
                && (binary.IsKind(SyntaxKind.LogicalAndExpression)
                    || binary.IsKind(SyntaxKind.LogicalOrExpression)
                    || binary.IsKind(SyntaxKind.CoalesceExpression)));
        var awaits = descendants.Count(node => node is AwaitExpressionSyntax);
        var created = descendants.Count(node => node is ObjectCreationExpressionSyntax
            or ImplicitObjectCreationExpressionSyntax or ArrayCreationExpressionSyntax);
        var hasLock = descendants.Any(node => node is LockStatementSyntax);
        var hasTry = descendants.Any(node => node is TryStatementSyntax);
        var hasThrow = descendants.Any(node => node is ThrowStatementSyntax or ThrowExpressionSyntax);
        var hasUnsafe = syntax.ChildTokens().Any(t => t.IsKind(SyntaxKind.UnsafeKeyword));
        var sourceLines = Math.Max(1, syntax.GetLocation().GetLineSpan().EndLinePosition.Line -
            syntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1);

        var hints = new List<string>();
        if (loops > 0) hints.Add("loops");
        if (awaits > 0) hints.Add("async_suspension");
        if (hasLock) hints.Add("synchronization");
        if (hasTry) hints.Add("exception_handling");
        if (hasThrow) hints.Add("explicit_throw");
        if (created > 0 && loops > 0) hints.Add("possible_allocations");
        if (hasUnsafe) hints.Add("unsafe");
        if (sourceLines > 75) hints.Add("large_method");

        var score = Math.Round(
            1.0 + conditionalCount * 1.3 + loops * 2.3
            + awaits * 2.0 + created * 0.45
            + Math.Min(sourceLines, 300) * 0.05
            + (hasLock ? 5 : 0) + (hasTry ? 2 : 0) + (hasUnsafe ? 4 : 0),
            2);
        return new ReviewTarget(method, score, 1 + conditionalCount + loops,
            sourceLines, loops, conditionalCount, awaits, created, hints);
    }
}

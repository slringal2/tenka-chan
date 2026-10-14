using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TenkaChan.Mcp.Analysis;

/// <summary>Compiler-semantic patterns; proving a conversion is not proving runtime allocation cost.</summary>
public static class DeterministicSemanticRules
{
    public static IReadOnlyList<RuleFinding> Scan(SyntaxNode root, SemanticModel model,
        string file, CancellationToken cancellationToken = default)
    {
        var results = new List<RuleFinding>();
        foreach (var statement in root.DescendantNodes().OfType<ThrowStatementSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (statement.Expression is not IdentifierNameSyntax identifier) continue;
            var catchClause = statement.Ancestors().OfType<CatchClauseSyntax>()
                .FirstOrDefault(c => c.Block.Span.Contains(statement.Span));
            if (catchClause?.Declaration is null || catchClause.Declaration.Identifier.ValueText != identifier.Identifier.ValueText)
                continue;
            var thrownSymbol = model.GetSymbolInfo(identifier, cancellationToken).Symbol;
            var caughtSymbol = model.GetDeclaredSymbol(catchClause.Declaration, cancellationToken);
            if (thrownSymbol is null || caughtSymbol is null || !SymbolEqualityComparer.Default.Equals(thrownSymbol, caughtSymbol)) continue;
            Add("CORRECT2001", statement, "throw " + identifier.Identifier.ValueText +
                " resets the caught exception's stack trace; bare throw preserves it.",
                "If stack trace preservation is intended, replace with bare throw;.");
        }

        // GetConversion handles contextual conversions to object AND implemented
        // interfaces, including argument passing, assignments and return values.
        // ClassifyConversion handles explicit boxing casts. Only concrete/proven
        // value types qualify; unknown generic/dynamic conversion costs are not guessed.
        foreach (var expression in root.DescendantNodes().OfType<ExpressionSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsInsideExecutableLoop(expression) || model.GetOperation(expression, cancellationToken) is null) continue;
            if (expression.Parent is CastExpressionSyntax) continue; // report the cast once, not its operand twice
            var operand = expression is CastExpressionSyntax cast ? cast.Expression : expression;
            if (model.GetTypeInfo(operand, cancellationToken).Type?.IsValueType != true) continue;
            var boxing = expression is CastExpressionSyntax explicitCast
                ? model.GetTypeInfo(explicitCast.Type, cancellationToken).Type is { } target &&
                    model.ClassifyConversion(explicitCast.Expression, target, isExplicitInSource: true).IsBoxing
                : model.GetConversion(expression, cancellationToken).IsBoxing;
            if (!boxing) continue;
            Add("PERF2001", expression, "Roslyn resolved a value-type boxing conversion inside an executable loop.",
                "Inspect IL and measure allocated bytes in the target runtime; JIT optimization and nullable values can change actual allocation cost.");
        }
        return results.DistinctBy(x => (x.RuleId, x.Line, x.Column)).OrderBy(x => x.Line)
            .ThenBy(x => x.Column).ThenBy(x => x.RuleId, StringComparer.Ordinal).ToArray();

        void Add(string id, SyntaxNode node, string message, string verification)
        {
            var span = node.GetLocation().GetLineSpan();
            results.Add(new RuleFinding(id, DeterministicRuleCatalog.Get(id).Category, "warning",
                message, file, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1, null, verification));
        }
    }

    private static bool IsInsideExecutableLoop(SyntaxNode node)
    {
        foreach (var parent in node.Ancestors())
        {
            if (parent is ForStatementSyntax or ForEachStatementSyntax or ForEachVariableStatementSyntax
                or WhileStatementSyntax or DoStatementSyntax) return true;
            if (parent is MethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax
                or ConstructorDeclarationSyntax or AccessorDeclarationSyntax) return false;
        }
        return false;
    }
}

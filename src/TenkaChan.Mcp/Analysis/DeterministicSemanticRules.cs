using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace TenkaChan.Mcp.Analysis;

/// <summary>
/// Additional Roslyn-semantic rules. No local model is called here.
/// Findings distinguish a provable language behavior from a possible
/// application defect or performance problem.
/// </summary>
public static class DeterministicSemanticRules
{
    public static IReadOnlyList<RuleFinding> Scan(
        SyntaxNode root, SemanticModel model, string file,
        CancellationToken cancellationToken = default)
    {
        var results = new List<RuleFinding>();

        foreach (var statement in root.DescendantNodes().OfType<ThrowStatementSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (statement.Expression is not IdentifierNameSyntax identifier) continue;
            // A throw of a caught exception variable inside that catch body
            // restarts the stack trace; a bare throw preserves it.
            var catchClause = statement.Ancestors().OfType<CatchClauseSyntax>()
                .FirstOrDefault(c => c.Block.Span.Contains(statement.Span));
            if (catchClause?.Declaration is null ||
                catchClause.Declaration.Identifier.ValueText != identifier.Identifier.ValueText)
                continue;

            var thrownSymbol = model.GetSymbolInfo(identifier, cancellationToken).Symbol;
            var caughtSymbol = model.GetDeclaredSymbol(catchClause.Declaration, cancellationToken);
            if (thrownSymbol is null || caughtSymbol is null ||
                !SymbolEqualityComparer.Default.Equals(thrownSymbol, caughtSymbol))
                continue;

            Add("CORRECT2001", statement,
                "throw " + identifier.Identifier.ValueText +
                " resets the caught exception's stack trace; bare throw preserves it.",
                "If stack trace preservation is intended, replace with bare throw;.");
        }

        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsInsideExecutableLoop(assignment)) continue;
            var operation = model.GetOperation(assignment.Right, cancellationToken);
            if (operation is null) continue;
            // Narrow the rule to an actual conversion to System.Object from
            // a value type. More complex interface and generic conversions
            // are intentionally left out until the analysis can prove them.
            var target = model.GetTypeInfo(assignment.Left, cancellationToken).Type;
            if (target?.SpecialType != SpecialType.System_Object) continue;
            var operandType = model.GetTypeInfo(assignment.Right, cancellationToken).Type;
            if (operandType?.IsValueType != true) continue;
            if (operation is IConversionOperation conversion &&
                conversion.Operand.Type?.IsValueType != true) continue;
            Add("PERF2001", assignment.Right,
                "Value type assigned to object in a loop; boxing may allocate.",
                "Inspect compiled IL and compare allocations in the target runtime.");
        }

        foreach (var declaration in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (declaration.Initializer?.Value is not ExpressionSyntax initializer ||
                !IsInsideExecutableLoop(declaration)) continue;
            var declared = model.GetDeclaredSymbol(declaration, cancellationToken);
            if (declared is not ILocalSymbol local ||
                local.Type.SpecialType != SpecialType.System_Object) continue;
            var valueType = model.GetTypeInfo(initializer, cancellationToken).Type;
            if (valueType?.IsValueType != true) continue;
            Add("PERF2001", initializer,
                "Value type assigned to object in a loop; boxing may allocate.",
                "Inspect compiled IL and compare allocations in the target runtime.");
        }

        return results
            .OrderBy(x => x.Line).ThenBy(x => x.Column)
            .ThenBy(x => x.RuleId, StringComparer.Ordinal).ToArray();

        void Add(string id, SyntaxNode node, string message, string verification)
        {
            var span = node.GetLocation().GetLineSpan();
            results.Add(new RuleFinding(id,
                DeterministicRuleCatalog.Get(id).Category, "warning",
                message, file, span.StartLinePosition.Line + 1,
                span.StartLinePosition.Character + 1, null, verification));
        }
    }

    private static bool IsInsideExecutableLoop(SyntaxNode node)
    {
        foreach (var parent in node.Ancestors())
        {
            if (parent is Microsoft.CodeAnalysis.CSharp.Syntax.ForStatementSyntax
                or Microsoft.CodeAnalysis.CSharp.Syntax.ForEachStatementSyntax
                or Microsoft.CodeAnalysis.CSharp.Syntax.ForEachVariableStatementSyntax
                or Microsoft.CodeAnalysis.CSharp.Syntax.WhileStatementSyntax
                or Microsoft.CodeAnalysis.CSharp.Syntax.DoStatementSyntax)
                return true;
            if (parent is MethodDeclarationSyntax or LocalFunctionStatementSyntax
                or AnonymousFunctionExpressionSyntax or ConstructorDeclarationSyntax)
                return false;
        }
        return false;
    }
}

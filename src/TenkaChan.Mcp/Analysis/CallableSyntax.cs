using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace TenkaChan.Mcp.Analysis;

public sealed record SourceCallable(SyntaxNode Syntax, IMethodSymbol Symbol);

/// <summary>Source-owned callable boundaries. Never attributes a deferred body to its lexical parent.</summary>
public static class CallableSyntax
{
    public static IEnumerable<SourceCallable> Enumerate(SyntaxNode root, SemanticModel model,
        CancellationToken cancellationToken = default)
    {
        foreach (var node in root.DescendantNodes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            IMethodSymbol? symbol = node switch
            {
                BaseMethodDeclarationSyntax method => model.GetDeclaredSymbol(method, cancellationToken) as IMethodSymbol,
                AccessorDeclarationSyntax accessor => model.GetDeclaredSymbol(accessor, cancellationToken),
                LocalFunctionStatementSyntax local => model.GetDeclaredSymbol(local, cancellationToken),
                AnonymousFunctionExpressionSyntax lambda => (model.GetOperation(lambda, cancellationToken) as IAnonymousFunctionOperation)?.Symbol,
                PropertyDeclarationSyntax { ExpressionBody: not null } property => model.GetDeclaredSymbol(property, cancellationToken)?.GetMethod,
                IndexerDeclarationSyntax { ExpressionBody: not null } indexer => model.GetDeclaredSymbol(indexer, cancellationToken)?.GetMethod,
                _ => null
            };
            if (symbol is not null) yield return new(node, symbol);
        }
    }

    public static bool IsBoundary(SyntaxNode node) => node is BaseMethodDeclarationSyntax or
        AccessorDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax or
        PropertyDeclarationSyntax or IndexerDeclarationSyntax;

    public static IEnumerable<SyntaxNode> OwnNodes(SyntaxNode node) =>
        node.DescendantNodes(descendIntoChildren: child => child == node || !IsBoundary(child));

    public static SyntaxNode? Body(SyntaxNode node) => node switch
    {
        BaseMethodDeclarationSyntax n => (SyntaxNode?)n.Body ?? n.ExpressionBody?.Expression,
        AccessorDeclarationSyntax n => (SyntaxNode?)n.Body ?? n.ExpressionBody?.Expression,
        LocalFunctionStatementSyntax n => (SyntaxNode?)n.Body ?? n.ExpressionBody?.Expression,
        LambdaExpressionSyntax n => n.Body,
        AnonymousMethodExpressionSyntax n => n.Block,
        PropertyDeclarationSyntax n => n.ExpressionBody?.Expression,
        IndexerDeclarationSyntax n => n.ExpressionBody?.Expression,
        _ => null
    };

    public static bool CanCorrelateByMetadataName(IMethodSymbol symbol) =>
        symbol.MethodKind is not (MethodKind.AnonymousFunction or MethodKind.LocalFunction);

    public static ControlFlowGraph? TryCreateGraph(SyntaxNode node, SemanticModel model,
        CancellationToken cancellationToken = default)
    {
        // Never climb from a lambda/local-function operation into its enclosing method:
        // that would return the parent's CFG under the nested callable's identity.
        if (node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax) return null;
        try { return ControlFlowGraph.Create(node, model, cancellationToken); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; }
    }
}

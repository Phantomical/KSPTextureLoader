using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace KSPTextureLoader.Analyzers;

[ExportCodeFixProvider(
    LanguageNames.CSharp,
    Name = nameof(ModifiedCapturedVariableCodeFixProvider)
)]
[Shared]
public sealed class ModifiedCapturedVariableCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(ModifiedCapturedVariableAnalyzer.DiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var diagnostic = context.Diagnostics.First();

        if (
            !diagnostic.Properties.TryGetValue(
                ModifiedCapturedVariableAnalyzer.CapturedVariableProperty,
                out var variableName
            )
            || variableName == null
        )
            return;

        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken);
        if (root == null)
            return;

        var closureNode = root.FindNode(diagnostic.Location.SourceSpan);
        if (closureNode is not AnonymousFunctionExpressionSyntax closure)
        {
            // The diagnostic location may be on a child node; walk up
            closure = closureNode
                .AncestorsAndSelf()
                .OfType<AnonymousFunctionExpressionSyntax>()
                .FirstOrDefault();
            if (closure == null)
                return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                $"Introduce local copy of '{variableName}' before closure",
                ct => IntroduceLocalCopyAsync(context.Document, closure, variableName, ct),
                equivalenceKey: $"IntroduceLocalCopy_{variableName}"
            ),
            diagnostic
        );
    }

    private static async Task<Document> IntroduceLocalCopyAsync(
        Document document,
        AnonymousFunctionExpressionSyntax closure,
        string variableName,
        CancellationToken cancellationToken
    )
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
            return document;

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null)
            return document;

        // Find the statement containing the closure
        var containingStatement = closure.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
        if (containingStatement == null)
            return document;

        // Generate a unique local name
        var localName = GenerateUniqueName(variableName, semanticModel, closure, cancellationToken);

        // Create the local copy declaration: var {localName} = {variableName};
        var localDeclaration = SyntaxFactory
            .LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.IdentifierName("var"),
                    SyntaxFactory.SeparatedList(
                        new[]
                        {
                            SyntaxFactory
                                .VariableDeclarator(localName)
                                .WithInitializer(
                                    SyntaxFactory.EqualsValueClause(
                                        SyntaxFactory.IdentifierName(variableName)
                                    )
                                ),
                        }
                    )
                )
            )
            .NormalizeWhitespace()
            .WithLeadingTrivia(containingStatement.GetLeadingTrivia())
            .WithTrailingTrivia(SyntaxFactory.ElasticLineFeed);

        // Replace references to the variable inside the closure with the local copy name
        var newClosure = ReplaceIdentifiersInClosure(
            closure,
            variableName,
            localName,
            semanticModel,
            cancellationToken
        );

        // Build the new root: insert the local declaration before the containing statement,
        // and replace the closure
        var newRoot = root.ReplaceNode(closure, newClosure);

        // Re-find the containing statement in the new tree
        var newContainingStatement = newRoot
            .FindNode(containingStatement.Span)
            .AncestorsAndSelf()
            .OfType<StatementSyntax>()
            .FirstOrDefault();
        if (newContainingStatement == null)
            return document.WithSyntaxRoot(newRoot);

        // Insert the local declaration before the statement
        if (newContainingStatement.Parent is BlockSyntax block)
        {
            var index = block.Statements.IndexOf(newContainingStatement);
            if (index >= 0)
            {
                var newStatements = block.Statements.Insert(index, localDeclaration);
                var newBlock = block.WithStatements(newStatements);
                newRoot = newRoot.ReplaceNode(block, newBlock);
            }
        }

        return document.WithSyntaxRoot(newRoot);
    }

    private static AnonymousFunctionExpressionSyntax ReplaceIdentifiersInClosure(
        AnonymousFunctionExpressionSyntax closure,
        string originalName,
        string newName,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        var identifiersToReplace = closure
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Where(id =>
            {
                if (id.Identifier.ValueText != originalName)
                    return false;

                var symbol = semanticModel.GetSymbolInfo(id, cancellationToken).Symbol;
                return symbol is ILocalSymbol or IParameterSymbol;
            })
            .ToList();

        if (identifiersToReplace.Count == 0)
            return closure;

        return closure.ReplaceNodes(
            identifiersToReplace,
            (original, _) =>
                SyntaxFactory
                    .IdentifierName(newName)
                    .WithLeadingTrivia(original.GetLeadingTrivia())
                    .WithTrailingTrivia(original.GetTrailingTrivia())
        );
    }

    private static string GenerateUniqueName(
        string baseName,
        SemanticModel semanticModel,
        SyntaxNode location,
        CancellationToken cancellationToken
    )
    {
        var candidate = baseName + "Copy";
        var existingSymbols = semanticModel.LookupSymbols(location.SpanStart);

        var suffix = 0;
        var name = candidate;
        while (existingSymbols.Any(s => s.Name == name))
        {
            suffix++;
            name = candidate + suffix;
        }
        return name;
    }
}

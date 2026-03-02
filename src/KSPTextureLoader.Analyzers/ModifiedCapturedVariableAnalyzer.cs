using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace KSPTextureLoader.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ModifiedCapturedVariableAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "KTLA0001";
    public const string CapturedVariableProperty = "CapturedVariable";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Access to modified captured variable",
        "Variable '{0}' is captured by this closure and reassigned afterward",
        "Correctness",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    private static readonly ImmutableArray<SyntaxKind> MethodLikeKinds = ImmutableArray.Create(
        SyntaxKind.MethodDeclaration,
        SyntaxKind.ConstructorDeclaration,
        SyntaxKind.DestructorDeclaration,
        SyntaxKind.OperatorDeclaration,
        SyntaxKind.ConversionOperatorDeclaration,
        SyntaxKind.GetAccessorDeclaration,
        SyntaxKind.SetAccessorDeclaration,
        SyntaxKind.AddAccessorDeclaration,
        SyntaxKind.RemoveAccessorDeclaration,
        SyntaxKind.LocalFunctionStatement
    );

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeMethodBody, MethodLikeKinds.ToArray());
    }

    private static void AnalyzeMethodBody(SyntaxNodeAnalysisContext context)
    {
        var body = GetBody(context.Node);
        if (body == null)
            return;

        // Early bail: no closures in this method
        var closures = body.DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>().ToList();
        if (closures.Count == 0)
            return;

        var semanticModel = context.SemanticModel;

        // For each closure, find captured variables (locals/parameters declared outside the closure)
        // Map: captured symbol -> list of closures that capture it
        var capturedBy = new Dictionary<ISymbol, List<AnonymousFunctionExpressionSyntax>>(
            SymbolEqualityComparer.Default
        );

        foreach (var closure in closures)
        {
            // Static lambdas cannot capture
            if (IsStaticLambda(closure))
                continue;

            var closureSpan = closure.Span;

            foreach (var identifier in closure.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                var symbolInfo = semanticModel.GetSymbolInfo(identifier, context.CancellationToken);
                var symbol = symbolInfo.Symbol;
                if (symbol == null)
                    continue;

                if (symbol is not ILocalSymbol and not IParameterSymbol)
                    continue;

                // Check if declared outside the closure
                var declaringSyntax = symbol.DeclaringSyntaxReferences.FirstOrDefault();
                if (declaringSyntax == null)
                    continue;

                var declSpan = declaringSyntax.Span;
                if (closureSpan.Contains(declSpan))
                    continue; // Declared inside the closure — not captured

                if (!capturedBy.TryGetValue(symbol, out var list))
                {
                    list = new List<AnonymousFunctionExpressionSyntax>();
                    capturedBy[symbol] = list;
                }

                if (!list.Contains(closure))
                    list.Add(closure);
            }
        }

        if (capturedBy.Count == 0)
            return;

        // Collect all assignments in the body (outside closures are checked later per-closure)
        var assignments = CollectAssignments(body, semanticModel, context.CancellationToken);

        // For each captured variable, check if there's a post-closure assignment outside the closure
        foreach (var kvp in capturedBy)
        {
            var symbol = kvp.Key;
            var capturingClosures = kvp.Value;

            if (
                !assignments.TryGetValue(symbol, out var assignmentLocations)
                || assignmentLocations.Count == 0
            )
                continue;

            foreach (var closure in capturingClosures)
            {
                var closureStart = closure.SpanStart;
                var closureSpan = closure.Span;

                foreach (var assignment in assignmentLocations)
                {
                    // Assignment must not be inside the closure itself
                    if (closureSpan.Contains(assignment.Span))
                        continue;

                    // Assignment must be in the same enclosing scope as the closure.
                    // Both the closure and the assignment should share the same
                    // nearest enclosing anonymous function (or both be at the method level).
                    if (!IsInSameScope(assignment, closure))
                        continue;

                    // Check if the assignment logically executes after the closure.
                    // For-loop incrementors execute after the loop body, so treat them
                    // as being after any closure inside the loop body.
                    var isAfterClosure =
                        assignment.SpanStart > closureStart
                        || IsForLoopIncrementorAffectingClosure(assignment, closure);

                    if (!isAfterClosure)
                        continue;

                    var properties = ImmutableDictionary.CreateBuilder<string, string?>();
                    properties.Add(CapturedVariableProperty, symbol.Name);

                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            Rule,
                            closure.GetLocation(),
                            properties.ToImmutable(),
                            symbol.Name
                        )
                    );

                    // Only report once per closure per variable
                    break;
                }
            }
        }
    }

    private static bool IsForLoopIncrementorAffectingClosure(
        SyntaxNode assignment,
        AnonymousFunctionExpressionSyntax closure
    )
    {
        // Check if the assignment is inside a for-loop incrementor,
        // and the closure is inside the same for-loop's body.
        // The incrementor executes after the body each iteration.
        for (var node = assignment.Parent; node != null; node = node.Parent)
        {
            if (node is ForStatementSyntax forStatement)
            {
                // Check if the assignment is in the incrementors
                foreach (var incrementor in forStatement.Incrementors)
                {
                    if (incrementor.Span.Contains(assignment.Span))
                    {
                        // Check if the closure is in the for statement's body
                        if (
                            forStatement.Statement != null
                            && forStatement.Statement.Span.Contains(closure.Span)
                        )
                            return true;
                    }
                }
                break;
            }
        }
        return false;
    }

    private static bool IsInSameScope(
        SyntaxNode assignment,
        AnonymousFunctionExpressionSyntax closure
    )
    {
        // Find the nearest enclosing anonymous function for both nodes.
        // If they share the same enclosing scope, the assignment is relevant.
        var closureScope = GetEnclosingAnonymousFunction(closure);
        var assignmentScope = GetEnclosingAnonymousFunction(assignment);
        return ReferenceEquals(closureScope, assignmentScope);
    }

    private static AnonymousFunctionExpressionSyntax? GetEnclosingAnonymousFunction(SyntaxNode node)
    {
        for (var current = node.Parent; current != null; current = current.Parent)
        {
            if (current is AnonymousFunctionExpressionSyntax anon)
                return anon;
        }
        return null;
    }

    private static Dictionary<ISymbol, List<SyntaxNode>> CollectAssignments(
        SyntaxNode body,
        SemanticModel semanticModel,
        System.Threading.CancellationToken cancellationToken
    )
    {
        var result = new Dictionary<ISymbol, List<SyntaxNode>>(SymbolEqualityComparer.Default);

        foreach (var node in body.DescendantNodes())
        {
            ISymbol? symbol = null;
            SyntaxNode? assignmentNode = null;

            switch (node)
            {
                case AssignmentExpressionSyntax assignment:
                    symbol = GetAssignedSymbol(assignment.Left, semanticModel, cancellationToken);
                    assignmentNode = assignment;

                    // Skip if this is the variable's declaration initializer
                    // (e.g. var x = value; — the EqualsValueClause parent indicates initialization)
                    if (
                        assignment.Parent is EqualsValueClauseSyntax
                        || IsDeclarationInitializer(assignment, symbol)
                    )
                        continue;
                    break;

                case PrefixUnaryExpressionSyntax prefix
                    when prefix.IsKind(SyntaxKind.PreIncrementExpression)
                        || prefix.IsKind(SyntaxKind.PreDecrementExpression):
                    symbol = GetAssignedSymbol(prefix.Operand, semanticModel, cancellationToken);
                    assignmentNode = prefix;
                    break;

                case PostfixUnaryExpressionSyntax postfix
                    when postfix.IsKind(SyntaxKind.PostIncrementExpression)
                        || postfix.IsKind(SyntaxKind.PostDecrementExpression):
                    symbol = GetAssignedSymbol(postfix.Operand, semanticModel, cancellationToken);
                    assignmentNode = postfix;
                    break;

                case ArgumentSyntax argument
                    when argument.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword):
                    symbol = GetAssignedSymbol(
                        argument.Expression,
                        semanticModel,
                        cancellationToken
                    );
                    assignmentNode = argument;
                    break;
            }

            if (symbol == null || assignmentNode == null)
                continue;
            if (symbol is not ILocalSymbol and not IParameterSymbol)
                continue;

            // Skip foreach iteration variables — they get a fresh copy per iteration in C# 5+
            if (IsForEachVariable(symbol))
                continue;

            if (!result.TryGetValue(symbol, out var list))
            {
                list = new List<SyntaxNode>();
                result[symbol] = list;
            }
            list.Add(assignmentNode);
        }

        return result;
    }

    private static bool IsDeclarationInitializer(
        AssignmentExpressionSyntax assignment,
        ISymbol? symbol
    )
    {
        // For simple assignments like `var x = ...` the initializer is an EqualsValueClause,
        // not an AssignmentExpression, so this case shouldn't normally occur.
        // But guard against edge cases.
        return false;
    }

    private static bool IsForEachVariable(ISymbol symbol)
    {
        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            var syntax = syntaxRef.GetSyntax();
            if (syntax.Parent is ForEachStatementSyntax)
                return true;
        }
        return false;
    }

    private static ISymbol? GetAssignedSymbol(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        System.Threading.CancellationToken cancellationToken
    )
    {
        if (expression is IdentifierNameSyntax)
        {
            return semanticModel.GetSymbolInfo(expression, cancellationToken).Symbol;
        }
        return null;
    }

    private static bool IsStaticLambda(AnonymousFunctionExpressionSyntax closure)
    {
        return closure.Modifiers.Any(SyntaxKind.StaticKeyword);
    }

    private static SyntaxNode? GetBody(SyntaxNode node)
    {
        return node switch
        {
            MethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
            ConstructorDeclarationSyntax c => (SyntaxNode?)c.Body ?? c.ExpressionBody,
            DestructorDeclarationSyntax d => (SyntaxNode?)d.Body ?? d.ExpressionBody,
            OperatorDeclarationSyntax o => (SyntaxNode?)o.Body ?? o.ExpressionBody,
            ConversionOperatorDeclarationSyntax co => (SyntaxNode?)co.Body ?? co.ExpressionBody,
            AccessorDeclarationSyntax a => (SyntaxNode?)a.Body ?? a.ExpressionBody,
            LocalFunctionStatementSyntax lf => (SyntaxNode?)lf.Body ?? lf.ExpressionBody,
            _ => null,
        };
    }
}

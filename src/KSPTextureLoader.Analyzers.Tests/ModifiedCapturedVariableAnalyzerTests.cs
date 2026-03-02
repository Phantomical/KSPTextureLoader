using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace KSPTextureLoader.Analyzers.Tests;

using AnalyzerTest = CSharpAnalyzerTest<ModifiedCapturedVariableAnalyzer, DefaultVerifier>;
using CodeFixTest = CSharpCodeFixTest<
    ModifiedCapturedVariableAnalyzer,
    ModifiedCapturedVariableCodeFixProvider,
    DefaultVerifier
>;

public class ModifiedCapturedVariableAnalyzerTests
{
    private static DiagnosticResult Diagnostic(int location) =>
        new DiagnosticResult(
            ModifiedCapturedVariableAnalyzer.DiagnosticId,
            Microsoft.CodeAnalysis.DiagnosticSeverity.Warning
        ).WithLocation(location);

    #region Should produce diagnostic

    [Fact]
    public async Task BasicReassignmentAfterLambda()
    {
        var source = """
            using System;
            class C
            {
                void M()
                {
                    int x = 1;
                    Action a = {|#0:() => Console.WriteLine(x)|};
                    x = 2;
                }
            }
            """;

        var test = new AnalyzerTest { TestCode = source };
        test.ExpectedDiagnostics.Add(Diagnostic(0).WithArguments("x"));
        await test.RunAsync();
    }

    [Fact]
    public async Task AsyncMethodWithAwaitInClosure()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            class C
            {
                async Task M(Task<int> dataTask)
                {
                    Action a = {|#0:() => { var t = dataTask; }|};
                    dataTask = Task.FromResult(0);
                }
            }
            """;

        var test = new AnalyzerTest { TestCode = source };
        test.ExpectedDiagnostics.Add(Diagnostic(0).WithArguments("dataTask"));
        await test.RunAsync();
    }

    [Fact]
    public async Task ForLoopVariableCapturedInsideLoopBody()
    {
        var source = """
            using System;
            using System.Collections.Generic;
            class C
            {
                void M()
                {
                    var actions = new List<Action>();
                    for (int i = 0; i < 10; i++)
                    {
                        actions.Add({|#0:() => Console.WriteLine(i)|});
                    }
                }
            }
            """;

        var test = new AnalyzerTest { TestCode = source };
        test.ExpectedDiagnostics.Add(Diagnostic(0).WithArguments("i"));
        await test.RunAsync();
    }

    [Fact]
    public async Task CompoundAssignmentAfterClosure()
    {
        var source = """
            using System;
            class C
            {
                void M()
                {
                    int x = 1;
                    Action a = {|#0:() => Console.WriteLine(x)|};
                    x += 2;
                }
            }
            """;

        var test = new AnalyzerTest { TestCode = source };
        test.ExpectedDiagnostics.Add(Diagnostic(0).WithArguments("x"));
        await test.RunAsync();
    }

    [Fact]
    public async Task IncrementAfterClosure()
    {
        var source = """
            using System;
            class C
            {
                void M()
                {
                    int x = 1;
                    Action a = {|#0:() => Console.WriteLine(x)|};
                    x++;
                }
            }
            """;

        var test = new AnalyzerTest { TestCode = source };
        test.ExpectedDiagnostics.Add(Diagnostic(0).WithArguments("x"));
        await test.RunAsync();
    }

    [Fact]
    public async Task MultipleClosuresCapturingSameVariableWithAssignmentBetween()
    {
        var source = """
            using System;
            class C
            {
                void M()
                {
                    int x = 1;
                    Action a = {|#0:() => Console.WriteLine(x)|};
                    x = 2;
                    Action b = () => Console.WriteLine(x);
                }
            }
            """;

        // Only the first closure gets a diagnostic — the assignment is before the second closure
        var test = new AnalyzerTest { TestCode = source };
        test.ExpectedDiagnostics.Add(Diagnostic(0).WithArguments("x"));
        await test.RunAsync();
    }

    [Fact]
    public async Task OutArgumentAfterClosure()
    {
        var source = """
            using System;
            class C
            {
                void M()
                {
                    int x = 1;
                    Action a = {|#0:() => Console.WriteLine(x)|};
                    TryGet(out x);
                }
                void TryGet(out int value) { value = 0; }
            }
            """;

        var test = new AnalyzerTest { TestCode = source };
        test.ExpectedDiagnostics.Add(Diagnostic(0).WithArguments("x"));
        await test.RunAsync();
    }

    [Fact]
    public async Task NestedClosureReassignmentInsideOuterClosure()
    {
        var source = """
            using System;
            class C
            {
                void M(int x)
                {
                    Action outer = () =>
                    {
                        Action inner = {|#0:() => Console.WriteLine(x)|};
                        x = 2;
                    };
                }
            }
            """;

        var test = new AnalyzerTest { TestCode = source };
        test.ExpectedDiagnostics.Add(Diagnostic(0).WithArguments("x"));
        await test.RunAsync();
    }

    #endregion

    #region Should NOT produce diagnostic

    [Fact]
    public async Task ReassignmentBeforeClosure()
    {
        var source = """
            using System;
            class C
            {
                void M()
                {
                    int x = 1;
                    x = 2;
                    Action a = () => Console.WriteLine(x);
                }
            }
            """;

        var test = new AnalyzerTest { TestCode = source };
        await test.RunAsync();
    }

    [Fact]
    public async Task VariableDeclaredInsideClosure()
    {
        var source = """
            using System;
            class C
            {
                void M()
                {
                    Action a = () =>
                    {
                        int x = 1;
                        x = 2;
                        Console.WriteLine(x);
                    };
                }
            }
            """;

        var test = new AnalyzerTest { TestCode = source };
        await test.RunAsync();
    }

    [Fact]
    public async Task ForEachLoopVariable()
    {
        var source = """
            using System;
            using System.Collections.Generic;
            class C
            {
                void M()
                {
                    var actions = new List<Action>();
                    foreach (var item in new[] { 1, 2, 3 })
                    {
                        actions.Add(() => Console.WriteLine(item));
                    }
                }
            }
            """;

        var test = new AnalyzerTest { TestCode = source };
        await test.RunAsync();
    }

    [Fact]
    public async Task LocalCopyPattern()
    {
        var source = """
            using System;
            class C
            {
                void M()
                {
                    int x = 1;
                    var copy = x;
                    Action a = () => Console.WriteLine(copy);
                    x = 2;
                }
            }
            """;

        var test = new AnalyzerTest { TestCode = source };
        await test.RunAsync();
    }

    [Fact]
    public async Task StaticLambda()
    {
        var source = """
            using System;
            class C
            {
                void M()
                {
                    int x = 1;
                    Action a = static () => Console.WriteLine(42);
                    x = 2;
                }
            }
            """;

        var test = new AnalyzerTest { TestCode = source };
        await test.RunAsync();
    }

    [Fact]
    public async Task DifferentVariableModified()
    {
        var source = """
            using System;
            class C
            {
                void M()
                {
                    int x = 1;
                    int y = 1;
                    Action a = () => Console.WriteLine(x);
                    y = 2;
                }
            }
            """;

        var test = new AnalyzerTest { TestCode = source };
        await test.RunAsync();
    }

    #endregion

    #region Code fix tests

    [Fact]
    public async Task CodeFix_BasicIntroduceLocalCopy()
    {
        var source = """
            using System;
            class C
            {
                void M()
                {
                    int x = 1;
                    Action a = {|#0:() => Console.WriteLine(x)|};
                    x = 2;
                }
            }
            """;

        var fixedSource = """
            using System;
            class C
            {
                void M()
                {
                    int x = 1;
                    var xCopy = x;
                    Action a = () => Console.WriteLine(xCopy);
                    x = 2;
                }
            }
            """;

        var test = new CodeFixTest { TestCode = source, FixedCode = fixedSource };
        test.ExpectedDiagnostics.Add(Diagnostic(0).WithArguments("x"));
        await test.RunAsync();
    }

    [Fact]
    public async Task CodeFix_MultipleReferencesInClosure()
    {
        var source = """
            using System;
            class C
            {
                void M()
                {
                    int x = 1;
                    Action a = {|#0:() =>
                    {
                        Console.WriteLine(x);
                        Console.WriteLine(x + 1);
                    }|};
                    x = 2;
                }
            }
            """;

        var fixedSource = """
            using System;
            class C
            {
                void M()
                {
                    int x = 1;
                    var xCopy = x;
                    Action a = () =>
                    {
                        Console.WriteLine(xCopy);
                        Console.WriteLine(xCopy + 1);
                    };
                    x = 2;
                }
            }
            """;

        var test = new CodeFixTest { TestCode = source, FixedCode = fixedSource };
        test.ExpectedDiagnostics.Add(Diagnostic(0).WithArguments("x"));
        await test.RunAsync();
    }

    #endregion
}

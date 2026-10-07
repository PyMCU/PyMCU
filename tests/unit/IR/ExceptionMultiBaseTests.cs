using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `class F(ValueError, OSError)`: every base contributes an IS-A path, so `except OSError`
/// must catch an `F` raised anywhere in the program. The base scan used to stop at the
/// first base that resolved at all -- ValueError -- which records no OSError-subtree edge,
/// so the class got no parent and `except OSError` emitted a single compare that `F`'s
/// code never satisfies: the raise fell through to the unhandled path.
/// </summary>
public class ExceptionMultiBaseTests
{
    private const string Prelude =
        "from pymcu.types import uint8\n" +
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static List<Instruction> Fn(ProgramIR ir, string name) =>
        ir.Functions.Single(f => f.Name == name).Body;

    private static int ErrorCodeCompares(List<Instruction> fn) =>
        fn.Count(i => i is Binary { Op: PyMCU.IR.BinaryOp.Equal, Src1: Variable { Name: var n } }
                      && n.StartsWith("__exn_", StringComparison.Ordinal));

    [Fact]
    public void ASecondBaseInsideTheOSErrorSubtreeStillCatches()
    {
        // The first resolvable base (ValueError) is outside the subtree; the second
        // (OSError) carries the edge. `except OSError` must compare against both the
        // OSError code and F's own code -- a single compare only matches OSError
        // exactly and lets `raise F(5)` escape.
        var main = Fn(Gen(
            "class F(ValueError, OSError):\n" +
            "    pass\n" +
            "try:\n" +
            "    raise F(5)\n" +
            "except OSError:\n" +
            "    print(\"caught\")\n"), "main");

        ErrorCodeCompares(main).Should().BeGreaterOrEqualTo(2,
            because: "the handler accepts OSError itself and the raised subclass F");
    }

    [Fact]
    public void AFirstBaseInsideTheSubtreeStillCatches()
    {
        // Order flipped: the OSError-subtree base comes first and must record the edge
        // exactly as before -- a later non-subtree base adds nothing but must not cost it.
        var main = Fn(Gen(
            "class F(OSError, ValueError):\n" +
            "    pass\n" +
            "try:\n" +
            "    raise F(5)\n" +
            "except OSError:\n" +
            "    print(\"caught\")\n"), "main");

        ErrorCodeCompares(main).Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public void ASubtleTreeBaseThroughAnotherUserClassCatches()
    {
        // `class G(F)` where F already carries an OSError edge: the scan looks at the
        // parent MAP, so a base whose own edge was just recorded must chain through.
        var main = Fn(Gen(
            "class F(OSError):\n" +
            "    pass\n" +
            "class G(F, ValueError):\n" +
            "    pass\n" +
            "try:\n" +
            "    raise G(5)\n" +
            "except OSError:\n" +
            "    print(\"caught\")\n"), "main");

        ErrorCodeCompares(main).Should().BeGreaterOrEqualTo(2,
            because: "G descends from F, which descends from OSError");
    }

    [Fact]
    public void TwoBasesInsideTheSubtreeEachRecordAnEdge()
    {
        // `class F(A, B)` where BOTH bases descend from OSError: the scan used to stop
        // at the first recorded edge (F -> A), so `except B` never saw F and the raise
        // landed in the catch-all instead. Every in-subtree base carries an edge now,
        // so `except B` compares against B's code and F's alike.
        var main = Fn(Gen(
            "class A(OSError):\n" +
            "    pass\n" +
            "class B(OSError):\n" +
            "    pass\n" +
            "class F(A, B):\n" +
            "    pass\n" +
            "try:\n" +
            "    raise F(5)\n" +
            "except B:\n" +
            "    print(\"caught\")\n" +
            "except Exception:\n" +
            "    print(\"wrong\")\n"), "main");

        ErrorCodeCompares(main).Should().BeGreaterOrEqualTo(2,
            because: "except B accepts B itself and the raised F that descends from it");
    }

    [Fact]
    public void ABaseListedBeforeItsOwnSubclassRefuses()
    {
        // `class F(A, B)` over `B(A)`: the source order demands A before B while B's
        // own linearization demands B before A, so no C3 merge exists. CPython raises
        // TypeError at the class definition; the edge table only records IS-A facts,
        // so this compiled and printed "ok".
        var act = () => Gen(
            "class A(OSError):\n" +
            "    pass\n" +
            "class B(A):\n" +
            "    pass\n" +
            "class F(A, B):\n" +
            "    pass\n");

        act.Should().Throw<PyMCU.Common.CompilerError>()
            .Which.Message.Should().Contain("method resolution order")
            .And.Contain("F");
    }

    [Fact]
    public void AConsistentDiamondStillCompiles()
    {
        // `class F(B, C)` over `B(A)` and `C(A)`: both orders agree -- B and C each
        // come before A, and the base list only fixes B before C. The merge is
        // [F, B, C, A, OSError, ...] and the edges keep `except A` catching a raised F.
        var main = Fn(Gen(
            "class A(OSError):\n" +
            "    pass\n" +
            "class B(A):\n" +
            "    pass\n" +
            "class C(A):\n" +
            "    pass\n" +
            "class F(B, C):\n" +
            "    pass\n" +
            "try:\n" +
            "    raise F(5)\n" +
            "except A:\n" +
            "    print(\"caught\")\n"), "main");

        ErrorCodeCompares(main).Should().BeGreaterOrEqualTo(2,
            because: "F descends from B and C, which both descend from A");
    }

    [Fact]
    public void AParentAfterABuiltinSubclassRefuses()
    {
        // `class F(OSError, TimeoutError)`: TimeoutError is a builtin OSError
        // subclass, so the base order contradicts the seeded edge the same way the
        // user-defined version does.
        var act = () => Gen(
            "class F(OSError, TimeoutError):\n" +
            "    pass\n");

        act.Should().Throw<PyMCU.Common.CompilerError>()
            .Which.Message.Should().Contain("method resolution order");
    }

    [Fact]
    public void AnArithmeticErrorBeforeItsOwnSubclassRefuses()
    {
        // `class F(ArithmeticError, ZeroDivisionError)`: the C3 model only knew the
        // OSError-subtree edge, so this compiled where CPython raises TypeError --
        // ZeroDivisionError descends from ArithmeticError and must come first.
        var act = () => Gen(
            "class F(ArithmeticError, ZeroDivisionError):\n" +
            "    pass\n");

        act.Should().Throw<PyMCU.Common.CompilerError>()
            .Which.Message.Should().Contain("method resolution order");
    }

    [Fact]
    public void ASubclassBeforeItsBuiltinParentStillCompiles()
    {
        // The same pair in the consistent order: ZeroDivisionError first, then
        // ArithmeticError. CPython accepts it and so must the merge.
        Gen(
            "class F(ZeroDivisionError, ArithmeticError):\n" +
            "    pass\n");
    }

    [Fact]
    public void AnExceptionReboundAsItsOwnSubclassCompiles()
    {
        // `class A(Exception)` then `class A(A)`: the base resolves to the binding the
        // name held BEFORE this class registered, which is the rebinding CPython
        // accepts. Registering the new code first resolved the base to the class being
        // defined and refused it as self-inheritance.
        Gen(
            "class A(Exception):\n" +
            "    pass\n" +
            "class A(A):\n" +
            "    pass\n");
    }

    [Fact]
    public void AMixinListedAfterItsOwnSubclassRefuses()
    {
        // `class E(Exception, M, N)` over `N(M)`: a fresh pseudo-node per occurrence
        // hid the parent/subclass edge between the mix-ins, so the order contradiction
        // compiled. CPython raises TypeError at the class definition.
        var act = () => Gen(
            "class M(object):\n" +
            "    pass\n" +
            "class N(M):\n" +
            "    pass\n" +
            "class E(Exception, M, N):\n" +
            "    pass\n");

        act.Should().Throw<PyMCU.Common.CompilerError>()
            .Which.Message.Should().Contain("method resolution order");
    }

    [Fact]
    public void ARepeatedMixinBaseRefuses()
    {
        // The same non-exception base twice is the duplicate CPython names. One node
        // per name is what lets the check see the repeat at all.
        var act = () => Gen(
            "class M(object):\n" +
            "    pass\n" +
            "class E(Exception, M, M):\n" +
            "    pass\n");

        act.Should().Throw<PyMCU.Common.CompilerError>()
            .Which.Message.Should().Contain("repeats base class");
    }

    [Fact]
    public void AConsistentMixinOrderStillCompiles()
    {
        // `class E(Exception, N, M)` over `N(M)`: the subclass precedes its parent, the
        // base list adds no contradiction, and the mix-in ordering is accepted.
        Gen(
            "class M(object):\n" +
            "    pass\n" +
            "class N(M):\n" +
            "    pass\n" +
            "class E(Exception, N, M):\n" +
            "    pass\n");
    }

    [Fact]
    public void ANameReboundAsItsOwnSubclassMixesIntoAnException()
    {
        // `class M(object)` then `class M(M)` then `class E(Exception, M)`: the base
        // resolves to the binding the name held BEFORE this def -- the previous M, so
        // the second M extends it and the merge sees a chain, not a cycle. Name-keyed
        // pseudo-nodes resolved the base to the node being built and recursed until the
        // stack gave out.
        Gen(
            "class M(object):\n" +
            "    pass\n" +
            "class M(M):\n" +
            "    pass\n" +
            "class E(Exception, M):\n" +
            "    pass\n");
    }

    [Fact]
    public void ANameReboundFromExceptionToPlainClassMixesInAsThePlainClass()
    {
        // `class A(Exception)` then `class A(object)` then `class E(Exception, A)`: the
        // rebinding retires the exception's binding, so the base is the ordinary class
        // and its lone `object` tail -- not the dead code -- is what C3 merges.
        Gen(
            "class A(Exception):\n" +
            "    pass\n" +
            "class A(object):\n" +
            "    pass\n" +
            "class E(Exception, A):\n" +
            "    pass\n");
    }

    [Fact]
    public void AUserClassNamedLikeAnUnmodelledBuiltinAncestorIsNotConfusedWithIt()
    {
        // `class LookupError` (PyMCU's table numbers no such builtin) then
        // `class E(LookupError, IndexError, Exception)`: the builtin edge
        // IndexError -> LookupError resolves to a fixed node, never to the user's
        // class, so no false parent/child order fires. Name-keyed resolution made
        // IndexError descend from the user's LookupError and refused the line.
        Gen(
            "class LookupError(object):\n" +
            "    pass\n" +
            "class E(LookupError, IndexError, Exception):\n" +
            "    pass\n");
    }

    [Fact]
    public void AnUndefinedExceptionBaseRefuses()
    {
        // `class E(Exception, Missing)`: the same definedness check the ordinary-class
        // path already defers now covers the exception path too -- CPython raises
        // NameError at class creation.
        var act = () => Gen(
            "class E(Exception, Missing):\n" +
            "    pass\n");

        act.Should().Throw<PyMCU.Common.CompilerError>()
            .Which.Message.Should().Contain("not defined");
    }
}

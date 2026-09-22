using FluentAssertions;
using PyMCU.IR;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// DeduplicateConstantCalls: call sites that invoke the same callee with
/// structurally identical constant arguments share one synthesized zero-arg
/// stub holding the constant-bearing call. The win guard requires the saved
/// marshal bytes to exceed the stub's own cost by more than 4 bytes, so the
/// signatures below are sized to cross it at the stated site counts.
/// </summary>
public class DeduplicateConstantCallsTests
{
    private static Function Callee(string name, bool canFail = false) =>
        new()
        {
            Name = name,
            Params = new List<string> { "a", "b" },
            Body = new List<Instruction> { new Return(new NoneVal()) },
            CanFail = canFail,
        };

    // wr(70000, 5): marshal = 8 + 2 = 10 bytes/site; stubCost = 14 (or 22 when
    // the callee can fail), so two sites net +6 and three sites of a CanFail
    // callee net +8 -- both clear the "> 4 bytes" bar.
    private static Call WideCall(string callee, int a = 70000, int b = 5) =>
        new(callee, new List<Val> { new Constant(a), new Constant(b) }, new NoneVal());

    private static List<Function> Stubs(ProgramIR prog) =>
        prog.Functions.Where(f => f.Name.StartsWith("__pymcu_callstub_")).ToList();

    [Fact]
    public void Dedupe_TwoIdenticalConstArgCalls_ShareOneStub()
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function
        {
            Name = "main",
            Body = new List<Instruction>
            {
                WideCall("wr"), WideCall("wr"), new Return(new NoneVal()),
            },
        });
        prog.Functions.Add(Callee("wr"));

        var optimized = Optimizer.Optimize(prog);

        var stubs = Stubs(optimized);
        stubs.Should().ContainSingle("two identical constant-arg calls collapse into one stub");
        var stub = stubs[0];
        stub.CanFail.Should().BeFalse("the callee cannot fail, so the stub is a plain subroutine");

        stub.Body.Should().HaveCount(2);
        var inner = stub.Body[0].Should().BeOfType<Call>().Subject;
        inner.FunctionName.Should().Be("wr");
        inner.Args.Should().Equal(new Constant(70000), new Constant(5));
        stub.Body[1].Should().BeOfType<Return>();

        var mainCalls = optimized.Functions.First(f => f.Name == "main").Body.OfType<Call>().ToList();
        mainCalls.Should().HaveCount(2);
        mainCalls.Should().OnlyContain(c =>
            c.FunctionName == stub.Name && c.Args.Count == 0 && c.Dst is NoneVal,
            "each site is a bare zero-arg call to the shared stub");
    }

    [Fact]
    public void Dedupe_CallWithNonConstantArg_IsNotDeduped()
    {
        Call withVar() => new("wr",
            new List<Val> { new Variable("main.x", DataType.UINT8), new Constant(5) },
            new NoneVal());
        var prog = new ProgramIR();
        prog.Functions.Add(new Function
        {
            Name = "main",
            Body = new List<Instruction>
            {
                withVar(), withVar(), withVar(), new Return(new NoneVal()),
            },
        });
        prog.Functions.Add(Callee("wr"));

        var optimized = Optimizer.Optimize(prog);

        Stubs(optimized).Should().BeEmpty("a runtime-decided argument disqualifies the whole site");
        optimized.Functions.First(f => f.Name == "main").Body.OfType<Call>()
            .Should().OnlyContain(c => c.FunctionName == "wr" && c.Args.Count == 2);
    }

    [Fact]
    public void Dedupe_CanFailCallee_StubForwardsTFlag()
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function
        {
            Name = "main",
            Body = new List<Instruction>
            {
                WideCall("wr"), WideCall("wr"), WideCall("wr"), new Return(new NoneVal()),
            },
        });
        prog.Functions.Add(Callee("wr", canFail: true));

        var optimized = Optimizer.Optimize(prog);

        var stub = Stubs(optimized).Should().ContainSingle().Subject;
        stub.CanFail.Should().BeTrue("the stub may propagate the callee's error");
        stub.Body[0].Should().BeOfType<Call>().Which.FunctionName.Should().Be("wr");
        var branch = stub.Body[1].Should().BeOfType<BranchOnError>().Subject;
        stub.Body[2].Should().BeOfType<Return>("the happy path returns normally");
        stub.Body[3].Should().BeOfType<Label>().Which.Name.Should().Be(branch.ErrorLabel);
        var raise = stub.Body[4].Should().BeOfType<SignalError>().Subject;
        raise.Code.Should().Be(new Constant(0),
            "code 0 keeps the callee's error value in R22 -- the same raise, re-signalled");
    }

    [Fact]
    public void Dedupe_DifferentArgs_ProduceDifferentStubs()
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function
        {
            Name = "main",
            Body = new List<Instruction>
            {
                WideCall("wr", b: 5), WideCall("wr", b: 5),
                WideCall("wr", b: 6), WideCall("wr", b: 6),
                new Return(new NoneVal()),
            },
        });
        prog.Functions.Add(Callee("wr"));

        var optimized = Optimizer.Optimize(prog);

        var stubs = Stubs(optimized);
        stubs.Should().HaveCount(2, "each distinct constant tuple gets its own stub");
        stubs.Select(s => ((Call)s.Body[0]).Args[1]).Cast<Constant>().Select(c => c.Value)
            .Should().BeEquivalentTo(new[] { 5, 6 });
    }

    [Fact]
    public void Dedupe_IdenticalCallsAcrossFunctions_ShareOneStub()
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function
        {
            Name = "main",
            Body = new List<Instruction>
            {
                new Call("draw", new List<Val>(), new NoneVal()),
                WideCall("wr"),
                new Return(new NoneVal()),
            },
        });
        prog.Functions.Add(new Function
        {
            Name = "draw",
            Body = new List<Instruction> { WideCall("wr"), new Return(new NoneVal()) },
        });
        prog.Functions.Add(Callee("wr"));

        var optimized = Optimizer.Optimize(prog);

        var stub = Stubs(optimized).Should().ContainSingle(
            "sites are grouped program-wide, not per function").Subject;
        optimized.Functions.Where(f => f.Name is "main" or "draw")
            .SelectMany(f => f.Body.OfType<Call>())
            .Count(c => c.FunctionName == stub.Name)
            .Should().Be(2, "one site in each of main and draw");
    }

    [Fact]
    public void Dedupe_SingleSite_NeverDeduped()
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function
        {
            Name = "main",
            Body = new List<Instruction> { WideCall("wr"), new Return(new NoneVal()) },
        });
        prog.Functions.Add(Callee("wr"));

        Stubs(Optimizer.Optimize(prog)).Should().BeEmpty(
            "a stub for one site adds an RCALL/RET pair for zero marshal savings");
    }
}

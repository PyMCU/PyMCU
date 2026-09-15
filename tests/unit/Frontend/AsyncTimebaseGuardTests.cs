using Xunit;
using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;

namespace PyMCU.UnitTests;

/// <summary>
/// asyncio.ticks() is the counter every `await` polls. On a target with no time base
/// the wait condition never clears, so the coroutine blocks forever. The stdlib guards
/// those targets with `raise CompileError` inside ticks(); these tests pin the guard to
/// the real lib/src/pymcu/asyncio.py so it cannot silently regress to `return 0`.
/// </summary>
public class AsyncTimebaseGuardTests
{
    private static readonly string AsyncioSource = File.ReadAllText(FindAsyncioPy());

    /// The stdlib module that ships, found through the shared repository-root walk so that a
    /// binary running outside the tree is answered by one sentence naming the cause rather than
    /// by an exception about a missing file (PyMCU#412).
    private static string FindAsyncioPy() =>
        RepositoryFile.Under("lib", "src", "pymcu", "asyncio.py");

    // Parses the stdlib asyncio module and folds it for one target, returning the
    // surviving body of ticks().
    private static List<Statement> TicksBodyFor(string chip, string arch)
    {
        var ast = new Parser(new Lexer(AsyncioSource).Tokenize()).ParseProgram();
        new ConditionalCompilator(new DeviceConfig { Chip = chip, Arch = arch, Frequency = 16000000 })
        {
            ModuleName = "pymcu.asyncio"
        }.Process(ast);

        var ticks = ast.Functions.Single(f => f.Name == "ticks");
        return ((Block)ticks.Body).Statements;
    }

    private static RaiseStmt? GuardIn(IEnumerable<Statement> body)
        => body.OfType<RaiseStmt>().FirstOrDefault(r => r.ErrorType == "CompileError");

    [Theory]
    [InlineData("pic16f877a", "pic14")]
    [InlineData("pic16f84a", "pic14")]
    [InlineData("pic10f200", "pic12")]
    [InlineData("ch32v003", "riscv")]
    public void NoTimebaseArchitecture_TicksRaisesCompileError(string chip, string arch)
    {
        var guard = GuardIn(TicksBodyFor(chip, arch));

        guard.Should().NotBeNull("async on {0} must fail to compile instead of hanging on the first await", chip);
        guard!.Message.Should().Contain("async needs a timebase");
        guard.Message.Should().Contain("not available on this architecture yet");
    }

    [Theory]
    [InlineData("attiny85")]
    [InlineData("attiny13")]
    [InlineData("attiny2313")]
    public void Attiny_TicksRaisesCompileError(string chip)
    {
        // ATtiny is arch "avr" but its timer HAL has no micros() -- millis/micros are
        // ATmega-only, so the stub would freeze the counter at 0.
        var guard = GuardIn(TicksBodyFor(chip, "avr"));

        guard.Should().NotBeNull("async on {0} must fail to compile instead of hanging on the first await", chip);
        guard!.Message.Should().Contain("not available on attiny yet");
    }

    [Theory]
    [InlineData("atmega328p", "avr")]
    [InlineData("atmega2560", "avr")]
    [InlineData("rp2040", "arm")]
    [InlineData("rp2350", "arm")]
    [InlineData("pic18f45k50", "pic18")]
    public void TargetWithTimebase_TicksHasNoGuard(string chip, string arch)
    {
        GuardIn(TicksBodyFor(chip, arch)).Should().BeNull(
            "{0} has a hardware time base, so async must keep compiling", chip);
    }
}

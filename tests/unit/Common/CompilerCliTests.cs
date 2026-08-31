using System.CommandLine;
using PyMCU.Common.Models;
using PyMCU.Infrastructure.Cli;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// What the compiler does with an argument it does not recognise, asserted on the EXIT CODE.
///
/// On the exit code and not on the text, because the text is not what failed: an unrecognised
/// argument was accepted, said nothing, and exited 0, so every harness that builds a `pymcuc`
/// command line by concatenation could feed it garbage and report green. A test that asserted
/// on a message would have passed throughout, since there was no message either way. Issue #237.
///
/// It does NOT join <see cref="ConsoleCaptureCollection"/>, and that is deliberate rather than
/// an oversight: the invocation writes into a <see cref="StringWriter"/> through
/// <see cref="InvocationConfiguration"/>, so it never swaps <c>Console.Error</c> and cannot
/// interleave with the fixtures that do.
/// </summary>
public class CompilerCliTests
{
    /// The runner is a stub returning 0: nothing here compiles, and a non-zero result can only
    /// have come from the parser. Otherwise a refusal and a compile failure look the same.
    private static int Run(params string[] args)
    {
        RootCommand root = CompilerCliBuilder.BuildRootCommand(_ => 0);
        var sink = new StringWriter();
        return root.Parse(args).Invoke(new InvocationConfiguration { Output = sink, Error = sink });
    }

    [Theory]
    // The two spellings from the report. The second is one argv entry, not two, because a shell
    // does not word-split a quoted variable, and it is the one that cost an hour.
    [InlineData("--totally-invented")]
    [InlineData("--board pico_w")]
    // A bare token. Before the fix this became an entry in the module search path, which is
    // worse than being ignored: imports started resolving out of a directory nobody named.
    [InlineData("extra")]
    // A short form, to pin that the refusal is not special-cased to `--`.
    [InlineData("-Z")]
    public void UnrecognisedArgumentAfterIncludePaths_ExitsNonZero(string extra)
    {
        // AFTER `-I`, which is where every real command line puts it and the only position in
        // which this was ever accepted. Before `-I` it was refused all along.
        int rc = Run("w.py", "--target", "rp2040", "-I", "lib", extra);
        Assert.NotEqual(0, rc);
    }

    [Theory]
    [InlineData("--totally-invented")]
    [InlineData("-Z")]
    public void UnrecognisedArgumentAfterConfigBits_ExitsNonZero(string extra)
    {
        // `--config` carried the same greedy arity as `--include` and swallowed independently
        // of it, so fixing one and not the other would have left half the hole open.
        int rc = Run("w.py", "--target", "rp2040", "-C", "FOO=1", extra, "-I", "lib");
        Assert.NotEqual(0, rc);
    }

    [Fact]
    public void RepeatedIncludeFlag_StillParses()
    {
        // The spelling every caller in this repo and in the backend repos uses. The fix removes
        // `-I a b`; it must not remove `-I a -I b`.
        Assert.Equal(0, Run("w.py", "--target", "rp2040", "-I", "lib", "-I", "other"));
    }

    [Fact]
    public void RepeatedConfigFlag_StillParses()
        => Assert.Equal(0, Run("w.py", "--target", "rp2040", "-C", "A=1", "-C", "B=2", "-I", "lib"));

    [Theory]
    [InlineData("--help")]
    [InlineData("--version")]
    public void HelpAndVersion_StillExitZero(string flag)
    {
        // Named in the issue as what a fix has to keep. They short-circuit before the argument
        // is required, so a fix that made the parser stricter could plausibly have caught them.
        Assert.Equal(0, Run(flag));
    }

    [Fact]
    public void AWellFormedCommandLine_StillExitsZero()
        => Assert.Equal(0, Run("w.py", "--target", "rp2040", "--board", "pico_w", "-I", "lib"));
}

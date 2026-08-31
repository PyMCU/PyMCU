/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
 *
 * -----------------------------------------------------------------------------
 * SAFETY WARNING / HIGH RISK ACTIVITIES:
 * THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
 * ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
 * NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
 * TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
 * -----------------------------------------------------------------------------
 */

using Xunit;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#51, the for-in cluster: `g = gen()` then `for v in g`.
///
/// The desugaring only ever recognised `for v in gen()`, so a generator bound to a name first
/// was refused with the generic list of iterable kinds -- a message that names neither
/// generators nor the binding. Two of the cluster's three forms were that; the third (a
/// generator returned from a function) is now caught earlier by #243.
///
/// The loop emitted is the same one, minus the construction: the machine is the instance the
/// assignment already built, which is what makes two loops over one generator resume rather
/// than restart.
/// </summary>
public class GeneratorBoundToANameTests
{
    private const string Gen =
        "def gen():\n" +
        "    yield 1\n" +
        "    yield 2\n";

    private static ProgramIR Compile(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer("from pymcu.types import uint8\n" + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static List<Instruction> MainBody(ProgramIR p) =>
        Assert.Single(p.Functions, f => f.Name == "main").Body;

    /// Names of the functions `main` calls. The generator's state machine is driven by a call
    /// to its poll(), so this is how a driven loop is told from a refused one.
    private static List<string> CallsIn(ProgramIR p) =>
        MainBody(p).OfType<Call>().Select(c => c.FunctionName).ToList();

    /// The per-instance storage the loop reads and writes. Two loops over ONE generator must
    /// name the same slot; two machines would be two names.
    private static List<string> SlotsIn(ProgramIR p) =>
        MainBody(p).SelectMany(i => i switch
            {
                ArrayStore st => new[] { st.ArrayName },
                ArrayLoad ld => new[] { ld.ArrayName },
                _ => Array.Empty<string>(),
            })
            .Where(n => n.Contains("__slot"))
            .Distinct().ToList();

    [Fact]
    public void AGeneratorBoundToALocalCanBeIterated()
    {
        var ir = Compile(Gen + "T: uint8 = 0\n" +
                         "def main() -> None:\n" +
                         "    global T\n" +
                         "    g = gen()\n" +
                         "    for v in g:\n" +
                         "        T = v\n");

        Assert.Contains(CallsIn(ir), c => c.Contains("poll"));
    }

    [Fact]
    public void AGeneratorBoundAtModuleLevelCanBeIterated()
    {
        // The binding and the loop are in different statement lists, which is why the module
        // level is scanned first and its names handed to every function.
        var ir = Compile(Gen + "T: uint8 = 0\nG = gen()\n" +
                         "def main() -> None:\n" +
                         "    global T\n" +
                         "    for v in G:\n" +
                         "        T = v\n");

        Assert.Contains(CallsIn(ir), c => c.Contains("poll"));
    }

    [Fact]
    public void TwoLoopsOverOneBoundGeneratorShareTheMachine()
    {
        // The property that makes this worth doing rather than rewriting to `for v in gen()`.
        // In Python a generator is consumed: the second loop resumes where the first stopped
        // and finds it exhausted. That only holds if both loops poll the SAME instance, so the
        // machine has to live in the user's variable and not in a fresh temp per loop.
        var ir = Compile(Gen + "T: uint8 = 0\n" +
                         "def main() -> None:\n" +
                         "    global T\n" +
                         "    g = gen()\n" +
                         "    for v in g:\n" +
                         "        T = v\n" +
                         "    for w in g:\n" +
                         "        T = w\n");

        Assert.Equal(2, CallsIn(ir).Count(c => c.Contains("poll")));
        Assert.Single(SlotsIn(ir));
    }

    [Fact]
    public void ANameReboundToSomethingElseIsNoLongerAGenerator()
    {
        // The binding is tracked in statement order, so a name reused after the assignment
        // stops being a machine. Without this the loop would poll an integer.
        var ex = Assert.ThrowsAny<CompilerError>(() =>
            Compile(Gen + "T: uint8 = 0\n" +
                    "def main() -> None:\n" +
                    "    global T\n" +
                    "    g = gen()\n" +
                    "    g = 5\n" +
                    "    for v in g:\n" +
                    "        T = v\n"));

        Assert.Contains("for-in loop iterable", ex.Message);
    }

    [Theory]
    [InlineData("    try:\n        for v in gen():\n            T = v\n    except ValueError:\n        T = 9\n")]
    [InlineData("    try:\n        T = 1\n    except ValueError:\n        for v in gen():\n            T = v\n")]
    [InlineData("    try:\n        T = 1\n    finally:\n        for v in gen():\n            T = v\n")]
    public void AGeneratorLoopInsideATryIsStillDesugared(string body)
    {
        // The rewrite descends into Block, If, While and For, and until now not into try. A
        // `for v in gen():` one line inside a `try` was therefore never desugared and fell
        // through to the generic for-in guard -- refused, with a message naming neither
        // generators nor the try, for a loop that compiles one line further out.
        //
        // A TryStmt holds four statement LISTS (body, each handler, else, finally) rather than
        // Blocks, so it needs its own arm. All four are covered here except `else`, which the
        // parser only builds alongside a handler and which the same arm walks.
        var ir = Compile(Gen + "T: uint8 = 0\n" +
                         "def main() -> None:\n    global T\n" + body);

        Assert.Contains(CallsIn(ir), c => c.Contains("poll"));
    }

    [Fact]
    public void TheDirectFormIsUnchanged()
    {
        // The control. The two forms share the loop, so a mistake in the shared half would
        // show here rather than only in the new cases.
        var ir = Compile(Gen + "T: uint8 = 0\n" +
                         "def main() -> None:\n" +
                         "    global T\n" +
                         "    for v in gen():\n" +
                         "        T = v\n");

        Assert.Contains(CallsIn(ir), c => c.Contains("poll"));
    }
}

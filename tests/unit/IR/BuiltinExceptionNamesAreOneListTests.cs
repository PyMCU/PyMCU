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
/// PyMCU#245: the builtin exception list existed twice.
///
/// `BuiltinExceptionNames.cs` owns it and its own docstring warns that "a second copy of the
/// list would eventually disagree with this one". `IRGenerator/State.cs` was that second copy,
/// with the same six names written out again. The warning is read by whoever opens the file
/// that carries it, and whoever duplicates a list is by definition editing a different one.
///
/// State.cs now derives from the dictionary, and this test is what keeps the derivation honest:
/// it is written against `Codes`, so a seventh name added there is automatically required to
/// work end to end. That is the case that made it urgent -- `StopIteration` would be the
/// seventh, and under the old arrangement someone would add a row to `Codes`, find the name
/// still unrecognised by the IR generator, and have no hint why.
/// </summary>
public class BuiltinExceptionNamesAreOneListTests
{
    public static TheoryData<string> EveryBuiltinName()
    {
        var d = new TheoryData<string>();
        foreach (var name in BuiltinExceptionNames.Codes.Keys) d.Add(name);
        return d;
    }

    [Theory]
    [MemberData(nameof(EveryBuiltinName))]
    public void EveryNameInTheDictionaryIsUsableInRaiseAndExcept(string exn)
    {
        // Compiles rather than asserting on a private field: the property that matters is that
        // the IR generator recognises the name, not that two collections happen to be equal.
        var src =
            "from pymcu.types import uint8\n" +
            "T: uint8 = 0\n" +
            "def risky(n: uint8) -> uint8:\n" +
            "    if n > 2:\n" +
            $"        raise {exn}(\"x\")\n" +
            "    return n\n" +
            "def main() -> None:\n" +
            "    global T\n" +
            "    try:\n" +
            "        T = risky(5)\n" +
            $"    except {exn}:\n" +
            "        T = 9\n";

        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

        Assert.Contains(ir.Functions, f => f.Name == "main");
    }

    [Fact]
    public void AnUnknownExceptionNameIsStillRefused()
    {
        // The control. Without it the test above passes for a compiler that accepts any name at
        // all, which would make the derivation look right while proving nothing.
        //
        // The name is deliberately one nobody will ever add. `StopIteration` was the obvious
        // choice and is the wrong one: it is the seventh name this whole issue is about, so the
        // day someone adds it this control would fail for a reason that has nothing to do with
        // what it guards.
        Assert.ThrowsAny<CompilerError>(() =>
            new IRGenerator().Generate(
                new Parser(new Lexer(
                    "from pymcu.types import uint8\n" +
                    "T: uint8 = 0\n" +
                    "def main() -> None:\n" +
                    "    global T\n" +
                    "    try:\n" +
                    "        raise NeverAnExceptionError(\"x\")\n" +
                    "    except NeverAnExceptionError:\n" +
                    "        T = 9\n").Tokenize()).ParseProgram(),
                new Dictionary<string, ProgramNode>(),
                new DeviceConfig { Arch = "avr" }));
    }
}

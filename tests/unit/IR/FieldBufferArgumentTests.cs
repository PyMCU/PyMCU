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

using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;
using IrBinaryOp = PyMCU.IR.BinaryOp;

namespace PyMCU.UnitTests;

/// <summary>
/// `head(obj.buf)` -- a buffer FIELD passed whole to a function expecting a
/// bytearray -- linked with "undefined reference to main_box_buf". The field's own
/// flattened name (`baseName + "_" + member`) was never declared as real storage:
/// the actual bytes live wherever the constructor argument's own storage was
/// materialized (an inline call's hidden buffer slot, a module array, another
/// field), and the field write side never aliased to it, so the field's name had
/// no backing at all.
///
/// `self._data = data` (Assign.cs) already resolved the right-hand name to its real
/// storage endpoint and called BindArrayAlias -- which ALSO copies the resolved
/// size onto arraySizes[fieldKey]. That copy defeats the alias for two different
/// consumers each in their own way: TryResolveArrayStorageKey's own direct-hit
/// check (fixed to prefer the alias) and a separate, alias-blind argument-marshaling
/// branch that reads arraySizes directly with no alias-chase of its own (the reason
/// both call sites use BindSequenceAlias, not BindArrayAlias, for the final fix).
/// A for-loop's own size resolution over an aliased field, which DID need the
/// mirrored size, is fixed at its own root instead (ResolveAliasedArraySize's bare-
/// name fallback) rather than by bringing the mirror back.
///
/// The exact same family covers a plain local alias outside any field at all
/// (`alias = buf`), while a ternary or a list literal choosing between two whole
/// buffers has no real lowering yet and is refused by name instead of silently
/// reading the wrong value.
/// </summary>
public class FieldBufferArgumentTests
{
    private static ProgramIR Gen(string src) => new IRGenerator().Generate(
        new Parser(new Lexer(src).Tokenize()).ParseProgram(),
        new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static IEnumerable<Instruction> Body(ProgramIR ir, string fn)
        => ir.Functions.Single(f => f.Name == fn).Body;

    private const string Preamble = "from pymcu.types import uint8\n\n";

    private const string Box =
        "class Box:\n" +
        "    def __init__(self, buf):\n" +
        "        self.buf = buf\n";

    private const string Head =
        "def head(v: bytearray) -> uint8:\n" +
        "    return v[0]\n";

    [Fact]
    public void AModuleLevelObjectsFieldForwardsAsTheWholeBuffer()
    {
        var ir = Gen(Preamble + Box + Head +
            "box = Box(bytearray([10, 20]))\n" +
            "def main():\n" +
            "    result: uint8 = head(box.buf)\n");

        Assert.Contains(Body(ir, "main"), i => i is Call { FunctionName: "head" });
    }

    [Fact]
    public void ASelfFieldForwardsAsTheWholeBufferInsideAMethod()
    {
        var ir = Gen(Preamble +
            Head +
            "class Box:\n" +
            "    def __init__(self, buf):\n" +
            "        self.buf = buf\n" +
            "    def first(self) -> uint8:\n" +
            "        return head(self.buf)\n" +
            "def main():\n" +
            "    box = Box(bytearray([10, 20]))\n" +
            "    result: uint8 = box.first()\n");

        Assert.Contains(Body(ir, "main"), i => i is Call);
    }

    [Fact]
    public void ALocalObjectsFieldForwardsAsTheWholeBuffer()
    {
        var ir = Gen(Preamble + Box + Head +
            "def main():\n" +
            "    box = Box(bytearray([10, 20]))\n" +
            "    result: uint8 = head(box.buf)\n");

        Assert.Contains(Body(ir, "main"), i => i is Call { FunctionName: "head" });
    }

    [Fact]
    public void ANestedFieldTwoHopsDeepForwardsAsTheWholeBuffer()
    {
        var ir = Gen(Preamble + Head +
            "class Inner:\n" +
            "    def __init__(self, buf):\n" +
            "        self.buf = buf\n" +
            "class Outer:\n" +
            "    def __init__(self, buf):\n" +
            "        self.inner = Inner(buf)\n" +
            "def main():\n" +
            "    obj = Outer(bytearray([10, 20]))\n" +
            "    result: uint8 = head(obj.inner.buf)\n");

        Assert.Contains(Body(ir, "main"), i => i is Call { FunctionName: "head" });
    }

    [Fact]
    public void AFieldForwardsToAnInlineCallee()
    {
        var ir = Gen("from pymcu.types import uint8, inline\n\n" + Box +
            "@inline\n" +
            "def head(v: bytearray) -> uint8:\n" +
            "    return v[0]\n" +
            "def main():\n" +
            "    box = Box(bytearray([10, 20]))\n" +
            "    result: uint8 = head(box.buf)\n");

        // Inlined: no separate "head" Call survives, but the body must not be a
        // bare scalar copy either -- an indexed load off the real storage is what
        // a correct inline expansion of `v[0]` looks like.
        Assert.Contains(Body(ir, "main"), i => i is ArrayLoad or BytearrayLoad);
    }

    [Fact]
    public void AFieldForwardsToANonInlineCalleeThatWritesThroughIt()
    {
        var ir = Gen(Preamble + Box +
            "def setfirst(v: bytearray, val: uint8):\n" +
            "    v[0] = val\n" +
            "def main():\n" +
            "    box = Box(bytearray([10, 20]))\n" +
            "    setfirst(box.buf, 99)\n" +
            "    r: uint8 = box.buf[0]\n");

        Assert.Contains(Body(ir, "main"), i => i is Call { FunctionName: "setfirst" });
    }

    [Fact]
    public void APlainLocalAliasOfAModuleBufferForwardsAsTheWholeBuffer()
    {
        var ir = Gen(Preamble + Head +
            "buf = bytearray([10, 20])\n" +
            "alias = buf\n" +
            "def main():\n" +
            "    result: uint8 = head(alias)\n");

        Assert.Contains(Body(ir, "main"), i => i is Call { FunctionName: "head" });
    }

    [Fact]
    public void ATernaryChoosingBetweenTwoBuffers_IsRefusedByName()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Preamble + Head +
            "buf = bytearray([10, 20])\n" +
            "other = bytearray([7])\n" +
            "def main():\n" +
            "    flag: uint8 = 1\n" +
            "    result: uint8 = head(buf if flag else other)\n"));

        Assert.Contains("conditional expression", ex.Message);
        Assert.Contains("buffers", ex.Message);
    }

    [Fact]
    public void AListLiteralWithABufferElement_IsRefusedByName()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Preamble + Head +
            "buf = bytearray([10, 20])\n" +
            "def main():\n" +
            "    bufs = [buf]\n" +
            "    for b in bufs:\n" +
            "        result: uint8 = head(b)\n"));

        Assert.Contains("'buf'", ex.Message);
        Assert.Contains("a buffer", ex.Message);
    }
}

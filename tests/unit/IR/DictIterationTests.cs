using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

// Iterating a dict or a set, issue #200. Both were refused with the same list of seven other
// iterables, which mentions neither, so the reader holding a dict was told their dict is not
// on a list without being told whether dicts can be iterated at all.
//
// They are two different answers, and the measurement that separates them is the ORDER.
//
// A dict is walked in insertion order, which is what CPython does, so it unrolls over its
// entries the way a constant list literal already does and every value matches the host.
//
// A set is not. CPython walks a set in hash-table slot order: {30, 5} gives 5 then 30, and
// {70, 7} gives 70 then 7, so it is neither the order written nor sorted. A compile-time
// membership table has no hashes and no table to reproduce that with, and any order chosen
// here would silently disagree with the host on some literal. It is refused by name instead,
// with the list to write if the order matters.
//
// WHAT DISCRIMINATES: every case below except the four invariants. Against the unfixed
// compiler all of them, accepted and refused alike, produce the seven-iterable list.
//
// WHAT IS INVARIANT: what a set IS for (`in`, `len`), the list literal the set refusal points
// at, and the seven-iterable message still arriving for something that really is none of them.
//
// The values are checked on hardware, not here: tests/integration/Tests/AVR/DictIterationTests
// runs the whole transcript against the one CPython prints for the same program.
public class DictIterationTests
{
    private static void Build(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static PyMCU.Common.CompilerError Reject(string src)
        => Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Build(src));

    private const string Preamble =
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
        "from pymcu.types import uint8\n\n\n";

    private static string Program(string body) =>
        Preamble +
        "def main():\n" +
        "    seed: uint8 = GPIOR0.value\n" +
        "    total: uint8 = seed\n" +
        body +
        "    GPIOR1.value = total\n";

    // --- a dict is walked -------------------------------------------------------

    [Fact]
    public void IteratingADictWalksItsKeys()
    {
        Build(Program(
            "    d = {1: 70, 2: 7}\n" +
            "    for k in d:\n" +
            "        total = total + uint8(d[k])\n"));
    }

    [Fact]
    public void ADictLiteralWrittenInThePlaceOfTheIterable()
    {
        Build(Program(
            "    for k in {1: 70, 2: 7}:\n" +
            "        total = total + uint8(k)\n"));
    }

    [Fact]
    public void ItemsGivesTheKeyAndTheValue()
    {
        Build(Program(
            "    d = {1: 70, 2: 7}\n" +
            "    for k, v in d.items():\n" +
            "        total = total + uint8(k) + uint8(v)\n"));
    }

    [Fact]
    public void KeysAndValuesEachGiveTheirOwnHalf()
    {
        Build(Program(
            "    d = {1: 70, 2: 7}\n" +
            "    for k in d.keys():\n" +
            "        total = total + uint8(k)\n" +
            "    for v in d.values():\n" +
            "        total = total + uint8(v)\n"));
    }

    [Fact]
    public void AStringKeyIsAUsableKeyInsideTheBody()
    {
        // The shape the issue was filed with. The key is bound as a string constant, so the
        // second lookup folds the same way `d["red"]` written out does.
        Build(Program(
            "    d = {\"red\": 70, \"green\": 7}\n" +
            "    for name in d:\n" +
            "        total = total + uint8(d[name])\n"));
    }

    // --- a set is not, and the refusal says why ---------------------------------

    [Fact]
    public void IteratingASetIsRefusedByName()
    {
        var ex = Reject(Program(
            "    for x in {70, 7}:\n" +
            "        total = total + uint8(x)\n"));

        Assert.Contains("a set is not iterated here", ex.Message);
        Assert.DoesNotContain("for-in loop iterable must be", ex.Message);
    }

    [Fact]
    public void TheSetRefusalGivesTheReasonRatherThanAList()
    {
        var ex = Reject(Program(
            "    for x in {70, 7}:\n" +
            "        total = total + uint8(x)\n"));

        Assert.Contains("hash order", ex.Message);
        Assert.Contains("differ from the one your program prints on the host", ex.Message);
    }

    [Fact]
    public void TheSetRefusalOffersTheListToWriteInstead()
    {
        // The elements as the program wrote them, so the advice can be pasted.
        var ex = Reject(Program(
            "    s = {30, 5}\n" +
            "    for x in s:\n" +
            "        total = total + uint8(x)\n"));

        Assert.Contains("for x in [30, 5]", ex.Message);
    }

    [Fact]
    public void ALongSetIsElidedRatherThanPrintedWhole()
    {
        var ex = Reject(Program(
            "    s = {30, 5, 9, 11, 13}\n" +
            "    for x in s:\n" +
            "        total = total + uint8(x)\n"));

        Assert.Contains("[30, 5, 9, ...]", ex.Message);
    }

    // --- the two arity mistakes -------------------------------------------------

    [Fact]
    public void OneNameOverItemsSaysWhereTheValueWouldGo()
    {
        var ex = Reject(Program(
            "    d = {1: 70, 2: 7}\n" +
            "    for k in d.items():\n" +
            "        total = total + uint8(k)\n"));

        Assert.Contains("items() gives a key and a value", ex.Message);
        Assert.Contains("nowhere to put the value", ex.Message);
    }

    [Fact]
    public void TwoNamesOverABareDictPointsAtItems()
    {
        var ex = Reject(Program(
            "    d = {1: 70, 2: 7}\n" +
            "    for k, v in d:\n" +
            "        total = total + uint8(v)\n"));

        Assert.Contains("one value at a time", ex.Message);
        Assert.Contains("d.items()", ex.Message);
    }

    [Fact]
    public void AValueThatIsNotConstantIsRefusedAsThat()
    {
        // The unrolling binds each value as a constant, so a run-time one has nothing to bind.
        // Named as that rather than as the dict not being iterable.
        var ex = Reject(Program(
            "    d = {1: seed, 2: 7}\n" +
            "    for v in d.values():\n" +
            "        total = total + uint8(v)\n"));

        Assert.Contains("has to be a constant", ex.Message);
        Assert.Contains("value", ex.Message);
    }

    // --- invariants -------------------------------------------------------------

    [Fact]
    public void WhatASetIsForIsUntouched()
    {
        Build(Program(
            "    s = {70, 7}\n" +
            "    total = total + uint8(seed in s) + uint8(len(s))\n"));
    }

    [Fact]
    public void TheListTheSetRefusalPointsAtCompiles()
    {
        // The advice, run. A refusal that hands over a program which does not build is worse
        // than one that hands over nothing.
        Build(Program(
            "    for x in [30, 5]:\n" +
            "        total = total + uint8(x)\n"));
    }

    [Fact]
    public void ADictSubscriptWithALiteralKeyStillFolds()
    {
        Build(Program(
            "    d = {1: 70, 2: 7}\n" +
            "    total = total + uint8(d[1])\n"));
    }

    [Fact]
    public void SomethingThatIsNoneOfThemStillGetsTheGeneralMessage()
    {
        // The seven-iterable list is the right answer when the iterable really is not one of
        // the supported forms, and it has to keep arriving for those.
        var ex = Reject(Program(
            "    for x in seed:\n" +
            "        total = total + uint8(x)\n"));

        Assert.Contains("for-in loop iterable must be", ex.Message);
    }
}

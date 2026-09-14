using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#359. A variable that held a compile-time constant and is then assigned an array
/// element keeps the old constant, and every later read folds to it.
///
///     acc = 0
///     acc = buf[2]
///     print(acc)          # CPython 52, atmega328p 0
///
/// The IR generator is right: with PYMCU_NO_OPT=1 the device prints 52. `PropagateCopies`
/// decides which instructions retire a tracked constant from a hand-written whitelist --
/// Copy, AugAssign, Binary, Unary, Bitcast, InlineAsm, Call, Label -- and every instruction
/// kind outside it writes its destination and leaves the stale entry standing. `GetDst` in
/// the same file already knows the full set; six of its cases are missing from the whitelist
/// (BitCheck, LoadIndirect, ArrayLoad, ArrayLoadFlash, FlashLoadPtr, BytearrayLoad), so this
/// is a class of bugs and not one case.
///
/// These assertions are on the value the optimized IR carries, not on the program compiling.
/// A test that only checked it compiles would have passed for the whole life of the bug, and
/// the same is true of a test written against the unoptimized IR: the generator emits the
/// variable, and the pass replaces it.
///
/// The register-decode accumulator reaches it through a fold (`0 << 8` is 0, `0 | x` folds to
/// x, the load is retargeted at the name), which is how it was found and why a sensor built on
/// adafruit_register reads what the accumulator was seeded with.
/// </summary>
public class StaleConstantAcrossALoadTests
{
    private static ProgramIR Optimized(string src)
    {
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });
        return Optimizer.Optimize(ir);
    }

    private const string Buf = "buf = bytearray([0x00, 0x12, 0x34, 0x56])\n";

    /// <summary>
    /// What the program's LAST store into buf[0] carries. A store is a position the propagation
    /// pass rewrites, so it witnesses the stale constant; a `return` is not, which is why every
    /// probe here ends in a store rather than in the value it would be natural to return. The
    /// earlier stores into the same slot are the bytearray literal's own zero-fill.
    /// </summary>
    private static Val LastStoredIntoSlotZero(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>()
            .Where(s => s.Index is Constant k && k.Value == 0)
            .Select(s => s.Src).Last();

    private static void AssertStoresAValueAndNotAConstant(ProgramIR ir) =>
        Assert.False(LastStoredIntoSlotZero(ir) is Constant,
            $"the value stored is {LastStoredIntoSlotZero(ir)}, which the program cannot know "
            + "until the array load has run");

    // The bug at its smallest: no fold, no operator, no loop. An array element assigned over a
    // constant must retire the constant.
    [Fact]
    public void AnArrayElementAssignedOverAConstant_RetiresTheConstant()
    {
        var ir = Optimized(Buf +
            "acc = 0\n" +
            "acc = buf[2]\n" +
            "buf[0] = acc\n");

        AssertStoresAValueAndNotAConstant(ir);
    }

    // The shape that found it: the identity fold turns the first iteration of a register
    // decode into the program above.
    [Fact]
    public void AnOrWithZeroOverAnArrayElement_RetiresTheConstant()
    {
        var ir = Optimized(Buf +
            "acc = 0\n" +
            "acc = acc | buf[2]\n" +
            "buf[0] = acc\n");

        AssertStoresAValueAndNotAConstant(ir);
    }

    // Commuted, and with XOR, because the identity is a property of the operator and not of
    // the side the zero is written on.
    [Fact]
    public void AnOrWithZeroOnTheRight_RetiresTheConstant()
    {
        var ir = Optimized(Buf +
            "acc = 0\n" +
            "acc = buf[2] | acc\n" +
            "buf[0] = acc\n");

        AssertStoresAValueAndNotAConstant(ir);
    }

    [Fact]
    public void AnXorWithZeroOverAnArrayElement_RetiresTheConstant()
    {
        var ir = Optimized(Buf +
            "acc = 0\n" +
            "acc = acc ^ buf[2]\n" +
            "buf[0] = acc\n");

        AssertStoresAValueAndNotAConstant(ir);
    }

    // The decode loop itself, unrolled by the compiler, seeded zero.
    [Fact]
    public void AShiftedAccumulatorSeededZero_RetiresTheConstant()
    {
        var ir = Optimized(Buf +
            "reg = 0\n" +
            "reg = (reg << 8) | buf[2]\n" +
            "reg = (reg << 8) | buf[1]\n" +
            "buf[0] = reg\n");

        AssertStoresAValueAndNotAConstant(ir);
    }

    // The three programs that escape the bug today, kept so the fix cannot be had by breaking
    // them: `+` is lowered through a copy the whitelist covers, a non-zero seed has no identity
    // to fold, and a different target name never reads the stale entry back.
    [Fact]
    public void APlusWithZero_StillCarriesTheLoadedValue()
    {
        var ir = Optimized(Buf +
            "acc = 0\n" +
            "acc = acc + buf[2]\n" +
            "buf[0] = acc\n");

        AssertStoresAValueAndNotAConstant(ir);
    }

    [Fact]
    public void ANonZeroSeed_StillCarriesTheLoadedValue()
    {
        var ir = Optimized(Buf +
            "acc = 1\n" +
            "acc = acc | buf[2]\n" +
            "buf[0] = acc\n");

        AssertStoresAValueAndNotAConstant(ir);
    }

    [Fact]
    public void ADifferentTargetName_StillCarriesTheLoadedValue()
    {
        var ir = Optimized(Buf +
            "acc = 0\n" +
            "other = acc | buf[2]\n" +
            "buf[0] = other\n");

        AssertStoresAValueAndNotAConstant(ir);
    }
}

using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#329. A compile-time `__len__` had to be a literal WRITTEN IN THE METHOD: the reader was
/// matched against `[ReturnStmt { Value: IntegerLiteral }]` and nothing else. One method hop, or
/// a constant imported from another module, was refused -- and the size of a part's EEPROM is a
/// chip fact that belongs in the HAL, which is exactly where `microcontroller.nvm` has to reach
/// to get it. `len(nvm)` was therefore stuck at the ATmega328P's 1024 on every part.
///
/// A compile-time `if` chain inside the method always worked, which is what showed the rule was
/// not "a single literal token": the frontend prunes that chain before the reader runs, so by
/// then the body really is one literal. The value has to be REACHED WITHOUT LEAVING THE METHOD,
/// and that is the restriction lifted here.
/// </summary>
public class DunderLenResolutionTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    /// One `__getitem__` dispatch per element, counted through the port write its body makes.
    /// The trip count IS the length the compiler resolved, so the number is the measurement.
    private static int UnrolledReads(string lenBody, string extra = "")
    {
        string src =
            extra +
            "class Table:\n" +
            "    @inline\n" +
            "    def __init__(self):\n" +
            "        pass\n" +
            "    @inline\n" +
            "    def __len__(self) -> uint8:\n" +
            lenBody +
            "    @inline\n" +
            "    def __getitem__(self, index: uint8) -> uint8:\n" +
            "        PORTB: ptr[uint8] = ptr(0x25)\n" +
            "        PORTB.value = index\n" +
            "        return index\n" +
            "table = Table()\n" +
            "def main():\n" +
            "    for v in table:\n" +
            "        pass\n";
        return Gen(src).Functions.SelectMany(f => f.Body).OfType<StoreIndirect>().Count();
    }

    [Fact]
    public void ALiteralInTheMethodStillResolves()
    {
        Assert.Equal(3, UnrolledReads("        return 3\n"));
    }

    [Fact]
    public void AModuleLevelConstantResolves()
    {
        Assert.Equal(5, UnrolledReads("        return _SIZE\n", "_SIZE = 5\n"));
    }

    [Fact]
    public void AClassAttributeResolves()
    {
        string src =
            "class Table:\n" +
            "    SIZE = 6\n" +
            "    @inline\n" +
            "    def __init__(self):\n" +
            "        pass\n" +
            "    @inline\n" +
            "    def __len__(self) -> uint8:\n" +
            "        return Table.SIZE\n" +
            "    @inline\n" +
            "    def __getitem__(self, index: uint8) -> uint8:\n" +
            "        PORTB: ptr[uint8] = ptr(0x25)\n" +
            "        PORTB.value = index\n" +
            "        return index\n" +
            "table = Table()\n" +
            "def main():\n" +
            "    for v in table:\n" +
            "        pass\n";
        Assert.Equal(6, Gen(src).Functions.SelectMany(f => f.Body).OfType<StoreIndirect>().Count());
    }

    [Fact]
    public void OneMethodHopInsideTheSameClassResolves()
    {
        string src =
            "class Table:\n" +
            "    @inline\n" +
            "    def __init__(self):\n" +
            "        pass\n" +
            "    @inline\n" +
            "    def _size(self) -> uint8:\n" +
            "        return 4\n" +
            "    @inline\n" +
            "    def __len__(self) -> uint8:\n" +
            "        return self._size()\n" +
            "    @inline\n" +
            "    def __getitem__(self, index: uint8) -> uint8:\n" +
            "        PORTB: ptr[uint8] = ptr(0x25)\n" +
            "        PORTB.value = index\n" +
            "        return index\n" +
            "table = Table()\n" +
            "def main():\n" +
            "    for v in table:\n" +
            "        pass\n";
        Assert.Equal(4, Gen(src).Functions.SelectMany(f => f.Body).OfType<StoreIndirect>().Count());
    }

    [Fact]
    public void OneMethodHopIntoAnotherClassResolves()
    {
        // The shape the HAL has: the size is a fact of another object, constructed and asked.
        string src =
            "class Eeprom:\n" +
            "    @inline\n" +
            "    def __init__(self):\n" +
            "        pass\n" +
            "    @inline\n" +
            "    def size(self) -> uint8:\n" +
            "        return 7\n" +
            "class Table:\n" +
            "    @inline\n" +
            "    def __init__(self):\n" +
            "        pass\n" +
            "    @inline\n" +
            "    def __len__(self) -> uint8:\n" +
            "        return Eeprom().size()\n" +
            "    @inline\n" +
            "    def __getitem__(self, index: uint8) -> uint8:\n" +
            "        PORTB: ptr[uint8] = ptr(0x25)\n" +
            "        PORTB.value = index\n" +
            "        return index\n" +
            "table = Table()\n" +
            "def main():\n" +
            "    for v in table:\n" +
            "        pass\n";
        Assert.Equal(7, Gen(src).Functions.SelectMany(f => f.Body).OfType<StoreIndirect>().Count());
    }

    [Fact]
    public void AModuleConstantReachedThroughOneHopResolves()
    {
        // Both mechanisms at once, which is the CircuitPython layer's actual shape: the layer
        // asks the HAL object, and the HAL object returns the chip's constant.
        Assert.Equal(6, UnrolledReads(
            "        return self._size()\n" +
            "    @inline\n" +
            "    def _size(self) -> uint8:\n" +
            "        return _SIZE\n",
            "_SIZE = 6\n"));
    }

    [Fact]
    public void ARunTimeLenIsStillRefusedWithItsOwnSentence()
    {
        // The restriction that remains: a length read from hardware has no compile-time value,
        // and saying so is the honest answer rather than a number picked for it.
        var err = Assert.Throws<PyMCU.Common.CompilerError>(() => Gen(
            "class Table:\n" +
            "    def __init__(self, n: uint8):\n        self.n: uint8 = n\n" +
            "    def __len__(self) -> uint8:\n        return self.n\n" +
            "    def __getitem__(self, index: uint8) -> uint8:\n        return index\n" +
            "def main():\n" +
            "    GPIOR0: ptr[uint8] = ptr(0x3E)\n" +
            "    t = Table(GPIOR0.value)\n" +
            "    for v in t:\n        pass\n"));
        Assert.Contains("not a compile-time constant", err.Message);
    }

    [Fact]
    public void ASliceAssignWithAnOmittedBoundUsesTheResolvedLength()
    {
        // The canonical `microcontroller.nvm[0:] = b'...'` shape: the omitted stop is what makes
        // the length load-bearing rather than decorative.
        string src =
            "_SIZE = 3\n" +
            "class Store:\n" +
            "    @inline\n" +
            "    def __init__(self):\n" +
            "        pass\n" +
            "    @inline\n" +
            "    def __len__(self) -> uint16:\n" +
            "        return _SIZE\n" +
            "    @inline\n" +
            "    def __setitem__(self, index: uint16, value: uint8):\n" +
            "        PORTB: ptr[uint8] = ptr(0x25)\n" +
            "        PORTB.value = value\n" +
            "store = Store()\n" +
            "def main():\n" +
            "    store[0:] = [1, 2, 3]\n";
        Assert.Equal(3, Gen(src).Functions.SelectMany(f => f.Body).OfType<StoreIndirect>().Count());
    }

    [Fact]
    public void ASliceReadOfAnObjectIsNamedForWhatItIs()
    {
        // The message the issue reports as wrong: an object with __getitem__ was told about
        // fixed-size arrays, which is neither what the reader wrote nor what they should write.
        var err = Assert.Throws<PyMCU.Common.CompilerError>(() => Gen(
            "class Store:\n" +
            "    @inline\n" +
            "    def __init__(self):\n" +
            "        pass\n" +
            "    @inline\n" +
            "    def __len__(self) -> uint16:\n" +
            "        return 4\n" +
            "    @inline\n" +
            "    def __getitem__(self, index: uint16) -> uint8:\n" +
            "        return index\n" +
            "store = Store()\n" +
            "def main():\n" +
            "    y = store[0:2]\n"));
        Assert.Contains("a slice READ of 'Store'", err.Message);
        Assert.DoesNotContain("named fixed-size arrays", err.Message);
    }
}

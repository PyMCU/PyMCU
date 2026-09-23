using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A class's field layout is derived while the module scan is still walking the
/// file, so a constructor callee that is declared LATER (BitmapFont below
/// Framebuf's text()) is not in the sequentially-filled class-name table yet.
/// The field must still type as that instance: the order-independent
/// classModuleMap answers what the walk cannot.
/// </summary>
public class LaterDeclaredClassFieldTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    [Fact]
    public void AFieldAssignedALaterDeclaredClassKeepsItsInstanceType()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "class Holder:\n" +
            "    def __init__(self):\n" +
            "        self.f = None\n" +
            "    def set(self):\n" +
            "        self.f = Widget()\n" +
            "class Widget:\n" +
            "    def __init__(self):\n" +
            "        self.v: uint8 = 0\n" +
            "h = Holder()\n" +
            "h.set()\n");

        // None + a scalar write would tag the field; None + an instance write must
        // not. A `..._f$tag` storage sibling anywhere is the misclassification.
        Assert.DoesNotContain(
            ir.Functions.SelectMany(fn => fn.Body).OfType<Copy>(),
            c => (c.Dst is Variable d && d.Name.Contains("f$tag"))
                 || (c.Src is Variable s && s.Name.Contains("f$tag")));
        Assert.DoesNotContain(
            ir.Globals, g => g.Name.Contains("f$tag"));
    }

    [Fact]
    public void AMemberReadOnALaterDeclaredClassFieldCompiles()
    {
        // The Framebuf shape: the field read's member access resolves through the
        // instance type. With the scalar misclassification this refused with
        // 'v is not a member of a numeric value'.
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "class Holder:\n" +
            "    def __init__(self):\n" +
            "        self.f = None\n" +
            "    def set(self):\n" +
            "        self.f = Widget()\n" +
            "    def get(self) -> uint8:\n" +
            "        return self.f.v\n" +
            "class Widget:\n" +
            "    def __init__(self):\n" +
            "        self.v: uint8 = 0\n" +
            "h = Holder()\n" +
            "h.set()\n" +
            "n = h.get()\n");

        Assert.DoesNotContain(
            ir.Functions.SelectMany(fn => fn.Body).OfType<Copy>(),
            c => (c.Dst is Variable d && d.Name.Contains("f$tag"))
                 || (c.Src is Variable s && s.Name.Contains("f$tag")));
    }
}

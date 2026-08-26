using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// The debug spelling of an f-string, `f"{seed=}"` (issue #185). It is written to label a
/// value, and the label was the half that went missing: the parser sub-parsed the field text
/// and threw away whatever the expression did not consume, so the '=' left no trace and the
/// program printed a bare number. Nothing was reported, and the log line still looked right.
///
/// These tests assert the text the program writes and the order it writes it in, because a
/// build-success assertion passes on the unfixed compiler.
/// </summary>
public class FStringDebugSpecTests
{
    // Without a HAL there is no writer for print() to resolve. These four definitions are all
    // these programs need from one.
    private const string Prelude =
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "def uart_write(c: uint8):\n" +
        "    pass\n" +
        "def uart_write_decimal_u8(v: uint8):\n" +
        "    pass\n" +
        "def uart_write_fmt(v: uint16, width: uint8, radix: uint8, pad: uint8, upper: uint8):\n" +
        "    pass\n";

    private static ProgramIR GenerateIR(string source)
    {
        var lexer = new Lexer(Prelude + source);
        var parser = new Parser(lexer.Tokenize());
        return new IRGenerator().Generate(parser.ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });
    }

    /// <summary>
    /// What the program writes, in emission order: a literal run as its own text, and an
    /// interpolated value as "{name}". A label that is dropped simply does not appear, which is
    /// the whole defect, so the assertions below are on this sequence and not on a byte count.
    /// </summary>
    private static List<string> WriteStream(ProgramIR ir)
    {
        var body = ir.Functions.SelectMany(f => f.Body).ToList();
        var texts = new Dictionary<string, string>();
        foreach (var fd in body.OfType<FlashData>())
            texts[fd.Name] = new string(fd.Bytes.TakeWhile(b => b != 0).Select(b => (char)b).ToArray());

        // A value reaches the writer in a temporary. Name it after whatever was copied in, so
        // the stream says which value was written and not just that one was.
        var sources = new Dictionary<string, string>();
        foreach (var copy in body.OfType<Copy>())
            if (copy.Dst is Temporary d && copy.Src is Variable s)
                sources[d.Name] = s.Name;

        var stream = new List<string>();
        foreach (var call in body.OfType<Call>())
        {
            if (!call.FunctionName.Contains("uart_write")) continue;
            foreach (var arg in call.Args.Take(1))
            {
                if (arg is FlashStrAddr f)
                    stream.Add(texts.TryGetValue(f.Name, out var t) ? t : "");
                else if (arg is Variable v)
                    stream.Add("{" + v.Name + "}");
                else if (arg is Temporary t2)
                    stream.Add("{" + (sources.TryGetValue(t2.Name, out var src) ? src : t2.Name) + "}");
                else if (arg is Constant c)
                    stream.Add(c.Value.ToString());
            }
        }

        return stream;
    }

    private static string Rendered(ProgramIR ir) => string.Concat(WriteStream(ir));

    [Fact]
    public void DebugSpec_WritesTheLabelThenTheValue()
    {
        // The issue's program. `seed` is a parameter so the value cannot be folded into the
        // label, and the two halves stay distinguishable in the stream.
        var ir = GenerateIR(
            "def main(seed: uint8):\n" +
            "    print(f\"{seed=}\")\n");

        Assert.Contains("seed=", WriteStream(ir));
        Assert.StartsWith("seed={main.seed}", Rendered(ir));
    }

    [Fact]
    public void DebugSpec_IsTheSameProgramAsWritingTheLabelByHand()
    {
        // What the issue asks for: `f"{seed=}"` is the spelling `f"seed={seed}"` expands to.
        var debug = GenerateIR("def main(seed: uint8):\n    print(f\"{seed=}\")\n");
        var byHand = GenerateIR("def main(seed: uint8):\n    print(f\"seed={seed}\")\n");

        Assert.Equal(WriteStream(byHand), WriteStream(debug));
    }

    [Fact]
    public void DebugSpec_KeepsTheSpacingAsWritten()
    {
        // `f"{seed = }"` labels with "seed = ", spaces included. The text is the field
        // verbatim, which is what makes the label read the way it was typed.
        var ir = GenerateIR(
            "def main(seed: uint8):\n" +
            "    print(f\"{seed = }\")\n");

        Assert.Contains("seed = ", WriteStream(ir));
    }

    [Fact]
    public void DebugSpec_LabelsEachFieldSeparately()
    {
        // Two labelled values in one line, which is the shape that makes the loss unreadable:
        // dropped, this prints two bare numbers in an order the reader has to guess.
        var ir = GenerateIR(
            "def main(seed: uint8, count: uint8):\n" +
            "    print(f\"{seed=} {count=}\")\n");

        Assert.StartsWith("seed={main.seed} count={main.count}", Rendered(ir));
    }

    [Fact]
    public void DebugSpec_LabelsAnExpressionWithItsSource()
    {
        // The label is the source text of the expression, not the name of a variable, so a
        // field that is not a bare name still says what it was.
        var ir = GenerateIR(
            "def main(a: uint8, b: uint8):\n" +
            "    print(f\"{a + b=}\")\n");

        Assert.Contains("a + b=", WriteStream(ir));
    }

    [Fact]
    public void DebugSpec_CombinesWithAFormatSpec()
    {
        // `=` and a spec together: the label is the text, the spec still governs the value.
        var ir = GenerateIR(
            "def main(seed: uint8):\n" +
            "    print(f\"{seed=:02x}\")\n");

        Assert.Contains("seed=", WriteStream(ir));
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<Call>(),
            c => c.FunctionName.Contains("uart_write_fmt"));
    }

    [Fact]
    public void ComparisonInAField_IsNotADebugSpec()
    {
        // '==' is an operator, not the debug spelling. Reading it as one would label every
        // comparison with its own source text.
        var ir = GenerateIR(
            "def main(a: uint8, b: uint8):\n" +
            "    print(f\"{a == b}\")\n");

        Assert.DoesNotContain(WriteStream(ir), s => s.Contains('='));
    }

    [Theory]
    [InlineData("a != b")]
    [InlineData("a <= b")]
    [InlineData("a >= b")]
    public void ComparisonOperators_AreNotDebugSpecs(string field)
    {
        var ir = GenerateIR(
            "def main(a: uint8, b: uint8):\n" +
            "    print(f\"{" + field + "}\")\n");

        Assert.DoesNotContain(WriteStream(ir), s => s.Contains('='));
    }

    [Fact]
    public void EqualsInsideAQuotedRun_IsNotADebugSpec()
    {
        // The '=' here belongs to a string the field prints. Finding the marker means skipping
        // quoted text as well as brackets, or this becomes the label "'a=b'" over a broken
        // expression.
        var ir = GenerateIR(
            "def main():\n" +
            "    print(f\"{'a=b'}\")\n");

        Assert.StartsWith("a=b", Rendered(ir));
    }

    [Fact]
    public void BangInsideAQuotedRun_IsNotAConversion()
    {
        // Same rule on the other marker: a '!' the field prints is not a conversion request.
        var ir = GenerateIR(
            "def main():\n" +
            "    print(f\"{'a!b'}\")\n");

        Assert.StartsWith("a!b", Rendered(ir));
    }

    [Fact]
    public void EqualsInsideBrackets_IsNotADebugSpec()
    {
        // A keyword argument carries an '=' that ends nothing.
        var ir = GenerateIR(
            "def twice(v: uint8) -> uint8:\n" +
            "    return v * 2\n" +
            "def main(a: uint8):\n" +
            "    print(f\"{twice(v=a)}\")\n");

        Assert.DoesNotContain(WriteStream(ir), s => s.Contains('='));
    }

    [Fact]
    public void Conversion_IsRefusedByName()
    {
        // A neighbour in the same spelling. PyMCU writes a value as text and has no second
        // rendering to switch to, so '!r' cannot be honoured. It used to reach the sub-lexer,
        // which reported a valid f-string as a broken '!=': "Did you mean 'not' or '!='?".
        var ex = Assert.Throws<SyntaxError>(() =>
            GenerateIR("def main(seed: uint8):\n    print(f\"{seed!r}\")\n"));

        Assert.Contains("!r", ex.Message);
        Assert.Contains("conversion", ex.Message);
    }

    [Fact]
    public void AnFStringError_PointsAtTheFString()
    {
        // What is wrong is inside the literal, and the parser has stepped past it by the time
        // the field is examined, so the caret used to land on the ')' after it.
        var ex = Assert.Throws<SyntaxError>(() =>
            GenerateIR("def main(seed: uint8):\n    print(f\"{seed!r}\")\n"));

        //            1234567890123456789
        // the line:  "    print(f\"{seed!r}\")", f at column 11, 11 characters long
        Assert.Equal(11, ex.Column);
        Assert.Equal(11, ex.Length);
    }
}

using PyMCU.Frontend;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// The two commonest `except` spellings after the bare one, and the messages that used to
/// describe the program as missing what it has (issue #196):
///
///     except E as e     ->  Expected ':' after exception type       (the colon is there)
///     except (A, B)     ->  Expected exception type after 'except'  (there are two)
///     except* E         ->  Expected exception type after 'except'  (it is right there)
///
/// The third is not in the issue. It is the same defect three tokens away in the same clause,
/// from the same code, so leaving it would have meant reading the identical wrong message from
/// a line that had just been changed.
///
/// What makes that costly is the neighbourhood. `except ValueError:`, a user-defined exception
/// class, a bare `raise` re-raising, propagation from a callee, `else`/`finally`, and `return`
/// inside `try` with a `finally` all work, so the reader has a handler that compiles one line
/// above and no hint why `as e` broke it.
///
/// Both front ends, separately, because the CPython one did not inherit the refusal: it read
/// `as e` and dropped the binding, and unparsed a tuple into `(ValueError, TypeError)` as if it
/// were a type name. So the same program built through one front end and was refused by the
/// other, which is a divergence of the AST contract the two share.
///
/// WHAT DISCRIMINATES: every assertion about the new sentences, and the two `Translator` cases,
/// which BUILT before this change rather than reporting anything.
///
/// WHAT IS INVARIANT: the `except` forms that do work, and `as` in the two other places the
/// grammar uses it. A refusal wired to the `as` token is one edit away from taking `with ... as
/// f` and `import x as y` with it.
/// </summary>
public class ExceptFormDiagnosticTests
{
    private static void Parse(string src) =>
        new Parser(new Lexer(src).Tokenize()).ParseProgram();

    private static string Refusal(string src) =>
        Assert.ThrowsAny<Exception>(() => Parse(src)).Message;

    /// The same source through the CPython front end, whose translator reports by raising in
    /// pymcu_translate.py and surfaces here as the SyntaxError the C# parser would have thrown.
    private static string TranslatorRefusal(string src) =>
        Assert.ThrowsAny<Exception>(() => PythonAstReader.ParseSource(src, "main.py")).Message;

    private const string AsHandler =
        "def main():\n" +
        "    try:\n" +
        "        raise ValueError\n" +
        "    except ValueError as e:\n" +
        "        pass\n";

    private const string TupleHandler =
        "def main():\n" +
        "    try:\n" +
        "        raise ValueError\n" +
        "    except (ValueError, TypeError):\n" +
        "        pass\n";

    private const string ExceptStarHandler =
        "def main():\n" +
        "    try:\n" +
        "        raise ValueError\n" +
        "    except* ValueError:\n" +
        "        pass\n";

    // ── `except E as e` ──────────────────────────────────────────────────────

    [Fact]
    public void BindingTheExceptionToAName_IsRefusedByName()
    {
        var msg = Refusal(AsHandler);

        Assert.Contains("'except ValueError as ...' is not supported", msg);
        Assert.DoesNotContain("Expected ':' after exception type", msg);
    }

    [Fact]
    public void TheRefusalSaysWhyThereIsNothingToBind()
    {
        // The reason is the reusable half. A raise lowers to one exception code and no object,
        // so a reader who learns it here stops looking for the exception object anywhere else.
        var msg = Refusal(AsHandler);

        Assert.Contains("carries only which exception was raised", msg);
        Assert.Contains("not an exception object", msg);
    }

    [Fact]
    public void TheRefusalOffersTheSpellingThatWorks()
    {
        var msg = Refusal(AsHandler);

        Assert.Contains("'except ValueError:'", msg);
    }

    [Fact]
    public void TheRefusalNamesTheTypeTheProgramWrote()
    {
        var msg = Refusal(
            "def main():\n" +
            "    try:\n" +
            "        raise OSError\n" +
            "    except OSError as err:\n" +
            "        pass\n");

        Assert.Contains("'except OSError as ...' is not supported", msg);
        Assert.Contains("'except OSError:'", msg);
    }

    // ── `except (A, B)` ──────────────────────────────────────────────────────

    [Fact]
    public void ATupleOfTypes_IsRefusedByName()
    {
        var msg = Refusal(TupleHandler);

        Assert.Contains("'except (A, B):' is not supported", msg);
        Assert.DoesNotContain("Expected exception type after 'except'", msg);
    }

    [Fact]
    public void TheRefusalSaysToWriteOneClausePerType()
    {
        var msg = Refusal(TupleHandler);

        Assert.Contains("one 'except' clause per exception type", msg);
    }

    [Fact]
    public void ASingleTypeInParentheses_GetsTheSameAnswer()
    {
        // CPython accepts `except (ValueError):` as well, and the advice covers it: the way out
        // is the type without the parentheses either way.
        var msg = Refusal(
            "def main():\n" +
            "    try:\n" +
            "        raise ValueError\n" +
            "    except (ValueError):\n" +
            "        pass\n");

        Assert.Contains("without parentheses", msg);
    }

    // ── `except*` ────────────────────────────────────────────────────────────

    // Not one of the two the issue names. It is the same defect three tokens away in the same
    // clause: `except* ValueError:` reported "Expected exception type after 'except'" with the
    // type right there, from the code being changed here.
    [Fact]
    public void ExceptStar_IsRefusedByName()
    {
        var msg = Refusal(ExceptStarHandler);

        Assert.Contains("'except*'", msg);
        Assert.Contains("exception groups", msg);
        Assert.DoesNotContain("Expected exception type after 'except'", msg);
    }

    [Fact]
    public void TheExceptStarRefusalSaysWhyThereIsNoGroup()
    {
        var msg = Refusal(ExceptStarHandler);

        Assert.Contains("one exception at a time", msg);
    }

    [Fact]
    public void TheTranslatorNamesExceptStarRatherThanTheAstClass()
    {
        // It already refused this one, as "TryStar is not supported", which names a class of
        // CPython's AST and nothing the reader wrote.
        var msg = TranslatorRefusal(ExceptStarHandler);

        Assert.Contains("'except*'", msg);
        Assert.DoesNotContain("TryStar", msg);
    }

    // ── the CPython front end, which accepted both ───────────────────────────

    [Fact]
    public void TheTranslatorRefusesTheNameBinding_WithTheSameSentence()
    {
        // It used to BUILD, silently dropping the binding, so this is where the two front ends
        // disagreed about what the language is.
        Assert.Equal(Refusal(AsHandler), TranslatorRefusal(AsHandler));
    }

    [Fact]
    public void TheTranslatorRefusesTheTuple_WithTheSameSentence()
    {
        // It used to reach name resolution and report that `(ValueError, TypeError)` is not
        // defined, which is a name no one wrote.
        Assert.Equal(Refusal(TupleHandler), TranslatorRefusal(TupleHandler));
    }

    // ── invariants: the forms that work ──────────────────────────────────────

    [Fact]
    public void APlainHandler_StillParses()
    {
        Parse("def main():\n    try:\n        raise ValueError\n    except ValueError:\n        pass\n");
    }

    [Fact]
    public void ABareHandler_StillParses()
    {
        Parse("def main():\n    try:\n        raise ValueError\n    except:\n        pass\n");
    }

    [Fact]
    public void TwoHandlersElseAndFinally_StillParse()
    {
        Parse("def main():\n" +
              "    try:\n" +
              "        raise ValueError\n" +
              "    except ValueError:\n" +
              "        pass\n" +
              "    except TypeError:\n" +
              "        pass\n" +
              "    else:\n" +
              "        pass\n" +
              "    finally:\n" +
              "        pass\n");
    }

    [Fact]
    public void TheTranslatorStillAcceptsAPlainHandler()
    {
        PythonAstReader.ParseSource(
            "def main():\n    try:\n        raise ValueError\n    except ValueError:\n        pass\n",
            "main.py");
    }

    // ── invariants: `as` elsewhere in the grammar ────────────────────────────

    [Fact]
    public void WithAs_IsUntouched()
    {
        Parse("def main():\n    with lock as guard:\n        pass\n");
    }

    [Fact]
    public void ImportAs_IsUntouched()
    {
        Parse("import time as clock\n\ndef main():\n    pass\n");
    }
}

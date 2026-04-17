using System;
using Xunit;
using System.Linq;
using PyMCU.Common;
using PyMCU.Frontend;

namespace PyMCU.UnitTests;

public class LexerTests
{
    [Fact]
    public void BasicTokens()
    {
        var lexer = new Lexer("def main():" + (char)10 + "    return 42");
        var tokens = lexer.Tokenize();

        Assert.Equal(12, tokens.Count);
        Assert.Equal(TokenType.Def, tokens[0].Type);
        Assert.Equal(TokenType.Identifier, tokens[1].Type);
        Assert.Equal("main", tokens[1].Value);
        Assert.Equal(TokenType.LParen, tokens[2].Type);
        Assert.Equal(TokenType.RParen, tokens[3].Type);
        Assert.Equal(TokenType.Colon, tokens[4].Type);
        Assert.Equal(TokenType.Newline, tokens[5].Type);
        Assert.Equal(TokenType.Indent, tokens[6].Type);
        Assert.Equal(TokenType.Return, tokens[7].Type);
        Assert.Equal(TokenType.Number, tokens[8].Type);
        Assert.Equal("42", tokens[8].Value);
        Assert.Equal(TokenType.Newline, tokens[9].Type);
        Assert.Equal(TokenType.Dedent, tokens[10].Type);
        Assert.Equal(TokenType.EndOfFile, tokens[11].Type);
    }

    [Fact]
    public void Indentation()
    {
        var lexer = new Lexer("def a():" + (char)10 + "    def b():" + (char)10 + "        return 1" + (char)10 + "    return 2");
        var tokens = lexer.Tokenize();

        bool foundIndent = tokens.Any(t => t.Type == TokenType.Indent);
        bool foundDedent = tokens.Any(t => t.Type == TokenType.Dedent);

        Assert.True(foundIndent);
        Assert.True(foundDedent);
    }

    [Fact]
    public void Numbers()
    {
        var lexer = new Lexer("0 123 0x10 0b1010 0o77 1_000");
        var tokens = lexer.Tokenize();

        Assert.True(tokens.Count >= 6);
        Assert.Equal("0", tokens[0].Value);
        Assert.Equal("123", tokens[1].Value);
        Assert.Equal("0x10", tokens[2].Value);
        Assert.Equal("0b1010", tokens[3].Value);
        Assert.Equal("0o77", tokens[4].Value);
        Assert.Equal("1_000", tokens[5].Value);
    }

    [Fact]
    public void InvalidCharacter()
    {
        var lexer = new Lexer("$");
        bool thrown = false;
        try { lexer.Tokenize(); } catch (LexicalError) { thrown = true; }
        Assert.True(thrown);
    }

    [Fact]
    public void InvalidNumber()
    {
        var lexer = new Lexer("0123");
        bool thrown = false;
        try { lexer.Tokenize(); } catch (LexicalError) { thrown = true; }
        Assert.True(thrown);
    }

    [Fact]
    public void Comments()
    {
        var lexer = new Lexer("def # comment" + (char)10 + "    return 1");
        var tokens = lexer.Tokenize();

        Assert.Equal(TokenType.Def, tokens[0].Type);
        Assert.Equal(TokenType.Newline, tokens[1].Type);
    }

    // ─── String Literals ──────────────────────────────────────────────────

    [Fact]
    public void DoubleQuotedString_ProducesStringToken()
    {
        var lexer = new Lexer("\"hello\"");
        var tokens = lexer.Tokenize();

        var str = tokens.First(t => t.Type == TokenType.String);
        Assert.Equal("hello", str.Value);
    }

    [Fact]
    public void SingleQuotedString_ProducesStringToken()
    {
        var lexer = new Lexer("'world'");
        var tokens = lexer.Tokenize();

        var str = tokens.First(t => t.Type == TokenType.String);
        Assert.Equal("world", str.Value);
    }

    [Fact]
    public void StringWithEscapeSequences_Parsed()
    {
        // "\n\t" should be decoded to the actual control characters
        var lexer = new Lexer("\"\\n\\t\"");
        var tokens = lexer.Tokenize();

        var str = tokens.First(t => t.Type == TokenType.String);
        Assert.Contains((char)10, str.Value);  // newline
        Assert.Contains((char)9, str.Value);   // tab
    }

    [Fact]
    public void UnterminatedString_ThrowsLexicalError()
    {
        var lexer = new Lexer("\"unterminated");
        bool thrown = false;
        try { lexer.Tokenize(); } catch (LexicalError) { thrown = true; }
        Assert.True(thrown);
    }

    // ─── Operators ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("==", TokenType.EqualEqual)]
    [InlineData("!=", TokenType.BangEqual)]
    [InlineData("<",  TokenType.Less)]
    [InlineData("<=", TokenType.LessEqual)]
    [InlineData(">",  TokenType.Greater)]
    [InlineData(">=", TokenType.GreaterEqual)]
    [InlineData("+",  TokenType.Plus)]
    [InlineData("-",  TokenType.Minus)]
    [InlineData("*",  TokenType.Star)]
    [InlineData("/",  TokenType.Slash)]
    [InlineData("%",  TokenType.Percent)]
    [InlineData("//", TokenType.FloorDiv)]
    [InlineData("**", TokenType.DoubleStar)]
    [InlineData("&",  TokenType.Ampersand)]
    [InlineData("|",  TokenType.Pipe)]
    [InlineData("^",  TokenType.Caret)]
    [InlineData("~",  TokenType.Tilde)]
    [InlineData("<<", TokenType.LShift)]
    [InlineData(">>", TokenType.RShift)]
    public void Operator_ProducesCorrectTokenType(string op, TokenType expected)
    {
        var lexer = new Lexer(op);
        var tokens = lexer.Tokenize();

        Assert.Contains(tokens, t => t.Type == expected);
    }

    // ─── Augmented Assignment Operators ───────────────────────────────────

    [Theory]
    [InlineData("+=",  TokenType.PlusEqual)]
    [InlineData("-=",  TokenType.MinusEqual)]
    [InlineData("*=",  TokenType.StarEqual)]
    [InlineData("/=",  TokenType.SlashEqual)]
    [InlineData("//=", TokenType.FloorDivEqual)]
    [InlineData("%=",  TokenType.PercentEqual)]
    [InlineData("&=",  TokenType.AmpEqual)]
    [InlineData("|=",  TokenType.PipeEqual)]
    [InlineData("^=",  TokenType.CaretEqual)]
    [InlineData("<<=", TokenType.LShiftEqual)]
    [InlineData(">>=", TokenType.RShiftEqual)]
    public void AugmentedAssignmentOperator_ProducesCorrectTokenType(string op, TokenType expected)
    {
        var lexer = new Lexer(op);
        var tokens = lexer.Tokenize();

        Assert.Contains(tokens, t => t.Type == expected);
    }

    // ─── Arrow and Decorator ──────────────────────────────────────────────

    [Fact]
    public void Arrow_ProducesArrowToken()
    {
        var lexer = new Lexer("->");
        var tokens = lexer.Tokenize();

        Assert.Contains(tokens, t => t.Type == TokenType.Arrow);
    }

    [Fact]
    public void At_ProducesAtToken()
    {
        var lexer = new Lexer("@inline");
        var tokens = lexer.Tokenize();

        Assert.Equal(TokenType.At, tokens[0].Type);
        Assert.Equal(TokenType.Identifier, tokens[1].Type);
        Assert.Equal("inline", tokens[1].Value);
    }

    // ─── Walrus Operator ──────────────────────────────────────────────────

    [Fact]
    public void Walrus_ProducesWalrusToken()
    {
        var lexer = new Lexer(":=");
        var tokens = lexer.Tokenize();

        Assert.Contains(tokens, t => t.Type == TokenType.Walrus);
    }

    // ─── Dot ──────────────────────────────────────────────────────────────

    [Fact]
    public void Dot_ProducesDotToken()
    {
        var lexer = new Lexer("a.b");
        var tokens = lexer.Tokenize();

        Assert.Contains(tokens, t => t.Type == TokenType.Dot);
    }

    // ─── Keywords ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("if",       TokenType.If)]
    [InlineData("elif",     TokenType.Elif)]
    [InlineData("else",     TokenType.Else)]
    [InlineData("while",    TokenType.While)]
    [InlineData("for",      TokenType.For)]
    [InlineData("in",       TokenType.In)]
    [InlineData("break",    TokenType.Break)]
    [InlineData("continue", TokenType.Continue)]
    [InlineData("pass",     TokenType.Pass)]
    [InlineData("match",    TokenType.Match)]
    [InlineData("case",     TokenType.Case)]
    [InlineData("import",   TokenType.Import)]
    [InlineData("from",     TokenType.From)]
    [InlineData("as",       TokenType.As)]
    [InlineData("True",     TokenType.True)]
    [InlineData("False",    TokenType.False)]
    [InlineData("None",     TokenType.None)]
    [InlineData("or",       TokenType.Or)]
    [InlineData("and",      TokenType.And)]
    [InlineData("not",      TokenType.Not)]
    [InlineData("global",   TokenType.Global)]
    [InlineData("class",    TokenType.Class)]
    [InlineData("lambda",   TokenType.Lambda)]
    [InlineData("nonlocal", TokenType.Nonlocal)]
    [InlineData("return",   TokenType.Return)]
    public void Keyword_ProducesCorrectTokenType(string keyword, TokenType expected)
    {
        var lexer = new Lexer(keyword);
        var tokens = lexer.Tokenize();

        Assert.Contains(tokens, t => t.Type == expected);
    }

    // ─── Float literals ───────────────────────────────────────────────────

    [Fact]
    public void FloatLiteral_ProducesNumberToken()
    {
        var lexer = new Lexer("3.14");
        var tokens = lexer.Tokenize();

        var num = tokens.First(t => t.Type == TokenType.Number);
        Assert.Equal("3.14", num.Value);
    }

    // ─── Newline suppression inside parens ────────────────────────────────

    [Fact]
    public void NewlineSuppression_InParens_NoNewlineTokens()
    {
        // Newlines inside parentheses must be suppressed by the lexer.
        var src = "(\n    1,\n    2\n)";
        var lexer = new Lexer(src);
        var tokens = lexer.Tokenize();

        // No Newline tokens should appear between ( and )
        bool inParens = false;
        foreach (var t in tokens)
        {
            if (t.Type == TokenType.LParen) { inParens = true; continue; }
            if (t.Type == TokenType.RParen) { inParens = false; continue; }
            if (inParens) Assert.NotEqual(TokenType.Newline, t.Type);
        }
    }

    // ─── BangAlone_ThrowsLexicalError ────────────────────────────────────

    [Fact]
    public void BangAlone_ThrowsLexicalError()
    {
        // A bare '!' (not '!=') is not valid Python — the lexer must reject it.
        var lexer = new Lexer("!");
        bool thrown = false;
        try { lexer.Tokenize(); } catch (LexicalError) { thrown = true; }
        Assert.True(thrown);
    }

    // ─── LineAndColumn tracking ───────────────────────────────────────────

    [Fact]
    public void TokenLineNumbers_AreTracked()
    {
        // The 'return' keyword is on line 2.
        var src = "def main():" + (char)10 + "    return 0";
        var lexer = new Lexer(src);
        var tokens = lexer.Tokenize();

        var ret = tokens.First(t => t.Type == TokenType.Return);
        Assert.Equal(2, ret.Line);
    }

    // ─── Bytes Literal ────────────────────────────────────────────────────

    [Fact]
    public void BytesLiteral_ProducesBytesToken()
    {
        var lexer = new Lexer("b'\\x41'");  // b'\x41' = byte value 65
        var tokens = lexer.Tokenize();

        Assert.Contains(tokens, t => t.Type == TokenType.BytesLiteral);
    }

    // ─── f-string literal ─────────────────────────────────────────────────

    [Fact]
    public void FStringLiteral_ProducesFStringToken()
    {
        var lexer = new Lexer("f\"hello {name}\"");
        var tokens = lexer.Tokenize();

        Assert.Contains(tokens, t => t.Type == TokenType.FString);
    }
}

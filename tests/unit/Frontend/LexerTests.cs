using System;
using Xunit;
using PyMCU.Compiler;
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
}

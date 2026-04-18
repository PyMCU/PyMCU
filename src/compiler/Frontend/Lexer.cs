/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *
 * -----------------------------------------------------------------------------
 * SAFETY WARNING / HIGH RISK ACTIVITIES:
 * THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
 * ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
 * NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
 * TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
 * -----------------------------------------------------------------------------
 */

using System.Text;
using PyMCU.Common;

namespace PyMCU.Frontend;

public ref struct Lexer
{
    private ReadOnlySpan<char> src;
    private int pos;
    private int line;
    private int column;

    private List<int> indentStack;
    private Queue<Token> tokenQueue;
    private bool atLineStart;
    private int parenDepth;

    private static readonly Dictionary<string, TokenType> Keywords = new()
    {
        { "def", TokenType.Def }, { "return", TokenType.Return },
        { "if", TokenType.If }, { "elif", TokenType.Elif },
        { "else", TokenType.Else }, { "while", TokenType.While },
        { "for", TokenType.For }, { "in", TokenType.In },
        { "break", TokenType.Break }, { "continue", TokenType.Continue },
        { "pass", TokenType.Pass }, { "match", TokenType.Match },
        { "case", TokenType.Case }, { "import", TokenType.Import },
        { "from", TokenType.From }, { "as", TokenType.As },
        { "True", TokenType.True }, { "False", TokenType.False },
        { "None", TokenType.None }, { "or", TokenType.Or },
        { "and", TokenType.And }, { "not", TokenType.Not },
        { "global", TokenType.Global }, { "class", TokenType.Class },
        { "yield", TokenType.Yield }, { "raise", TokenType.Raise },
        { "with", TokenType.With }, { "assert", TokenType.Assert },
        { "is", TokenType.Is }, { "lambda", TokenType.Lambda },
        { "nonlocal", TokenType.Nonlocal },
    };

    public Lexer(ReadOnlySpan<char> source)
    {
        src = source;
        pos = 0;
        line = 1;
        column = 1;
        indentStack = new List<int> { 0 };
        tokenQueue = new Queue<Token>();
        atLineStart = true;
        parenDepth = 0;
    }

    private char Peek() => pos >= src.Length ? (char)0 : src[pos];
    private char PeekNext() => pos + 1 >= src.Length ? (char)0 : src[pos + 1];

    private char Advance()
    {
        if (pos >= src.Length) return (char)0;
        char c = src[pos++];
        column++;
        return c;
    }

    private bool Match(char expected)
    {
        if (Peek() == expected)
        {
            Advance();
            return true;
        }

        return false;
    }

    private void SkipWhitespace()
    {
        while (true)
        {
            char c = Peek();
            if (c == ' ' || c == (char)9 || c == (char)13)
            {
                Advance();
            }
            else
            {
                break;
            }
        }
    }

    private void SkipComment()
    {
        if (Peek() == '#')
        {
            while (Peek() != (char)10 && Peek() != (char)0)
            {
                Advance();
            }
        }
    }

    private void Error(string message)
    {
        throw new LexicalError(message, line, column);
    }

    private void HandleIndentation()
    {
        int spaces = 0;
        int tempPos = pos;

        while (tempPos < src.Length && src[tempPos] == ' ')
        {
            spaces++;
            tempPos++;
        }

        if (tempPos >= src.Length || src[tempPos] == (char)10 || src[tempPos] == '#')
        {
            return;
        }

        for (int i = 0; i < spaces; i++) Advance();

        int currentIndent = indentStack[^1];
        if (spaces > currentIndent)
        {
            indentStack.Add(spaces);
            tokenQueue.Enqueue(new Token(TokenType.Indent, "", line, column));
        }
        else if (spaces < currentIndent)
        {
            while (spaces < indentStack[^1])
            {
                indentStack.RemoveAt(indentStack.Count - 1);
                tokenQueue.Enqueue(new Token(TokenType.Dedent, "", line, column));
            }

            if (indentStack[^1] != spaces)
            {
                Error("Unindent does not match any outer indentation level");
            }
        }

        atLineStart = false;
    }

    private static bool IsDigitForBase(char c, int b)
    {
        if (c == '_') return true;
        switch (b)
        {
            case 2: return c == '0' || c == '1';
            case 8: return c >= '0' && c <= '7';
            case 10: return char.IsDigit(c);
            case 16: return char.IsAsciiHexDigit(c);
            default: return false;
        }
    }

    private Token Number()
    {
        StringBuilder text = new();
        int b = 10;

        if (Peek() == '0')
        {
            if (pos + 1 < src.Length)
            {
                char next = char.ToLowerInvariant(PeekNext());
                if (next == 'x')
                {
                    b = 16;
                    text.Append(Advance());
                    text.Append(Advance());
                }
                else if (next == 'b')
                {
                    b = 2;
                    text.Append(Advance());
                    text.Append(Advance());
                }
                else if (next == 'o')
                {
                    b = 8;
                    text.Append(Advance());
                    text.Append(Advance());
                }
                else if (char.IsDigit(next))
                {
                    Error(
                        "leading zeros in decimal integer literals are not permitted; use an 0o prefix for octal integers");
                }
            }
        }

        while (IsDigitForBase(Peek(), b))
        {
            text.Append(Advance());
        }

        if (b == 10 && Peek() == '.')
        {
            if (char.IsDigit(PeekNext()))
            {
                text.Append(Advance());
                while (char.IsDigit(Peek()))
                {
                    text.Append(Advance());
                }
            }
        }

        string textStr = text.ToString();
        if (textStr.EndsWith('_'))
        {
            Error("decimal literal cannot end with an underscore");
        }

        if (b != 10 && textStr.Length > 2 && textStr[2] == '_')
        {
            Error("underscores not allowed immediately after base prefix");
        }

        if (char.IsLetterOrDigit(Peek()) || Peek() == '_')
        {
            char bad = Peek();
            if (b == 2) Error($"invalid digit '{bad}' in binary literal");
            if (b == 8) Error($"invalid digit '{bad}' in octal literal");
            Error($"invalid suffix '{bad}' on integer literal");
        }

        return new Token(TokenType.Number, textStr, line, column);
    }

    private Token Identifier()
    {
        StringBuilder text = new();
        while (char.IsLetterOrDigit(Peek()) || Peek() == '_')
        {
            text.Append(Advance());
        }

        string textStr = text.ToString();

        if (textStr == "b" && (Peek() == (char)34 || Peek() == (char)39))
        {
            char quote = Advance();
            StringBuilder encoded = new();
            bool firstByte = true;
            while (Peek() != quote && Peek() != (char)0)
            {
                if (Peek() == (char)10) line++;
                int byteVal = 0;
                if (Peek() == (char)92)
                {
                    Advance();
                    char esc = Advance();
                    switch (esc)
                    {
                        case 'n': byteVal = 10; break;
                        case 't': byteVal = 9; break;
                        case 'r': byteVal = 13; break;
                        case '0': byteVal = 0; break;
                        case 'x':
                        {
                            char h1 = Advance();
                            char h2 = Advance();
                            int hi = char.IsDigit(h1) ? (h1 - '0') : (char.ToLowerInvariant(h1) - 'a' + 10);
                            int lo = char.IsDigit(h2) ? (h2 - '0') : (char.ToLowerInvariant(h2) - 'a' + 10);
                            byteVal = (hi << 4) | lo;
                            break;
                        }
                        default:
                            if (esc == (char)92) byteVal = 92;
                            else if (esc == (char)39) byteVal = 39;
                            else if (esc == (char)34) byteVal = 34;
                            else byteVal = esc;
                            break;
                    }
                }
                else
                {
                    byteVal = Advance();
                }

                if (!firstByte) encoded.Append(',');
                encoded.Append(byteVal);
                firstByte = false;
            }

            if (Peek() == (char)0) Error("Unterminated bytes literal");
            Advance();
            return new Token(TokenType.BytesLiteral, encoded.ToString(), line, column);
        }

        if (textStr == "r" && (Peek() == (char)34 || Peek() == (char)39))
        {
            char quote = Advance();
            StringBuilder raw = new();
            while (Peek() != quote && Peek() != (char)0)
            {
                if (Peek() == (char)10) line++;
                raw.Append(Advance());
            }

            if (Peek() == (char)0) Error("Unterminated raw string literal");
            Advance();
            return new Token(TokenType.String, raw.ToString(), line, column);
        }

        if (textStr == "f" && (Peek() == (char)34 || Peek() == (char)39))
        {
            char quote = Advance();
            StringBuilder raw = new();
            while (Peek() != quote && Peek() != (char)0)
            {
                if (Peek() == (char)10) line++;
                if (Peek() == (char)92)
                {
                    raw.Append(Advance());
                    if (Peek() != (char)0) raw.Append(Advance());
                }
                else
                {
                    raw.Append(Advance());
                }
            }

            if (Peek() == (char)0) Error("Unterminated f-string literal");
            Advance();
            return new Token(TokenType.FString, raw.ToString(), line, column);
        }

        if (Keywords.TryGetValue(textStr, out TokenType type))
        {
            return new Token(type, textStr, line, column);
        }

        return new Token(TokenType.Identifier, textStr, line, column);
    }

    private Token StringLiteral(char quote)
    {
        StringBuilder text = new();
        while (Peek() != quote && Peek() != (char)0)
        {
            if (Peek() == (char)10) line++;
            if (Peek() == (char)92)
            {
                Advance();
                char esc = Advance();
                switch (esc)
                {
                    case 'n': text.Append((char)10); break;
                    case 't': text.Append((char)9); break;
                    case 'r': text.Append((char)13); break;
                    case '0': text.Append((char)0); break;
                    default:
                        if (esc == (char)92) text.Append((char)92);
                        else if (esc == (char)39) text.Append((char)39);
                        else if (esc == (char)34) text.Append((char)34);
                        else
                        {
                            text.Append((char)92);
                            text.Append(esc);
                        }

                        break;
                }
            }
            else
            {
                text.Append(Advance());
            }
        }

        if (Peek() == (char)0) Error("Unterminated string literal");
        Advance();
        return new Token(TokenType.String, text.ToString(), line, column);
    }

    private Token ScanToken()
    {
        SkipWhitespace();
        SkipComment();

        if (pos >= src.Length) return new Token(TokenType.EndOfFile, "", line, column);

        char c = Peek();

        if (c == (char)10)
        {
            Advance();
            line++;
            column = 1;
            atLineStart = true;
            return new Token(TokenType.Newline, ((char)92).ToString() + "n", line - 1, column);
        }

        if (char.IsAsciiLetter(c) || c == '_') return Identifier();
        if (char.IsDigit(c)) return Number();

        Advance();

        switch (c)
        {
            case '(':
                parenDepth++;
                return new Token(TokenType.LParen, "(", line, column);
            case ')':
                if (parenDepth > 0) parenDepth--;
                return new Token(TokenType.RParen, ")", line, column);
            case '[':
                parenDepth++;
                return new Token(TokenType.LBracket, "[", line, column);
            case ']':
                if (parenDepth > 0) parenDepth--;
                return new Token(TokenType.RBracket, "]", line, column);
            case ':':
                if (Match('=')) return new Token(TokenType.Walrus, ":=", line, column);
                return new Token(TokenType.Colon, ":", line, column);
            case ';':
                return new Token(TokenType.Semicolon, ";", line, column);
            case ',':
                return new Token(TokenType.Comma, ",", line, column);
            case '.':
                return new Token(TokenType.Dot, ".", line, column);
            case '@':
                return new Token(TokenType.At, "@", line, column);

            case '-':
                if (Match('>')) return new Token(TokenType.Arrow, "->", line, column);
                if (Match('=')) return new Token(TokenType.MinusEqual, "-=", line, column);
                return new Token(TokenType.Minus, "-", line, column);
            case (char)34:
                if (Peek() == (char)34 && PeekNext() == (char)34)
                {
                    Advance();
                    Advance();
                    for (;;)
                    {
                        if (Peek() == (char)0)
                        {
                            Error("Unterminated docstring");
                            break;
                        }

                        if (Peek() == (char)10) line++;
                        char ch = Advance();
                        if (ch == (char)34 && Peek() == (char)34 && PeekNext() == (char)34)
                        {
                            Advance();
                            Advance();
                            break;
                        }
                    }

                    return new Token(TokenType.String, "", line, column);
                }

                return StringLiteral((char)34);
            case (char)39:
                if (Peek() == (char)39 && PeekNext() == (char)39)
                {
                    Advance();
                    Advance();
                    for (;;)
                    {
                        if (Peek() == (char)0)
                        {
                            Error("Unterminated docstring");
                            break;
                        }

                        if (Peek() == (char)10) line++;
                        char ch = Advance();
                        if (ch == (char)39 && Peek() == (char)39 && PeekNext() == (char)39)
                        {
                            Advance();
                            Advance();
                            break;
                        }
                    }

                    return new Token(TokenType.String, "", line, column);
                }

                return StringLiteral((char)39);
            case '+':
                if (Match('=')) return new Token(TokenType.PlusEqual, "+=", line, column);
                return new Token(TokenType.Plus, "+", line, column);
            case '*':
                if (Match('*')) return new Token(TokenType.DoubleStar, "**", line, column);
                if (Match('=')) return new Token(TokenType.StarEqual, "*=", line, column);
                return new Token(TokenType.Star, "*", line, column);
            case '/':
                if (Match('=')) return new Token(TokenType.SlashEqual, "/=", line, column);
                if (Match('/'))
                {
                    if (Match('=')) return new Token(TokenType.FloorDivEqual, "//=", line, column);
                    return new Token(TokenType.FloorDiv, "//", line, column);
                }

                return new Token(TokenType.Slash, "/", line, column);
            case '%':
                if (Match('=')) return new Token(TokenType.PercentEqual, "%=", line, column);
                return new Token(TokenType.Percent, "%", line, column);

            case '=':
                if (Match('=')) return new Token(TokenType.EqualEqual, "==", line, column);
                return new Token(TokenType.Equal, "=", line, column);
            case '!':
                if (Match('=')) return new Token(TokenType.BangEqual, "!=", line, column);
                Error("Invalid syntax. Did you mean 'not' or '!='?");
                break;
            case '<':
                if (Match('<'))
                {
                    if (Match('=')) return new Token(TokenType.LShiftEqual, "<<=", line, column);
                    return new Token(TokenType.LShift, "<<", line, column);
                }

                if (Match('=')) return new Token(TokenType.LessEqual, "<=", line, column);
                return new Token(TokenType.Less, "<", line, column);
            case '>':
                if (Match('>'))
                {
                    if (Match('=')) return new Token(TokenType.RShiftEqual, ">>=", line, column);
                    return new Token(TokenType.RShift, ">>", line, column);
                }

                if (Match('=')) return new Token(TokenType.GreaterEqual, ">=", line, column);
                return new Token(TokenType.Greater, ">", line, column);

            case '&':
                if (Match('=')) return new Token(TokenType.AmpEqual, "&=", line, column);
                return new Token(TokenType.Ampersand, "&", line, column);
            case '|':
                if (Match('=')) return new Token(TokenType.PipeEqual, "|=", line, column);
                return new Token(TokenType.Pipe, "|", line, column);
            case '^':
                if (Match('=')) return new Token(TokenType.CaretEqual, "^=", line, column);
                return new Token(TokenType.Caret, "^", line, column);
            case '~':
                return new Token(TokenType.Tilde, "~", line, column);

            default:
                Error($"invalid character '{c}'");
                break;
        }

        return new Token(TokenType.Unknown, "", line, column);
    }

    public List<Token> Tokenize()
    {
        List<Token> tokens = new();

        while (true)
        {
            if (tokenQueue.Count > 0)
            {
                tokens.Add(tokenQueue.Dequeue());
                continue;
            }

            if (pos >= src.Length) break;

            if (atLineStart)
            {
                if (parenDepth > 0)
                {
                    atLineStart = false;
                    while (pos < src.Length && src[pos] == ' ') pos++;
                    if (tokenQueue.Count > 0) tokenQueue.Clear();
                }
                else
                {
                    HandleIndentation();
                    if (tokenQueue.Count > 0) continue;
                }
            }

            Token token = ScanToken();

            if (token.Type == TokenType.EndOfFile) break;

            if (token.Type == TokenType.Newline)
            {
                if (parenDepth > 0) continue;
                if (tokens.Count > 0 && tokens[^1].Type == TokenType.Newline)
                {
                    continue;
                }
            }

            tokens.Add(token);
        }

        if (tokens.Count > 0 && tokens[^1].Type != TokenType.Newline)
        {
            tokens.Add(new Token(TokenType.Newline, ((char)92).ToString() + "n", line, column));
        }

        while (indentStack.Count > 1)
        {
            indentStack.RemoveAt(indentStack.Count - 1);
            tokens.Add(new Token(TokenType.Dedent, "", line, column));
        }

        tokens.Add(new Token(TokenType.EndOfFile, "", line, column));
        return tokens;
    }
}
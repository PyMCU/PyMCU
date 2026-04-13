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

using PyMCU.Common;

namespace PyMCU.Frontend;

public class Parser
{
    private readonly IReadOnlyList<Token> tokens;
    private int pos = 0;
    private int functionDepth = 0;

    public Parser(IReadOnlyList<Token> tokens)
    {
        this.tokens = tokens;
    }

    public ProgramNode ParseProgram()
    {
        var prog = new ProgramNode();

        while (!Check(TokenType.EndOfFile))
        {
            if (Match(TokenType.Newline)) continue;

            if (Check(TokenType.From) || Check(TokenType.Import))
            {
                prog.Imports.Add(ParseImportStatement());
            }
            else if (Check(TokenType.Def) || Check(TokenType.At))
            {
                prog.Functions.Add(ParseFunction());
            }
            else
            {
                try
                {
                    prog.GlobalStatements.Add(ParseStatement());
                }
                catch (SyntaxError e)
                {
                    Error("Expected function definition, import, or valid statement. Original error: " + e.Message);
                }
            }
        }

        return prog;
    }

    public Expression ParseExpressionPublic() => ParseExpression();

    private Token Peek() => pos >= tokens.Count ? tokens[^1] : tokens[pos];

    private Token PeekNext()
    {
        int next = pos + 1;
        return next >= tokens.Count ? tokens[^1] : tokens[next];
    }

    private Token Previous() => pos == 0 ? tokens[0] : tokens[pos - 1];

    private Token Advance()
    {
        if (pos < tokens.Count) pos++;
        return tokens[pos - 1];
    }

    private bool Check(TokenType type) => Peek().Type == type;

    private bool Match(TokenType type)
    {
        if (Check(type))
        {
            Advance();
            return true;
        }

        return false;
    }

    private Token Consume(TokenType type, string errorMessage)
    {
        if (Check(type)) return Advance();
        Error(errorMessage);
        return default; // Unreachable
    }

    private void ConsumeStatementEnd()
    {
        if (Match(TokenType.Semicolon))
        {
            Match(TokenType.Newline);
            return;
        }

        if (Match(TokenType.Newline)) return;
        if (Check(TokenType.Dedent)) return;
        if (Check(TokenType.EndOfFile)) return;

        Error("Expected newline or end of block");
    }

    private void Error(string message)
    {
        var t = Peek();
        if (t.Type == TokenType.EndOfFile)
            throw new SyntaxError("Unexpected EOF while parsing", t.Line, t.Column);
        throw new SyntaxError(message, t.Line, t.Column);
    }

    private void IndentError(string message)
    {
        var t = Peek();
        throw new IndentationError(message, t.Line, t.Column);
    }

    private string ParseTypeAnnotation()
    {
        var t = Consume(TokenType.Identifier, "Expected type identifier");
        string typeStr = t.Value;

        if (Match(TokenType.LBracket))
        {
            typeStr += "[";
            if (Check(TokenType.Identifier))
            {
                var inner = Consume(TokenType.Identifier, "Expected inner type");
                typeStr += inner.Value;
            }
            else if (Check(TokenType.Number))
            {
                var inner = Consume(TokenType.Number, "Expected array size");
                typeStr += inner.Value;
            }
            else
            {
                Error("Expected type name or array size inside '['");
            }

            Consume(TokenType.RBracket, "Expected ']'");
            typeStr += "]";
        }

        return typeStr;
    }

    private FunctionDef ParseFunction()
    {
        bool isInline = false;
        bool isInterrupt = false;
        int vector = 0;
        bool isPropertyGetter = false;
        bool isPropertySetter = false;
        string propSetterOf = "";
        bool isExtern = false;
        string externSymbol = "";

        while (Check(TokenType.At))
        {
            Advance();
            var decorator = Consume(TokenType.Identifier, "Expected decorator name");
            if (decorator.Value == "inline")
            {
                isInline = true;
            }
            else if (decorator.Value == "extern")
            {
                isExtern = true;
                Consume(TokenType.LParen, "Expected '(' after @extern");
                var symTok = Consume(TokenType.String,
                    "Expected C symbol name as a string literal in @extern(" + (char)34 + "name" + (char)34 + ")");
                externSymbol = symTok.Value;
                Consume(TokenType.RParen, "Expected ')' after @extern symbol name");
            }
            else if (decorator.Value == "property")
            {
                isPropertyGetter = true;
                isInline = true;
            }
            else if (Check(TokenType.Dot))
            {
                Advance();
                var suffix = Consume(TokenType.Identifier, "Expected 'setter' or 'getter' after '.'");
                if (suffix.Value == "setter")
                {
                    isPropertySetter = true;
                    isInline = true;
                    propSetterOf = decorator.Value;
                }
                else if (suffix.Value == "getter")
                {
                    isPropertyGetter = true;
                    isInline = true;
                }
                else
                {
                    Error("Unknown property modifier '@" + decorator.Value + "." + suffix.Value + "'");
                }
            }
            else if (decorator.Value == "interrupt")
            {
                isInterrupt = true;
                vector = 0x04;

                if (Check(TokenType.LParen))
                {
                    Advance();
                    var vectorToken = Consume(TokenType.Number, "Expected vector address");
                    string text = vectorToken.Value;
                    int b = 10;
                    if (text.Length >= 2 && text[0] == '0' && (text[1] == 'x' || text[1] == 'X'))
                    {
                        b = 16;
                        text = text.Substring(2);
                    }

                    try
                    {
                        vector = Convert.ToInt32(text, b);
                    }
                    catch
                    {
                        Error("Invalid vector address");
                    }

                    Consume(TokenType.RParen, "Expected ')'");
                }
            }
            else if (decorator.Value == "staticmethod")
            {
                // Ignored
            }
            else
            {
                Error("Unknown decorator: " + decorator.Value);
            }

            Consume(TokenType.Newline, "Expected newline after decorator");
        }

        Consume(TokenType.Def, "Expected 'def'");
        if (!Check(TokenType.Identifier))
        {
            Error("Expected function name, but found " + Peek().Value + " (Type: " + Peek().Type + ")");
        }

        var nameToken = Advance();
        string name = nameToken.Value;

        if (name.Length >= 4 && name.StartsWith("__") && name.EndsWith("__"))
        {
            isInline = true;
        }

        Consume(TokenType.LParen, "Expected '(' after function name");
        var parameters = ParseParameters();
        Consume(TokenType.RParen, "Expected ')' after parameters");

        string returnType = "void";
        if (Match(TokenType.Arrow))
        {
            returnType = ParseTypeAnnotation();
        }

        Consume(TokenType.Colon, "Expected ':' before function body");
        Consume(TokenType.Newline, "Expected newline after function definition");

        functionDepth++;
        var body = ParseBlock();
        functionDepth--;

        var func = new FunctionDef(name, parameters, returnType, body, isInline, isInterrupt, vector)
        {
            IsPropertyGetter = isPropertyGetter,
            IsPropertySetter = isPropertySetter,
            PropertyName = propSetterOf,
            IsExtern = isExtern,
            ExternSymbol = externSymbol
        };
        return func;
    }

    private ClassDef ParseClassDefinition()
    {
        Consume(TokenType.Class, "Expected 'class'");
        string name = Consume(TokenType.Identifier, "Expected class name").Value;
        var bases = new List<string>();
        if (Match(TokenType.LParen))
        {
            if (!Check(TokenType.RParen))
            {
                do
                {
                    bases.Add(Consume(TokenType.Identifier, "Expected base class name").Value);
                } while (Match(TokenType.Comma));
            }

            Consume(TokenType.RParen, "Expected ')'");
        }

        Consume(TokenType.Colon, "Expected ':'");
        Consume(TokenType.Newline, "Expected newline after class definition");
        var body = ParseBlock();
        return new ClassDef(name, bases, body) { IsStatic = true };
    }

    private List<Param> ParseParameters()
    {
        var parameters = new List<Param>();

        if (Check(TokenType.RParen)) return parameters;

        do
        {
            var name = Consume(TokenType.Identifier, "Expected parameter name");
            string type = "";
            if (Match(TokenType.Colon))
            {
                type = ParseTypeAnnotation();
            }

            Expression? defaultVal = null;
            if (Match(TokenType.Equal))
            {
                defaultVal = ParseExpression();
            }

            parameters.Add(new Param(name.Value, type, defaultVal));
        } while (Match(TokenType.Comma));

        return parameters;
    }

    private Block ParseBlock()
    {
        if (!Match(TokenType.Indent)) IndentError("Expected an indented block");

        var block = new Block();
        while (!Check(TokenType.Dedent) && !Check(TokenType.EndOfFile))
        {
            if (Match(TokenType.Newline)) continue;
            block.Statements.Add(ParseStatement());
        }

        if (!Match(TokenType.Dedent) && !Check(TokenType.EndOfFile))
        {
            IndentError("Unindent does not match any outer indentation level");
        }

        return block;
    }

    private Statement ParseStatement()
    {
        if (Check(TokenType.If)) return ParseIfStatement();
        if (Check(TokenType.Match)) return ParseMatchStatement();
        if (Check(TokenType.While)) return ParseWhileStatement();
        if (Check(TokenType.For)) return ParseForStatement();
        if (Check(TokenType.Def) || Check(TokenType.At))
        {
            if (functionDepth > 0)
            {
                bool isInlineDecorator = Check(TokenType.At) && PeekNext().Value == "inline";
                if (!isInlineDecorator)
                {
                    Error("Nested function definitions require the @inline decorator");
                }
            }

            return ParseFunction();
        }

        if (Check(TokenType.Return)) return ParseReturnStatement();
        if (Check(TokenType.Import) || Check(TokenType.From)) return ParseImportStatement();
        if (Check(TokenType.Global)) return ParseGlobalStatement();
        if (Check(TokenType.Nonlocal)) return ParseNonlocalStatement();
        if (Check(TokenType.Class)) return ParseClassDefinition();

        if (Match(TokenType.Break))
        {
            ConsumeStatementEnd();
            return new BreakStmt();
        }

        if (Match(TokenType.Continue))
        {
            ConsumeStatementEnd();
            return new ContinueStmt();
        }

        if (Match(TokenType.Pass))
        {
            ConsumeStatementEnd();
            return new PassStmt();
        }

        if (Check(TokenType.Raise)) return ParseRaiseStatement();
        if (Check(TokenType.With)) return ParseWithStatement();
        if (Check(TokenType.Assert)) return ParseAssertStatement();

        return ParseSimpleStatement();
    }

    private Statement ParseReturnStatement()
    {
        int line = Peek().Line;
        Consume(TokenType.Return, "Expected 'return'");
        Expression? value = null;
        if (!Check(TokenType.Newline) && !Check(TokenType.Semicolon))
        {
            value = ParseExpression();
        }

        ConsumeStatementEnd();
        return new ReturnStmt(value) { Line = line };
    }

    private Statement ParseRaiseStatement()
    {
        int line = Peek().Line;
        Consume(TokenType.Raise, "Expected 'raise'");
        string errorType = Consume(TokenType.Identifier, "Expected error type after 'raise'").Value;
        Consume(TokenType.LParen, "Expected '(' after error type");
        string message = "";
        if (Check(TokenType.String))
        {
            message = Advance().Value;
        }

        Consume(TokenType.RParen, "Expected ')' after error message");
        ConsumeStatementEnd();
        return new RaiseStmt(errorType, message) { Line = line };
    }

    private Statement ParseWithStatement()
    {
        int line = Peek().Line;
        Consume(TokenType.With, "Expected 'with'");

        var items = new List<(Expression Ctx, string AsName)>();

        do
        {
            var ctx = ParseExpression();
            string asName = "";
            if (Match(TokenType.As))
            {
                asName = Consume(TokenType.Identifier, "Expected name after 'as'").Value;
            }

            items.Add((ctx, asName));
        } while (Match(TokenType.Comma));

        Consume(TokenType.Colon, "Expected ':' after 'with' header");
        ConsumeStatementEnd();
        Statement body = ParseBlock();

        for (int i = items.Count - 1; i >= 0; --i)
        {
            var ws = new WithStmt(items[i].Ctx, items[i].AsName, body) { Line = line };
            body = ws;
        }

        return body;
    }

    private Statement ParseAssertStatement()
    {
        int line = Peek().Line;
        Consume(TokenType.Assert, "Expected 'assert'");
        var cond = ParseExpression();
        string message = "";
        if (Match(TokenType.Comma))
        {
            if (Check(TokenType.String))
            {
                message = Advance().Value;
            }
            else
            {
                ParseExpression();
            }
        }

        ConsumeStatementEnd();
        return new AssertStmt(cond, message) { Line = line };
    }

    private ImportStmt ParseImportStatement()
    {
        if (Match(TokenType.Import))
        {
            string modName = Consume(TokenType.Identifier, "Expected module name").Value;
            while (Match(TokenType.Dot))
            {
                modName += "." + Consume(TokenType.Identifier, "Expected part name").Value;
            }

            var stmt = new ImportStmt(modName, new List<string>(), 0);
            if (Match(TokenType.As))
            {
                stmt.ModuleAlias = Consume(TokenType.Identifier, "Expected alias name after 'as'").Value;
            }

            return stmt;
        }

        Consume(TokenType.From, "Expected 'from'");

        int relativeLevel = 0;
        while (Match(TokenType.Dot)) relativeLevel++;

        string modNameStr = "";
        if (Check(TokenType.Identifier))
        {
            modNameStr = Consume(TokenType.Identifier, "Expected module name").Value;
            while (Match(TokenType.Dot))
            {
                modNameStr += "." + Consume(TokenType.Identifier, "Expected part name").Value;
            }
        }
        else if (relativeLevel == 0)
        {
            Error("Expected module name in absolute import");
        }

        Consume(TokenType.Import, "Expected 'import'");

        var symbols = new List<string>();
        var symAliases = new Dictionary<string, string>();

        if (Match(TokenType.Star))
        {
            symbols.Add("*");
        }
        else
        {
            // PEP 328: symbol list may be wrapped in parentheses, allowing
            // multi-line imports.  The lexer already suppresses newlines while
            // parenDepth > 0, so no special newline handling is needed here.
            bool parenthesised = Match(TokenType.LParen);

            do
            {
                // A trailing comma before ')' is legal; stop when we see ')'.
                if (parenthesised && Check(TokenType.RParen)) break;

                var sym = Consume(TokenType.Identifier, "Expected symbol name");
                symbols.Add(sym.Value);
                if (Match(TokenType.As))
                {
                    var alias = Consume(TokenType.Identifier, "Expected alias name after 'as'");
                    symAliases[sym.Value] = alias.Value;
                }
            } while (Match(TokenType.Comma));

            if (parenthesised)
                Consume(TokenType.RParen, "Expected ')' to close parenthesised import list");
        }

        ConsumeStatementEnd();
        return new ImportStmt(modNameStr, symbols, relativeLevel) { Aliases = symAliases };
    }

    private Statement ParseGlobalStatement()
    {
        int line = Peek().Line;
        Consume(TokenType.Global, "Expected 'global'");
        var names = new List<string>();

        do
        {
            names.Add(Consume(TokenType.Identifier, "Expected variable name").Value);
        } while (Match(TokenType.Comma));

        ConsumeStatementEnd();
        return new GlobalStmt(names) { Line = line };
    }

    private Statement ParseNonlocalStatement()
    {
        int line = Peek().Line;
        Consume(TokenType.Nonlocal, "Expected 'nonlocal'");
        var names = new List<string>();

        do
        {
            names.Add(Consume(TokenType.Identifier, "Expected variable name").Value);
        } while (Match(TokenType.Comma));

        ConsumeStatementEnd();
        return new NonlocalStmt(names) { Line = line };
    }

    private Statement ParseIfStatement()
    {
        int line = Peek().Line;
        Consume(TokenType.If, "Expected 'if'");
        var condition = ParseExpression();
        Consume(TokenType.Colon, "Expected ':'");
        Consume(TokenType.Newline, "Expected newline");

        var thenBranch = ParseBlock();

        var elifBranches = new List<(Expression, Statement)>();
        while (Match(TokenType.Elif))
        {
            var elifCond = ParseExpression();
            Consume(TokenType.Colon, "Expected ':'");
            Consume(TokenType.Newline, "Expected newline");
            var elifBlock = ParseBlock();
            elifBranches.Add((elifCond, elifBlock));
        }

        Statement? elseBranch = null;
        if (Match(TokenType.Else))
        {
            Consume(TokenType.Colon, "Expected ':'");
            Consume(TokenType.Newline, "Expected newline");
            elseBranch = ParseBlock();
        }

        return new IfStmt(condition, thenBranch, elifBranches, elseBranch) { Line = line };
    }

    private Statement ParseMatchStatement()
    {
        Consume(TokenType.Match, "Expected 'match'");
        var target = ParseExpression();
        Consume(TokenType.Colon, "Expected ':'");
        Consume(TokenType.Newline, "Expected newline");

        if (!Match(TokenType.Indent)) IndentError("Expected indented block for match cases");

        var branches = new List<CaseBranch>();
        while (!Check(TokenType.Dedent) && !Check(TokenType.EndOfFile))
        {
            if (Match(TokenType.Newline)) continue;

            Consume(TokenType.Case, "Expected 'case'");

            Expression? pattern = null;
            string captureName = "";

            if (Check(TokenType.Identifier) && Peek().Value == "_")
            {
                Advance();
            }
            else if (Check(TokenType.Identifier) &&
                     (pos + 1 >= tokens.Count ||
                      (tokens[pos + 1].Type != TokenType.Dot && tokens[pos + 1].Type != TokenType.LParen)))
            {
                int lookahead = pos + 1;
                while (lookahead < tokens.Count && tokens[lookahead].Type == TokenType.Newline)
                    ++lookahead;
                bool nextIsOr = lookahead < tokens.Count && tokens[lookahead].Type == TokenType.Pipe;
                if (!nextIsOr)
                {
                    captureName = Peek().Value;
                    Advance();
                }
                else
                {
                    pattern = ParseExpression();
                }
            }
            else
            {
                pattern = ParseExpression();
            }

            if (Check(TokenType.As))
            {
                Advance();
                if (!Check(TokenType.Identifier))
                    throw new Exception("Expected identifier after 'as' in case pattern");
                captureName = Peek().Value;
                Advance();
            }

            Expression? guard = null;
            if (Check(TokenType.If))
            {
                Advance();
                guard = ParseExpression();
            }

            Consume(TokenType.Colon, "Expected ':'");

            Block body;
            if (Check(TokenType.Newline))
            {
                Advance();
                body = ParseBlock();
            }
            else
            {
                body = new Block();
                body.Statements.Add(ParseStatement());
                if (Check(TokenType.Newline)) Advance();
            }

            branches.Add(new CaseBranch { Pattern = pattern, Guard = guard, CaptureName = captureName, Body = body });
        }

        if (!Match(TokenType.Dedent) && !Check(TokenType.EndOfFile))
        {
            IndentError("Unindent does not match any outer indentation level");
        }

        return new MatchStmt(target, branches);
    }

    private Statement ParseWhileStatement()
    {
        int line = Peek().Line;
        Consume(TokenType.While, "Expected 'while'");
        var condition = ParseExpression();
        Consume(TokenType.Colon, "Expected ':'");
        Consume(TokenType.Newline, "Expected newline");
        var body = ParseBlock();
        return new WhileStmt(condition, body) { Line = line };
    }

    private Statement ParseForStatement()
    {
        int line = Peek().Line;
        Consume(TokenType.For, "Expected 'for'");
        var varTok = Consume(TokenType.Identifier, "Expected loop variable");

        string var2Name = "";
        if (Match(TokenType.Comma))
        {
            var2Name = Consume(TokenType.Identifier, "Expected second loop variable").Value;
        }

        Consume(TokenType.In, "Expected 'in'");

        if (Check(TokenType.Identifier) && Peek().Value == "range")
        {
            Consume(TokenType.Identifier, "Expected 'range'");
            Consume(TokenType.LParen, "Expected '('");

            var arg1 = ParseExpression();
            Expression? arg2 = null;
            Expression? arg3 = null;
            if (Match(TokenType.Comma))
            {
                arg2 = ParseExpression();
                if (Match(TokenType.Comma))
                {
                    arg3 = ParseExpression();
                }
            }

            Consume(TokenType.RParen, "Expected ')'");
            Consume(TokenType.Colon, "Expected ':'");
            Consume(TokenType.Newline, "Expected newline");
            var blockBody = ParseBlock();

            Expression? start = null, stop = null, step = null;
            if (arg2 == null)
            {
                stop = arg1;
            }
            else if (arg3 == null)
            {
                start = arg1;
                stop = arg2;
            }
            else
            {
                start = arg1;
                stop = arg2;
                step = arg3;
            }

            var stmt = new ForStmt(varTok.Value, start, stop, step, blockBody) { Var2Name = var2Name, Line = line };
            return stmt;
        }

        var iterable = ParseExpression();
        Consume(TokenType.Colon, "Expected ':'");
        Consume(TokenType.Newline, "Expected newline");
        var ibody = ParseBlock();

        return new ForStmt(varTok.Value, iterable, ibody) { Var2Name = var2Name, Line = line };
    }

    private Statement ParseSimpleStatement()
    {
        int line = Peek().Line;
        if (Check(TokenType.Return)) return ParseReturnStatement();

        if (Match(TokenType.Pass))
        {
            ConsumeStatementEnd();
            return new PassStmt() { Line = line };
        }

        if (Match(TokenType.Break))
        {
            ConsumeStatementEnd();
            return new BreakStmt() { Line = line };
        }

        if (Match(TokenType.Continue))
        {
            ConsumeStatementEnd();
            return new ContinueStmt() { Line = line };
        }

        return ParseAssignmentOrDeclaration();
    }

    private Statement ParseAssignmentOrDeclaration()
    {
        int line = Peek().Line;
        var expr = ParseExpression();

        if (Check(TokenType.Comma))
        {
            if (expr is VariableExpr firstVar)
            {
                var targets = new List<string> { firstVar.Name };
                int starredIndex = -1;
                while (Match(TokenType.Comma))
                {
                    if (Check(TokenType.Star))
                    {
                        Advance();
                        if (starredIndex != -1)
                            throw new Exception("Only one starred expression allowed in assignment");
                        var t = Consume(TokenType.Identifier, "Expected name after '*' in tuple unpack");
                        starredIndex = targets.Count;
                        targets.Add(t.Value);
                    }
                    else
                    {
                        var t = Consume(TokenType.Identifier, "Expected variable name in tuple unpack");
                        targets.Add(t.Value);
                    }
                }

                Consume(TokenType.Equal, "Expected '=' in tuple unpack assignment");
                var valueExpr = ParseExpression();
                ConsumeStatementEnd();
                return new TupleUnpackStmt(targets, valueExpr, starredIndex) { Line = line };
            }
        }

        if (Match(TokenType.Colon))
        {
            if (!(expr is VariableExpr varExpr)) Error("Only simple variables can be annotated with types");
            string name = ((VariableExpr)expr).Name;
            string type = ParseTypeAnnotation();

            Expression? init = null;
            if (Match(TokenType.Equal))
            {
                init = ParseExpression();
            }

            ConsumeStatementEnd();

            if (type.Contains('['))
            {
                return new AnnAssign(name, type, init) { Line = line };
            }

            return new VarDecl(name, type, init) { Line = line };
        }

        if (Match(TokenType.Equal))
        {
            var value = ParseExpression();
            if (Check(TokenType.Equal))
            {
                var targets = new List<Expression> { expr };
                var rhs = value;
                while (Match(TokenType.Equal))
                {
                    targets.Add(rhs);
                    rhs = ParseExpression();
                }

                ConsumeStatementEnd();

                var block = new Block();
                var inner = targets[^1];
                string innerName = inner is VariableExpr ve ? ve.Name : "";

                block.Statements.Add(new AssignStmt(inner, rhs) { Line = line });

                if (!string.IsNullOrEmpty(innerName))
                {
                    for (int ci = targets.Count - 2; ci >= 0; --ci)
                    {
                        block.Statements.Add(new AssignStmt(targets[ci], new VariableExpr(innerName)) { Line = line });
                    }
                }

                return block;
            }

            ConsumeStatementEnd();
            return new AssignStmt(expr, value) { Line = line };
        }

        AugOp? augOp = null;
        if (Match(TokenType.PlusEqual)) augOp = AugOp.Add;
        else if (Match(TokenType.MinusEqual)) augOp = AugOp.Sub;
        else if (Match(TokenType.StarEqual)) augOp = AugOp.Mul;
        else if (Match(TokenType.SlashEqual)) augOp = AugOp.Div;
        else if (Match(TokenType.FloorDivEqual)) augOp = AugOp.FloorDiv;
        else if (Match(TokenType.PercentEqual)) augOp = AugOp.Mod;
        else if (Match(TokenType.AmpEqual)) augOp = AugOp.BitAnd;
        else if (Match(TokenType.PipeEqual)) augOp = AugOp.BitOr;
        else if (Match(TokenType.CaretEqual)) augOp = AugOp.BitXor;
        else if (Match(TokenType.LShiftEqual)) augOp = AugOp.LShift;
        else if (Match(TokenType.RShiftEqual)) augOp = AugOp.RShift;

        if (augOp.HasValue)
        {
            var value = ParseExpression();
            ConsumeStatementEnd();
            return new AugAssignStmt(expr, augOp.Value, value) { Line = line };
        }

        ConsumeStatementEnd();
        return new ExprStmt(expr) { Line = line };
    }

    private Expression ParseExpression()
    {
        if (Match(TokenType.Yield))
        {
            var value = ParseExpression();
            return new YieldExpr(value);
        }

        var left = ParseLogicalOr();
        if (Match(TokenType.If))
        {
            var condition = ParseExpression();
            Consume(TokenType.Else, "Expected 'else' in ternary expression");
            var falseVal = ParseExpression();
            return new TernaryExpr(left, condition, falseVal);
        }

        return left;
    }

    private Expression ParseLogicalOr()
    {
        var left = ParseLogicalAnd();
        while (Match(TokenType.Or))
        {
            var right = ParseLogicalAnd();
            left = new BinaryExpr(left, BinaryOp.Or, right);
        }

        return left;
    }

    private Expression ParseLogicalAnd()
    {
        var left = ParseLogicalNot();
        while (Match(TokenType.And))
        {
            var right = ParseLogicalNot();
            left = new BinaryExpr(left, BinaryOp.And, right);
        }

        return left;
    }

    private Expression ParseLogicalNot()
    {
        if (Check(TokenType.Not) && PeekNext().Type != TokenType.In)
        {
            Advance();
            var operand = ParseLogicalNot();
            return new UnaryExpr(UnaryOp.Not, operand);
        }

        return ParseComparison();
    }

    private Expression ParseComparison()
    {
        var left = ParseBitwiseOr();

        while (Check(TokenType.EqualEqual) || Check(TokenType.BangEqual) ||
               Check(TokenType.Less) || Check(TokenType.LessEqual) ||
               Check(TokenType.Greater) || Check(TokenType.GreaterEqual) ||
               Check(TokenType.In) || Check(TokenType.Is) ||
               (Check(TokenType.Not) && PeekNext().Type == TokenType.In))
        {
            BinaryOp op = BinaryOp.Equal;

            if (Check(TokenType.Not))
            {
                Advance();
                Consume(TokenType.In, "Expected 'in' after 'not'");
                op = BinaryOp.NotIn;
            }
            else if (Check(TokenType.Is))
            {
                Advance();
                if (Check(TokenType.Not))
                {
                    Advance();
                    op = BinaryOp.IsNot;
                }
                else
                {
                    op = BinaryOp.Is;
                }
            }
            else if (Check(TokenType.In))
            {
                Advance();
                op = BinaryOp.In;
            }
            else
            {
                var opToken = Advance();
                switch (opToken.Type)
                {
                    case TokenType.EqualEqual: op = BinaryOp.Equal; break;
                    case TokenType.BangEqual: op = BinaryOp.NotEqual; break;
                    case TokenType.Less: op = BinaryOp.Less; break;
                    case TokenType.LessEqual: op = BinaryOp.LessEq; break;
                    case TokenType.Greater: op = BinaryOp.Greater; break;
                    case TokenType.GreaterEqual: op = BinaryOp.GreaterEq; break;
                }
            }

            var right = ParseBitwiseOr();
            left = new BinaryExpr(left, op, right);
        }

        return left;
    }

    private Expression ParseBitwiseOr()
    {
        var left = ParseBitwiseXor();
        while (Match(TokenType.Pipe))
        {
            var right = ParseBitwiseXor();
            left = new BinaryExpr(left, BinaryOp.BitOr, right);
        }

        return left;
    }

    private Expression ParseBitwiseXor()
    {
        var left = ParseBitwiseAnd();
        while (Match(TokenType.Caret))
        {
            var right = ParseBitwiseAnd();
            left = new BinaryExpr(left, BinaryOp.BitXor, right);
        }

        return left;
    }

    private Expression ParseBitwiseAnd()
    {
        var left = ParseShift();
        while (Match(TokenType.Ampersand))
        {
            var right = ParseShift();
            left = new BinaryExpr(left, BinaryOp.BitAnd, right);
        }

        return left;
    }

    private Expression ParseShift()
    {
        var left = ParseAdditive();
        while (Check(TokenType.LShift) || Check(TokenType.RShift))
        {
            var opToken = Advance();
            BinaryOp op = opToken.Type == TokenType.LShift ? BinaryOp.LShift : BinaryOp.RShift;
            var right = ParseAdditive();
            left = new BinaryExpr(left, op, right);
        }

        return left;
    }

    private Expression ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (Check(TokenType.Plus) || Check(TokenType.Minus))
        {
            var opToken = Advance();
            BinaryOp op = opToken.Type == TokenType.Plus ? BinaryOp.Add : BinaryOp.Sub;
            var right = ParseMultiplicative();
            left = new BinaryExpr(left, op, right);
        }

        return left;
    }

    private Expression ParseMultiplicative()
    {
        var left = ParsePower();
        while (Check(TokenType.Star) || Check(TokenType.Slash) ||
               Check(TokenType.FloorDiv) || Check(TokenType.Percent))
        {
            var opToken = Advance();
            BinaryOp op = BinaryOp.Mod;
            if (opToken.Type == TokenType.Star) op = BinaryOp.Mul;
            else if (opToken.Type == TokenType.Slash) op = BinaryOp.Div;
            else if (opToken.Type == TokenType.FloorDiv) op = BinaryOp.FloorDiv;

            var right = ParsePower();
            left = new BinaryExpr(left, op, right);
        }

        return left;
    }

    private Expression ParsePower()
    {
        var left = ParseUnary();
        if (Check(TokenType.DoubleStar))
        {
            Advance();
            var right = ParsePower();
            left = new BinaryExpr(left, BinaryOp.Pow, right);
        }

        return left;
    }

    private Expression ParseUnary()
    {
        if (Check(TokenType.Minus))
        {
            Advance();
            return new UnaryExpr(UnaryOp.Negate, ParseUnary());
        }

        if (Check(TokenType.Tilde))
        {
            Advance();
            return new UnaryExpr(UnaryOp.BitNot, ParseUnary());
        }

        if (Check(TokenType.Not))
        {
            Advance();
            return new UnaryExpr(UnaryOp.Not, ParseUnary());
        }

        return ParsePostfix();
    }

    private Expression ParsePostfix()
    {
        var expr = ParsePrimary();

        while (true)
        {
            if (Match(TokenType.LParen))
            {
                var args = new List<Expression>();
                while (Check(TokenType.Newline)) Advance();
                if (!Check(TokenType.RParen))
                {
                    do
                    {
                        while (Check(TokenType.Newline)) Advance();
                        if (Check(TokenType.RParen)) break;
                        if (Check(TokenType.Identifier) && pos + 1 < tokens.Count &&
                            tokens[pos + 1].Type == TokenType.Equal)
                        {
                            string name = Consume(TokenType.Identifier, "Expected argument name").Value;
                            Consume(TokenType.Equal, "Expected '='");
                            var value = ParseExpression();
                            args.Add(new KeywordArgExpr(name, value));
                        }
                        else
                        {
                            args.Add(ParseExpression());
                        }
                    } while (Match(TokenType.Comma));

                    while (Check(TokenType.Newline)) Advance();
                }

                Consume(TokenType.RParen, "Expected ')'");
                expr = new CallExpr(expr, args);
            }
            else if (Match(TokenType.LBracket))
            {
                Expression? index;
                if (Check(TokenType.Colon))
                {
                    Advance();
                    Expression? stop = Check(TokenType.RBracket) || Check(TokenType.Colon) ? null : ParseExpression();
                    Expression? step = null;
                    if (Match(TokenType.Colon))
                    {
                        step = Check(TokenType.RBracket) ? null : ParseExpression();
                    }

                    index = new SliceExpr(null, stop, step);
                }
                else
                {
                    var first = ParseExpression();
                    if (Check(TokenType.Colon))
                    {
                        Advance();
                        Expression? stop = Check(TokenType.RBracket) || Check(TokenType.Colon)
                            ? null
                            : ParseExpression();
                        Expression? step = null;
                        if (Match(TokenType.Colon))
                        {
                            step = Check(TokenType.RBracket) ? null : ParseExpression();
                        }

                        index = new SliceExpr(first, stop, step);
                    }
                    else
                    {
                        index = first;
                    }
                }

                Consume(TokenType.RBracket, "Expected ']'");
                expr = new IndexExpr(expr, index);
            }
            else if (Match(TokenType.Dot))
            {
                var member = Consume(TokenType.Identifier, "Expected member name");
                expr = new MemberAccessExpr(expr, member.Value);
            }
            else
            {
                break;
            }
        }

        return expr;
    }

    private Expression ParsePrimary()
    {
        if (Match(TokenType.Lambda))
        {
            var lparams = new List<Param>();
            while (!Check(TokenType.Colon) && !Check(TokenType.EndOfFile))
            {
                string pname = Consume(TokenType.Identifier, "Expected parameter name").Value;
                string ptype = "uint8";
                int colonPos = pos;
                if (Match(TokenType.Colon))
                {
                    if (Check(TokenType.Identifier))
                    {
                        int next = pos + 1;
                        while (next < tokens.Count && tokens[next].Type == TokenType.LBracket)
                        {
                            next += 3;
                        }

                        if (next < tokens.Count &&
                            (tokens[next].Type == TokenType.Comma || tokens[next].Type == TokenType.Colon))
                        {
                            ptype = Advance().Value;
                            if (Check(TokenType.LBracket))
                            {
                                Advance();
                                ptype += "[" + Advance().Value + "]";
                                Consume(TokenType.RBracket, "Expected ']'");
                            }
                        }
                        else
                        {
                            pos = colonPos;
                        }
                    }
                    else
                    {
                        pos = colonPos;
                    }
                }

                lparams.Add(new Param(pname, ptype));
                if (!Match(TokenType.Comma)) break;
            }

            Consume(TokenType.Colon, "Expected ':' after lambda parameters");
            var body = ParseExpression();
            return new LambdaExpr(lparams, body);
        }

        if (Match(TokenType.True)) return new BooleanLiteral(true);
        if (Match(TokenType.False)) return new BooleanLiteral(false);
        if (Match(TokenType.None)) return new IntegerLiteral(-1);

        if (Match(TokenType.Identifier))
        {
            Token t = Previous();
            if (Check(TokenType.Walrus))
            {
                Advance();
                var val = ParseExpression();
                return new WalrusExpr(t.Value, val);
            }

            return new VariableExpr(t.Value);
        }

        if (Match(TokenType.BytesLiteral))
        {
            string encoded = Previous().Value;
            var elems = new List<Expression>();
            if (!string.IsNullOrEmpty(encoded))
            {
                int start = 0;
                while (start <= encoded.Length)
                {
                    int comma = encoded.IndexOf(',', start);
                    if (comma == -1) comma = encoded.Length;
                    string tok = encoded.Substring(start, comma - start);
                    if (!string.IsNullOrEmpty(tok))
                    {
                        elems.Add(new IntegerLiteral(int.Parse(tok)));
                    }

                    start = comma + 1;
                }
            }

            return new ListExpr(elems);
        }

        if (Match(TokenType.String))
        {
            return new StringLiteral(Previous().Value);
        }

        if (Match(TokenType.FString))
        {
            string raw = Previous().Value;
            var parts = new List<FStringPart>();
            int i = 0;
            while (i < raw.Length)
            {
                if (raw[i] == '{')
                {
                    int j = i + 1;
                    while (j < raw.Length && raw[j] != '}') j++;
                    if (j >= raw.Length) Error("Unterminated '{' in f-string");
                    string exprSrc = raw.Substring(i + 1, j - i - 1);

                    var subLex = new Lexer(exprSrc.AsSpan());
                    var subTokens = subLex.Tokenize();
                    var subParser = new Parser(subTokens);
                    var innerExpr = subParser.ParseExpressionPublic();

                    parts.Add(new FStringPart { IsExpr = true, Expr = innerExpr });
                    i = j + 1;
                }
                else if (raw[i] == '}')
                {
                    Error("Unexpected '}' in f-string");
                }
                else
                {
                    string text = "";
                    while (i < raw.Length && raw[i] != '{' && raw[i] != '}')
                    {
                        if (raw[i] == (char)92 && i + 1 < raw.Length)
                        {
                            char esc = raw[i + 1];
                            switch (esc)
                            {
                                case 'n': text += (char)10; break;
                                case 't': text += (char)9; break;
                                case 'r': text += (char)13; break;
                                case '0': text += (char)0; break;
                                default:
                                    if (esc == (char)92) text += (char)92;
                                    else if (esc == (char)39) text += (char)39;
                                    else if (esc == (char)34) text += (char)34;
                                    else
                                    {
                                        text += (char)92;
                                        text += esc;
                                    }

                                    break;
                            }

                            i += 2;
                        }
                        else
                        {
                            text += raw[i++];
                        }
                    }

                    if (!string.IsNullOrEmpty(text))
                    {
                        parts.Add(new FStringPart { IsExpr = false, Text = text });
                    }
                }
            }

            return new FStringExpr(parts);
        }

        if (Match(TokenType.Number))
        {
            Token t = Previous();
            string text = t.Value.Replace("_", "");

            int b = 10;
            int offset = 0;

            if (text.Length >= 2 && text[0] == '0')
            {
                char prefix = char.ToLowerInvariant(text[1]);
                if (prefix == 'x')
                {
                    b = 16;
                    offset = 2;
                }
                else if (prefix == 'b')
                {
                    b = 2;
                    offset = 2;
                }
                else if (prefix == 'o')
                {
                    b = 8;
                    offset = 2;
                }
            }

            try
            {
                if (b == 10 && text.Contains('.'))
                {
                    double valD = double.Parse(text);
                    return new FloatLiteral(valD);
                }

                int val = Convert.ToInt32(text.Substring(offset), b);
                return new IntegerLiteral(val);
            }
            catch (OverflowException)
            {
                Error("Integer literal is too large: '" + t.Value + "'");
            }
            catch (FormatException)
            {
                Error("Invalid integer literal: '" + t.Value + "'");
            }

            return null!;
        }

        if (Match(TokenType.LParen))
        {
            if (Check(TokenType.RParen))
            {
                Advance();
                return new TupleExpr(new List<Expression>());
            }

            var first = ParseExpression();
            if (Check(TokenType.Comma))
            {
                var elems = new List<Expression> { first };
                while (Match(TokenType.Comma))
                {
                    if (Check(TokenType.RParen)) break;
                    elems.Add(ParseExpression());
                }

                Consume(TokenType.RParen, "Expected ')'");
                return new TupleExpr(elems);
            }

            Consume(TokenType.RParen, "Expected ')'");
            return first;
        }

        if (Match(TokenType.LBracket))
        {
            if (Check(TokenType.RBracket))
            {
                Advance();
                return new ListExpr(new List<Expression>());
            }

            var first = ParseExpression();
            if (Match(TokenType.For))
            {
                var varTok = Consume(TokenType.Identifier, "Expected loop variable");
                Consume(TokenType.In, "Expected 'in'");
                var iterable = ParseLogicalOr();

                string var2Name = "";
                Expression? iterable2 = null;
                if (Match(TokenType.For))
                {
                    var var2Tok = Consume(TokenType.Identifier, "Expected loop variable");
                    Consume(TokenType.In, "Expected 'in'");
                    iterable2 = ParseLogicalOr();
                    var2Name = var2Tok.Value;
                }

                Expression? filter = null;
                if (Match(TokenType.If))
                {
                    filter = ParseLogicalOr();
                }

                Consume(TokenType.RBracket, "Expected ']'");
                return new ListCompExpr(first, varTok.Value, iterable, var2Name, iterable2, filter);
            }

            var lelems = new List<Expression> { first };
            while (Match(TokenType.Comma))
            {
                lelems.Add(ParseExpression());
            }

            Consume(TokenType.RBracket, "Expected ']'");
            return new ListExpr(lelems);
        }

        Error("Expected expression");
        return null!;
    }
}
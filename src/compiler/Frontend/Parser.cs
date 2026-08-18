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

    // Extra modules from a comma-separated `import a, b, c`: ParseImportStatement returns
    // the first and queues the rest here for the caller to drain.
    private readonly Queue<ImportStmt> pendingImports = new();

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
                while (pendingImports.Count > 0)
                    prog.Imports.Add(pendingImports.Dequeue());
            }
            else if (Check(TokenType.At) && DecoratorsLeadToClass())
            {
                prog.GlobalStatements.Add(ParseClassDefinitionWithDecorators());
            }
            else if (Check(TokenType.Identifier) && Peek().Value == "async" && PeekNext().Type == TokenType.Def)
            {
                Advance(); // consume `async`
                var asyncFn = ParseFunction();
                asyncFn.IsAsync = true;
                prog.Functions.Add(asyncFn);
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

    private Token PeekAt(int offset)
    {
        int idx = pos + offset;
        return idx >= tokens.Count ? tokens[^1] : tokens[idx];
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
            throw new SyntaxError("Unexpected EOF while parsing", t.Line, t.Column, 1);
        throw new SyntaxError(message, t.Line, t.Column, t.Length);
    }

    private void IndentError(string message)
    {
        var t = Peek();
        throw new IndentationError(message, t.Line, t.Column, t.Length);
    }

    private string ParseTypeAnnotation()
    {
        if (Check(TokenType.String))
            Error("string ('forward reference') type annotations are not supported; " +
                  "use the bare class name (e.g. `-> Vec`, not `-> " + (char)34 + "Vec" + (char)34 + "`)");
        var t = Consume(TokenType.Identifier, "Expected type identifier");
        string typeStr = t.Value;

        if (Match(TokenType.LBracket))
        {
            typeStr += "[";
            if (Check(TokenType.Identifier))
            {
                var inner = Consume(TokenType.Identifier, "Expected inner type");
                typeStr += inner.Value;

                // Array size given as a named constant times a literal, e.g.
                // uint8[n*3] for a per-instance framebuffer. The product is folded
                // against the (compile-time constant) identifier in the IR generator.
                if (Match(TokenType.Star))
                {
                    var factor = Consume(TokenType.Number, "Expected a numeric factor after '*' in array size");
                    typeStr += "*" + factor.Value;
                }

                // Handle nested bracket: e.g. const[uint8[4]] or const[str]
                if (Match(TokenType.LBracket))
                {
                    typeStr += "[";
                    if (Check(TokenType.Identifier))
                    {
                        var innerType = Consume(TokenType.Identifier, "Expected inner type name");
                        typeStr += innerType.Value;
                    }
                    else if (Check(TokenType.Number))
                    {
                        var innerSize = Consume(TokenType.Number, "Expected array size");
                        typeStr += innerSize.Value;
                    }
                    else
                    {
                        Error("Expected type name or array size inside '['");
                    }
                    Consume(TokenType.RBracket, "Expected ']'");
                    typeStr += "]";
                }
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

            // Comma-separated subscript args, e.g. a tuple type tuple[uint8, uint16] used as a
            // function return annotation. Accepted (and recorded textually); for an @inline
            // function returning multiple values the annotation is documentation — the caller's
            // unpack targets receive the values.
            while (Match(TokenType.Comma))
            {
                typeStr += ",";
                if (Check(TokenType.Identifier)) typeStr += Consume(TokenType.Identifier, "Expected type name").Value;
                else if (Check(TokenType.Number)) typeStr += Consume(TokenType.Number, "Expected array size").Value;
                else Error("Expected type name or size after ',' in '['");
            }

            Consume(TokenType.RBracket, "Expected ']'");
            typeStr += "]";
        }

        return typeStr;
    }

    // Return position also accepts the parenthesized multi-value form
    //   def divmod8(a: uint8, b: uint8) -> (uint8, uint8):
    // which is normalized to the textual "tuple[uint8,uint8]" so it and the explicit
    // tuple[...] spelling share one validation path in the IR generator.
    // As in Python, `(T)` is just a parenthesized T; only a comma makes it a tuple.
    private string ParseReturnTypeAnnotation()
    {
        if (!Check(TokenType.LParen)) return ParseTypeAnnotation();

        Advance(); // (
        if (Check(TokenType.RParen))
            Error("Empty return annotation '()'; use '-> None' for a function that returns nothing");

        var elements = new List<string>();
        bool sawComma = false;
        while (true)
        {
            if (Check(TokenType.RParen)) break;   // trailing comma, e.g. -> (uint8,)
            elements.Add(ParseTypeAnnotation());
            if (!Match(TokenType.Comma)) break;
            sawComma = true;
        }

        Consume(TokenType.RParen, "Expected ')' to close the return type annotation");

        if (elements.Count == 1 && !sawComma) return elements[0];
        return "tuple[" + string.Join(",", elements) + "]";
    }

    private FunctionDef ParseFunction()
    {
        bool isInline = false;
        bool isOutline = false;
        bool isInterrupt = false;
        int vector = 0;
        bool isPropertyGetter = false;
        bool isPropertySetter = false;
        string propSetterOf = "";
        bool isNaked = false;
        bool isExportC = false;
        bool isExtern = false;
        string externSymbol = "";
        string warningMessage = "";
        bool isPioProgram = false;
        var pioParams = new Dictionary<string, Expression>();

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
            else if (decorator.Value == "asm_pio")
            {
                // @asm_pio(...) -- MicroPython-style PIO program. The body is PIO
                // assembly handled by the PIO assembler, not the CPU codegen.
                isPioProgram = true;
                ParsePioDecoratorArgs(pioParams);
            }
            else if (decorator.Value == "rp2" && Check(TokenType.Dot))
            {
                // @rp2.asm_pio(...) -- the dotted MicroPython form. Must be checked
                // before the generic dotted (property setter/getter) branch below.
                Advance();
                var sub = Consume(TokenType.Identifier, "Expected 'asm_pio' after '@rp2.'");
                if (sub.Value != "asm_pio")
                    Error("Unknown decorator '@rp2." + sub.Value + "'");
                isPioProgram = true;
                ParsePioDecoratorArgs(pioParams);
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
            else if (decorator.Value == "classmethod")
            {
                Error("@classmethod is not supported (no runtime class object on bare metal); " +
                      "use a module-level factory function, or a @staticmethod that calls the constructor");
            }
            else if (decorator.Value == "naked")
            {
                isNaked = true;
            }
            else if (decorator.Value == "used" || decorator.Value == "export_c")
            {
                // Keep the function alive with external linkage even with no Python
                // caller, so inline asm (e.g. an RTOS context switch) can `bl` it.
                isExportC = true;
            }
            else if (decorator.Value == "outline")
            {
                // RFC 0001 Model A: compile this ZCA method once as a shared
                // subroutine taking the instance fields as params (no force-inline).
                isOutline = true;
            }
            else if (decorator.Value == "warning")
            {
                // @warning("..."): record the message. The IR generator prints it
                // (once per function) as an informational note when a call to the
                // decorated function is expanded; it does NOT abort compilation.
                Consume(TokenType.LParen, "Expected '(' after @warning");
                var msgTok = Consume(TokenType.String,
                    "Expected a string message in @warning(" + (char)34 + "..." + (char)34 + ")");
                warningMessage = msgTok.Value;
                Consume(TokenType.RParen, "Expected ')' after @warning message");
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
            returnType = ParseReturnTypeAnnotation();
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
            IsNaked = isNaked,
            IsExportC = isExportC,
            IsExtern = isExtern,
            ExternSymbol = externSymbol,
            WarningMessage = warningMessage,
            IsOutline = isOutline,
            IsPioProgram = isPioProgram,
            PioParams = pioParams,
            Line = nameToken.Line
        };
        return func;
    }

    // Parses the optional keyword-argument list of an @asm_pio / @rp2.asm_pio
    // decorator into raw expressions (evaluated later by the PIO assembler):
    //   @asm_pio(out_init=(PIO.OUT_LOW,), sideset_init=PIO.OUT_LOW, autopull=True)
    // Only keyword arguments are accepted (the decorator takes no positional args).
    private void ParsePioDecoratorArgs(Dictionary<string, Expression> into)
    {
        if (!Check(TokenType.LParen)) return;   // bare @asm_pio with no parentheses
        Advance(); // (
        while (Check(TokenType.Newline)) Advance();
        if (!Check(TokenType.RParen))
        {
            do
            {
                while (Check(TokenType.Newline)) Advance();
                if (Check(TokenType.RParen)) break;
                var key = Consume(TokenType.Identifier, "Expected keyword argument name in @asm_pio(...)").Value;
                Consume(TokenType.Equal, "Expected '=' after '" + key + "' in @asm_pio(...)");
                into[key] = ParseExpression();
            } while (Match(TokenType.Comma));
            while (Check(TokenType.Newline)) Advance();
        }
        Consume(TokenType.RParen, "Expected ')' to close @asm_pio(...)");
    }

    private ClassDef ParseClassDefinitionWithDecorators()
    {
        bool isValue = false;
        while (Check(TokenType.At))
        {
            Advance(); // consume @
            var dec = Consume(TokenType.Identifier, "Expected decorator name after '@'");
            if (dec.Value == "value")
                isValue = true;
            else
                Error("Unknown class decorator: @" + dec.Value);
            Consume(TokenType.Newline, "Expected newline after class decorator");
        }
        var classDef = ParseClassDefinition();
        classDef.IsValue = isValue;
        return classDef;
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
            // Bare '*' is the PEP 3102 keyword-only separator (common in
            // CircuitPython APIs, e.g. busio.UART(tx, rx, *, baudrate=9600)).
            // PyMCU resolves arguments by name/position regardless, so we accept
            // the marker and treat following parameters like any other. A
            // '*args' varargs form is not supported.
            if (Check(TokenType.Star))
            {
                Advance();
                if (!Check(TokenType.Comma) && !Check(TokenType.RParen))
                {
                    Error("Variadic '*args' parameters are not supported; '*' is only valid as a keyword-only separator");
                }
                if (Check(TokenType.RParen)) break;
                continue;
            }

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

    // Returns true if the current @ sequence (one or more @identifier\n pairs) leads to 'class'.
    private bool DecoratorsLeadToClass()
    {
        int offset = 0;
        while (PeekAt(offset).Type == TokenType.At)
        {
            offset++; // skip @
            if (PeekAt(offset).Type != TokenType.Identifier) return false;
            offset++; // skip decorator name
            if (PeekAt(offset).Type != TokenType.Newline) return false;
            offset++; // skip newline
        }
        return PeekAt(offset).Type == TokenType.Class;
    }

    private Statement ParseStatement()
    {
        // `async def ...` -- a coroutine. `async` is a soft keyword (an identifier),
        // so detect it before the `def` dispatch and flag the parsed function.
        if (Check(TokenType.Identifier) && Peek().Value == "async" && PeekNext().Type == TokenType.Def)
        {
            Advance(); // consume `async`
            if (functionDepth > 0)
                Error("Nested function definitions require the @inline decorator");
            var asyncFn = ParseFunction();
            asyncFn.IsAsync = true;
            return asyncFn;
        }

        if (Check(TokenType.If)) return ParseIfStatement();
        if (Check(TokenType.Match)) return ParseMatchStatement();
        if (Check(TokenType.While)) return ParseWhileStatement();
        if (Check(TokenType.For)) return ParseForStatement();
        if (Check(TokenType.At) && DecoratorsLeadToClass())
            return ParseClassDefinitionWithDecorators();
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
        if (Check(TokenType.Try)) return ParseTryStatement();
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
            // A bare comma-separated return is a tuple: `return a, b` is `return (a, b)`.
            // Without this the parser stopped at the first element and choked on the comma,
            // so multi-value return required explicit parentheses.
            if (Check(TokenType.Comma))
            {
                var elems = new List<Expression> { value };
                while (Match(TokenType.Comma))
                {
                    if (Check(TokenType.Newline) || Check(TokenType.Semicolon) || Check(TokenType.EndOfFile))
                        break; // trailing comma
                    elems.Add(ParseExpression());
                }
                value = new TupleExpr(elems) { Line = line };
            }
        }

        ConsumeStatementEnd();
        return new ReturnStmt(value) { Line = line };
    }

    private Statement ParseRaiseStatement()
    {
        int line = Peek().Line;
        Consume(TokenType.Raise, "Expected 'raise'");
        // A bare `raise` (no type) re-raises the current exception inside an except handler.
        // ErrorType "" marks that re-raise; VisitRaise re-signals the pending code (in R22).
        string errorType = "";
        string message = "";
        string? messageName = null;
        if (Check(TokenType.Identifier))
        {
            errorType = Advance().Value;
            if (Check(TokenType.LParen))
            {
                Advance();
                while (Check(TokenType.Newline)) Advance();
                if (Check(TokenType.String))
                {
                    var parts = new System.Text.StringBuilder();
                    while (Check(TokenType.String) || Check(TokenType.Newline))
                    {
                        if (Check(TokenType.Newline)) Advance();
                        else parts.Append(Advance().Value);
                    }

                    message = parts.ToString();
                }
                else if (Check(TokenType.Identifier))
                {
                    messageName = Advance().Value;
                    while (Check(TokenType.Newline)) Advance();
                }

                Consume(TokenType.RParen,
                    "Expected ')' after the error message. The message must be one or more " +
                    "adjacent string literals, or the name of a module-level string constant");
            }
        }

        ConsumeStatementEnd();
        return new RaiseStmt(errorType, message, messageName) { Line = line };
    }

    private Statement ParseTryStatement()
    {
        int line = Peek().Line;
        Consume(TokenType.Try, "Expected 'try'");
        Consume(TokenType.Colon, "Expected ':' after 'try'");
        ConsumeStatementEnd();
        var tryBlock = ParseBlock();
        var body = tryBlock.Statements;

        var handlers = new List<(string, List<Statement>)>();
        while (Check(TokenType.Except))
        {
            Consume(TokenType.Except, "Expected 'except'");
            string exnType = Check(TokenType.Colon)
                ? ""
                : Consume(TokenType.Identifier, "Expected exception type after 'except'").Value;
            Consume(TokenType.Colon, "Expected ':' after exception type");
            ConsumeStatementEnd();
            var handlerBlock = ParseBlock();
            handlers.Add((exnType, handlerBlock.Statements));
        }

        // Optional `else`: runs when the try body raised no exception (Python order: except* else? finally?).
        List<Statement>? elseBody = null;
        if (Check(TokenType.Else))
        {
            Consume(TokenType.Else, "Expected 'else'");
            Consume(TokenType.Colon, "Expected ':' after 'else'");
            ConsumeStatementEnd();
            elseBody = ParseBlock().Statements;
        }

        List<Statement>? finallyBody = null;
        if (Check(TokenType.Finally))
        {
            Consume(TokenType.Finally, "Expected 'finally'");
            Consume(TokenType.Colon, "Expected ':' after 'finally'");
            ConsumeStatementEnd();
            finallyBody = ParseBlock().Statements;
        }

        return new TryStmt(body, handlers, finallyBody, elseBody) { Line = line };
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

    // Parses a single dotted module name with an optional `as` alias (the `import`
    // keyword is already consumed). Shared by single and comma-separated imports.
    private ImportStmt ParseModuleImportSpec()
    {
        string modName = Consume(TokenType.Identifier, "Expected module name").Value;
        while (Match(TokenType.Dot))
            modName += "." + Consume(TokenType.Identifier, "Expected part name").Value;

        var stmt = new ImportStmt(modName, new List<string>(), 0);
        if (Match(TokenType.As))
            stmt.ModuleAlias = Consume(TokenType.Identifier, "Expected alias name after 'as'").Value;
        return stmt;
    }

    private ImportStmt ParseImportStatement()
    {
        if (Match(TokenType.Import))
        {
            var first = ParseModuleImportSpec();
            // `import a, b, c`: queue the additional modules for the caller to drain.
            while (Match(TokenType.Comma))
                pendingImports.Enqueue(ParseModuleImportSpec());
            return first;
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

    // The loop `else` clause (runs when the loop finishes without `break`) is not
    // modelled. Reject it with a clear message instead of a confusing "Expected
    // expression" syntax error from trying to parse `else` as a statement.
    private void RejectLoopElse(string kind)
    {
        if (Check(TokenType.Else))
            throw new SyntaxError(
                $"'{kind} ... else' is not supported; move the else body to after the loop",
                Peek().Line, 1);
    }

    private Statement ParseWhileStatement()
    {
        int line = Peek().Line;
        Consume(TokenType.While, "Expected 'while'");
        var condition = ParseExpression();
        Consume(TokenType.Colon, "Expected ':'");
        Consume(TokenType.Newline, "Expected newline");
        var body = ParseBlock();
        RejectLoopElse("while");
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
            RejectLoopElse("for");
            return stmt;
        }

        var iterable = ParseExpression();
        Consume(TokenType.Colon, "Expected ':'");
        Consume(TokenType.Newline, "Expected newline");
        var ibody = ParseBlock();

        RejectLoopElse("for");
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
                // A bare comma-separated RHS is a tuple literal: `a, b = b, a`.
                // Without this it parsed only the first element and choked on the
                // comma, so tuple swap / multi-assign never worked.
                if (Check(TokenType.Comma))
                {
                    var elems = new List<Expression> { valueExpr };
                    while (Match(TokenType.Comma))
                    {
                        if (Check(TokenType.Newline) || Check(TokenType.EndOfFile)) break; // trailing comma
                        elems.Add(ParseExpression());
                    }
                    valueExpr = new TupleExpr(elems) { Line = line };
                }
                ConsumeStatementEnd();
                return new TupleUnpackStmt(targets, valueExpr, starredIndex) { Line = line };
            }
        }

        if (Match(TokenType.Colon))
        {
            // Allow a simple variable (x: T) or an instance-member array
            // declaration (self._buf: uint8[N]) used to reserve a per-instance
            // SRAM framebuffer. The member form is encoded as a dotted target
            // ("self._buf") and only supported for array annotations.
            string name;
            if (expr is VariableExpr varExpr)
            {
                name = varExpr.Name;
            }
            else if (expr is MemberAccessExpr memAnn && memAnn.Object is VariableExpr memObj)
            {
                name = memObj.Name + "." + memAnn.Member;
            }
            else
            {
                Error("Only simple variables or instance members can be annotated with types");
                name = "";
            }

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

            // `self.field: T = value` -- a scalar type annotation on an instance
            // member. The member's type is inferred from the assigned value (same as
            // a bare `self.field = value`), so accept the annotation and lower to a
            // plain member assignment. (Users naturally write the annotation; don't
            // make them delete it.)
            if (name.Contains('.'))
            {
                if (init == null)
                    Error("An annotated instance member needs an initial value, e.g. `self.x: int = 0`");
                return new AssignStmt(expr, init!) { Line = line, AnnotatedType = type };
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
        var first = ParseBitwiseOr();

        // Collect a comparison chain so `a < b < c` becomes (a<b) and (b<c) — the
        // Python semantics — instead of the left-associative (a<b)<c. operands[i]
        // and ops[i] line up so the chain is operands[0] ops[0] operands[1] ...
        var ops = new List<BinaryOp>();
        var operands = new List<Expression> { first };

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
            ops.Add(op);
            operands.Add(right);
        }

        if (ops.Count == 0) return first;
        if (ops.Count == 1) return new BinaryExpr(operands[0], ops[0], operands[1]);

        // Chained: build ((o0 op0 o1) and (o1 op1 o2)) and ...  The shared middle
        // operands are reused as AST nodes; with side-effect-free operands (the
        // usual case) this matches Python's single-evaluation semantics closely.
        Expression chain = new BinaryExpr(operands[0], ops[0], operands[1]);
        for (int i = 1; i < ops.Count; i++)
            chain = new BinaryExpr(chain, BinaryOp.And,
                new BinaryExpr(operands[i], ops[i], operands[i + 1]));
        return chain;
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
        // `await <expr>` -- suspension point in a coroutine (`await` is a soft keyword).
        if (Check(TokenType.Identifier) && Peek().Value == "await" && PeekNext().Type != TokenType.Equal
            && PeekNext().Type != TokenType.Dot && PeekNext().Type != TokenType.LParen)
        {
            Advance(); // consume `await`
            return new AwaitExpr(ParseUnary());
        }

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
        if (Match(TokenType.None)) return new NoneLiteral();

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
            // Python concatenates adjacent string literals: "a" "b" is one string, and
            // inside parentheses the pieces may sit on separate lines. Without this a
            // long message had to live on one 200-character line.
            string joined = Previous().Value;
            while (Check(TokenType.String)) joined += Advance().Value;
            return new StringLiteral(joined);
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

                    // Split off a format spec at the first ':' that is at bracket-nesting depth 0,
                    // so slices/subscripts inside the expression (e.g. {a[1:2]}) are not mistaken
                    // for a spec. `{value:02x}` -> expr "value", spec "02x".
                    string fmtSpec = "";
                    int depth = 0;
                    for (int k = 0; k < exprSrc.Length; k++)
                    {
                        char ch = exprSrc[k];
                        if (ch == '(' || ch == '[' || ch == '{') depth++;
                        else if (ch == ')' || ch == ']' || ch == '}') depth--;
                        else if (ch == ':' && depth == 0)
                        {
                            fmtSpec = exprSrc.Substring(k + 1);
                            exprSrc = exprSrc.Substring(0, k);
                            break;
                        }
                    }

                    var subLex = new Lexer(exprSrc.AsSpan());
                    var subTokens = subLex.Tokenize();
                    var subParser = new Parser(subTokens);
                    var innerExpr = subParser.ParseExpressionPublic();

                    parts.Add(new FStringPart { IsExpr = true, Expr = innerExpr, FormatSpec = fmtSpec });
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

                long val64 = Convert.ToInt64(text.Substring(offset), b);
                if (val64 > uint.MaxValue)
                    Error("Integer literal is too large: '" + t.Value + "'");
                // 2^31..2^32-1 is a valid uint32 literal (e.g. 4000000000). The AST/IR
                // carry int, so store the 32-bit BIT PATTERN: backends emit constants
                // byte-wise, and the magnitude-based width sees 4 bytes either way.
                // (Compile-time folding of arithmetic ON such literals would see the
                // signed reading; runtime uint32 arithmetic is unaffected.)
                return new IntegerLiteral(unchecked((int)(uint)val64));
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

        if (Match(TokenType.LBrace))
        {
            // `{}` is an empty dict (Python); `{k: v, ...}` a dict; `{a, b, ...}` a set.
            if (Check(TokenType.RBrace))
            {
                Advance();
                return new DictExpr(new List<(Expression, Expression)>());
            }
            var firstItem = ParseExpression();
            if (Match(TokenType.Colon))
            {
                var entries = new List<(Expression, Expression)> { (firstItem, ParseExpression()) };
                if (Check(TokenType.For))
                    Error("dict comprehensions are not supported (a general hash map needs a heap); " +
                          "precompute a closed dict literal, or fill a pymcu.collections.FixedDict in a loop");
                while (Match(TokenType.Comma))
                {
                    if (Check(TokenType.RBrace)) break;   // trailing comma
                    var k = ParseExpression();
                    Consume(TokenType.Colon, "Expected ':' in dict literal");
                    entries.Add((k, ParseExpression()));
                }
                Consume(TokenType.RBrace, "Expected '}' after dict literal");
                return new DictExpr(entries);
            }
            if (Check(TokenType.For))
                Error("set comprehensions are not supported (a general hash set needs a heap); " +
                      "precompute a closed set literal, or use a fixed array");
            var selems = new List<Expression> { firstItem };
            while (Match(TokenType.Comma))
            {
                if (Check(TokenType.RBrace)) break;       // trailing comma
                selems.Add(ParseExpression());
            }
            Consume(TokenType.RBrace, "Expected '}' after set literal");
            return new SetExpr(selems);
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
/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
 * -----------------------------------------------------------------------------
 */

using System.Diagnostics;
using System.Text.Json;
using PyMCU.Common;

namespace PyMCU.Frontend;

/// <summary>
/// Builds the PyMCU AST from CPython's own parser, via the translator in
/// Frontend/PyParser/pymcu_translate.py, when PYMCU_PY_PARSER=1.
///
/// The AST is the contract: this produces the same nodes the C# parser produces, so
/// nothing downstream can tell which front end ran. That is what makes the two
/// comparable -- build the corpus both ways and the firmware must be byte-identical.
///
/// Why it is worth having: CPython's grammar is the definition of the language PyMCU
/// accepts a subset of, so a construct the hand-written parser has not learned yet
/// (and the diagnostics for one it will not accept) comes for free.
/// </summary>
public static class PythonAstReader
{
    /// <summary>True when the environment asks for the Python front end.</summary>
    public static bool Enabled =>
        Environment.GetEnvironmentVariable("PYMCU_PY_PARSER") == "1";

    private static string? _cachedScript;

    /// <summary>Parses a file into a ProgramNode. Throws SyntaxError like the C# parser does.</summary>
    public static ProgramNode ParseFile(string path)
    {
        string json = RunTranslator(path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        {
            int line = root.TryGetProperty("line", out var l) ? l.GetInt32() : 0;
            throw new SyntaxError(err.GetString() ?? "parse failed", line, 1);
        }

        var program = new ProgramNode();
        foreach (var imp in root.GetProperty("imports").EnumerateArray())
            program.Imports.Add(ReadImport(imp));
        foreach (var fn in root.GetProperty("functions").EnumerateArray())
            program.Functions.Add(ReadFunction(fn));
        foreach (var st in root.GetProperty("globals").EnumerateArray())
            program.GlobalStatements.Add(ReadStatement(st));

        return program;
    }

    /// <summary>Parses source text by writing it to a temporary file (the entry file is
    /// read into memory before this phase runs).</summary>
    public static ProgramNode ParseSource(string source, string originalPath)
    {
        // Keep the original name so diagnostics and __name__ handling see the same file.
        string dir = Path.Combine(Path.GetTempPath(), "pymcu-pyparser", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string temp = Path.Combine(dir, Path.GetFileName(originalPath));
        try
        {
            File.WriteAllText(temp, source);
            return ParseFile(temp);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ── running the translator ───────────────────────────────────────────────

    private static string RunTranslator(string path)
    {
        string script = LocateScript();
        string python = Environment.GetEnvironmentVariable("PYMCU_PYTHON") ?? "python3";

        var psi = new ProcessStartInfo
        {
            FileName = python,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(path);

        using var proc = Process.Start(psi)
            ?? throw new CompilerError("PyParser", $"could not start '{python}'", 0, 0);
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        // Exit code 1 is a translation error, reported as JSON on stdout; anything else
        // means the translator itself failed, and its stderr is the useful part.
        if (proc.ExitCode > 1 || (stdout.Length == 0 && stderr.Length > 0))
            throw new CompilerError("PyParser",
                $"the Python front end failed on {Path.GetFileName(path)}: {stderr.Trim()}", 0, 0);

        return stdout;
    }

    private static string LocateScript()
    {
        if (_cachedScript != null) return _cachedScript;

        var candidates = new List<string>();
        string? fromEnv = Environment.GetEnvironmentVariable("PYMCU_PY_PARSER_SCRIPT");
        if (!string.IsNullOrEmpty(fromEnv)) candidates.Add(fromEnv);

        string baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, "pymcu_translate.py"));
        candidates.Add(Path.Combine(baseDir, "PyParser", "pymcu_translate.py"));

        // Development layout: the binary is deployed next to the driver or under build/bin.
        var dir = new DirectoryInfo(baseDir);
        for (int up = 0; up < 6 && dir != null; up++, dir = dir.Parent)
            candidates.Add(Path.Combine(dir.FullName,
                "src", "compiler", "Frontend", "PyParser", "pymcu_translate.py"));

        foreach (var candidate in candidates)
            if (File.Exists(candidate))
                return _cachedScript = candidate;

        throw new CompilerError("PyParser",
            "PYMCU_PY_PARSER=1 but the translator was not found. Set PYMCU_PY_PARSER_SCRIPT "
            + "to the path of Frontend/PyParser/pymcu_translate.py.", 0, 0);
    }

    // ── readers ──────────────────────────────────────────────────────────────

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static bool Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static bool Has(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

    private static T Located<T>(T node, JsonElement e) where T : ASTNode
    {
        node.Line = Int(e, "line");
        return node;
    }

    private static ImportStmt ReadImport(JsonElement e)
    {
        var symbols = new List<string>();
        foreach (var s in e.GetProperty("symbols").EnumerateArray())
            symbols.Add(s.GetString()!);

        var stmt = new ImportStmt(Str(e, "module"), symbols, Int(e, "level"))
        {
            ModuleAlias = Str(e, "moduleAlias"),
        };
        if (e.TryGetProperty("aliases", out var aliases) && aliases.ValueKind == JsonValueKind.Object)
            foreach (var kv in aliases.EnumerateObject())
                stmt.Aliases[kv.Name] = kv.Value.GetString()!;

        return Located(stmt, e);
    }

    private static Param ReadParam(JsonElement e) =>
        new(Str(e, "name"), Str(e, "type"), Has(e, "default") ? ReadExpr(e.GetProperty("default")) : null);

    private static FunctionDef ReadFunction(JsonElement e)
    {
        var parameters = new List<Param>();
        foreach (var p in e.GetProperty("params").EnumerateArray())
            parameters.Add(ReadParam(p));

        var body = (Block)ReadStatement(e.GetProperty("body"));
        var fn = new FunctionDef(Str(e, "name"), parameters, Str(e, "returnType"), body,
            Flag(e, "isInline"), Flag(e, "isInterrupt"), Int(e, "vector"))
        {
            IsPropertyGetter = Flag(e, "isPropertyGetter"),
            IsPropertySetter = Flag(e, "isPropertySetter"),
            PropertyName = Str(e, "propertyName"),
            IsNaked = Flag(e, "isNaked"),
            IsExtern = Flag(e, "isExtern"),
            ExternSymbol = Str(e, "externSymbol"),
            IsExportC = Flag(e, "isExportC"),
            IsOutline = Flag(e, "isOutline"),
            WarningMessage = Str(e, "warning"),
            IsPioProgram = Flag(e, "isPio"),
            IsAsync = Flag(e, "isAsync"),
        };

        if (e.TryGetProperty("pioParams", out var pio) && pio.ValueKind == JsonValueKind.Object)
            foreach (var kv in pio.EnumerateObject())
                fn.PioParams[kv.Name] = ReadExpr(kv.Value)!;

        return Located(fn, e);
    }

    private static List<Statement> ReadStatementList(JsonElement e)
    {
        var list = new List<Statement>();
        foreach (var s in e.EnumerateArray()) list.Add(ReadStatement(s));
        return list;
    }

    private static Statement ReadStatement(JsonElement e)
    {
        string kind = Str(e, "k");
        switch (kind)
        {
            case "Block":
            {
                var block = new Block();
                foreach (var s in e.GetProperty("statements").EnumerateArray())
                    block.Statements.Add(ReadStatement(s));
                return Located(block, e);
            }
            case "Function": return ReadFunction(e);
            case "Class":
            {
                var bases = new List<string>();
                foreach (var b in e.GetProperty("bases").EnumerateArray()) bases.Add(b.GetString()!);
                var cls = new ClassDef(Str(e, "name"), bases, ReadStatement(e.GetProperty("body")))
                {
                    IsStatic = Flag(e, "isStatic"),
                    IsDataclass = Flag(e, "isDataclass"),
                    IsValue = Flag(e, "isValue"),
                };
                return Located(cls, e);
            }
            case "VarDecl":
                return Located(new VarDecl(Str(e, "name"), Str(e, "varType"),
                    Has(e, "init") ? ReadExpr(e.GetProperty("init")) : null), e);
            case "AnnAssign":
                return Located(new AnnAssign(Str(e, "target"), Str(e, "annotation"),
                    Has(e, "value") ? ReadExpr(e.GetProperty("value")) : null), e);
            case "Assign":
            {
                var assign = new AssignStmt(ReadExpr(e.GetProperty("target"))!,
                    ReadExpr(e.GetProperty("value"))!);
                if (Has(e, "annotatedType")) assign.AnnotatedType = Str(e, "annotatedType");
                return Located(assign, e);
            }
            case "AugAssign":
                return Located(new AugAssignStmt(ReadExpr(e.GetProperty("target"))!,
                    Enum.Parse<AugOp>(Str(e, "op")), ReadExpr(e.GetProperty("value"))!), e);
            case "TupleUnpack":
            {
                var targets = new List<string>();
                foreach (var t in e.GetProperty("targets").EnumerateArray()) targets.Add(t.GetString()!);
                return Located(new TupleUnpackStmt(targets, ReadExpr(e.GetProperty("value"))!,
                    Int(e, "starredIndex")), e);
            }
            case "Return":
                return Located(new ReturnStmt(Has(e, "value") ? ReadExpr(e.GetProperty("value")) : null), e);
            case "If":
            {
                var elifs = new List<(Expression, Statement)>();
                foreach (var br in e.GetProperty("elifs").EnumerateArray())
                    elifs.Add((ReadExpr(br.GetProperty("condition"))!, ReadStatement(br.GetProperty("body"))));
                return Located(new IfStmt(ReadExpr(e.GetProperty("condition"))!,
                    ReadStatement(e.GetProperty("then")), elifs,
                    Has(e, "else") ? ReadStatement(e.GetProperty("else")) : null), e);
            }
            case "While":
                return Located(new WhileStmt(ReadExpr(e.GetProperty("condition"))!,
                    ReadStatement(e.GetProperty("body"))), e);
            case "For":
            {
                var body = ReadStatement(e.GetProperty("body"));
                ForStmt loop = Has(e, "iterable")
                    ? new ForStmt(Str(e, "varName"), ReadExpr(e.GetProperty("iterable"))!, body)
                    : new ForStmt(Str(e, "varName"),
                        Has(e, "rangeStart") ? ReadExpr(e.GetProperty("rangeStart")) : null,
                        Has(e, "rangeStop") ? ReadExpr(e.GetProperty("rangeStop")) : null,
                        Has(e, "rangeStep") ? ReadExpr(e.GetProperty("rangeStep")) : null,
                        body);
                loop.Var2Name = Str(e, "var2Name");
                return Located(loop, e);
            }
            case "With":
                return Located(new WithStmt(ReadExpr(e.GetProperty("context"))!, Str(e, "asName"),
                    ReadStatement(e.GetProperty("body"))), e);
            case "Match":
            {
                var branches = new List<CaseBranch>();
                foreach (var br in e.GetProperty("branches").EnumerateArray())
                    branches.Add(new CaseBranch
                    {
                        Pattern = Has(br, "pattern") ? ReadExpr(br.GetProperty("pattern")) : null,
                        Guard = Has(br, "guard") ? ReadExpr(br.GetProperty("guard")) : null,
                        CaptureName = Str(br, "capture"),
                        Body = ReadStatement(br.GetProperty("body")),
                    });
                return Located(new MatchStmt(ReadExpr(e.GetProperty("target"))!, branches), e);
            }
            case "Try":
            {
                var handlers = new List<(string, List<Statement>)>();
                foreach (var h in e.GetProperty("handlers").EnumerateArray())
                    handlers.Add((Str(h, "exnType"), ReadStatementList(h.GetProperty("body"))));
                return Located(new TryStmt(
                    ReadStatementList(e.GetProperty("body")), handlers,
                    Has(e, "finally") ? ReadStatementList(e.GetProperty("finally")) : null,
                    Has(e, "else") ? ReadStatementList(e.GetProperty("else")) : null), e);
            }
            case "Raise":
                return Located(new RaiseStmt(Str(e, "errorType"), Str(e, "message"),
                    Has(e, "messageName") ? Str(e, "messageName") : null), e);
            case "Assert":
                return Located(new AssertStmt(ReadExpr(e.GetProperty("condition"))!, Str(e, "message")), e);
            case "ExprStmt":
                return Located(new ExprStmt(ReadExpr(e.GetProperty("expr"))!), e);
            case "Global":
            {
                var names = new List<string>();
                foreach (var n in e.GetProperty("names").EnumerateArray()) names.Add(n.GetString()!);
                return Located(new GlobalStmt(names), e);
            }
            case "Nonlocal":
            {
                var names = new List<string>();
                foreach (var n in e.GetProperty("names").EnumerateArray()) names.Add(n.GetString()!);
                return Located(new NonlocalStmt(names), e);
            }
            case "Import": return ReadImport(e);
            case "Break": return Located(new BreakStmt(), e);
            case "Continue": return Located(new ContinueStmt(), e);
            case "Pass": return Located(new PassStmt(), e);
            default:
                throw new CompilerError("PyParser", $"unknown statement kind '{kind}' from the translator",
                    Int(e, "line"), 0);
        }
    }

    private static Expression? ReadExpr(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Null) return null;

        string kind = Str(e, "k");
        switch (kind)
        {
            case "Int": return Located(new IntegerLiteral(e.GetProperty("value").GetInt32()), e);
            case "Float": return Located(new FloatLiteral(e.GetProperty("value").GetDouble()), e);
            case "Bool": return Located(new BooleanLiteral(e.GetProperty("value").GetBoolean()), e);
            case "Str": return Located(new StringLiteral(Str(e, "value")), e);
            case "None": return Located(new NoneLiteral(), e);
            case "Var": return Located(new VariableExpr(Str(e, "name")), e);
            case "Binary":
                return Located(new BinaryExpr(ReadExpr(e.GetProperty("left"))!,
                    Enum.Parse<BinaryOp>(Str(e, "op")), ReadExpr(e.GetProperty("right"))!), e);
            case "Unary":
                return Located(new UnaryExpr(Enum.Parse<UnaryOp>(Str(e, "op")),
                    ReadExpr(e.GetProperty("operand"))!), e);
            case "Call":
            {
                var args = new List<Expression>();
                foreach (var a in e.GetProperty("args").EnumerateArray()) args.Add(ReadExpr(a)!);
                return Located(new CallExpr(ReadExpr(e.GetProperty("callee"))!, args), e);
            }
            case "Keyword":
                return Located(new KeywordArgExpr(Str(e, "key"), ReadExpr(e.GetProperty("value"))!), e);
            case "Member":
                return Located(new MemberAccessExpr(ReadExpr(e.GetProperty("object"))!, Str(e, "member")), e);
            case "Index":
                return Located(new IndexExpr(ReadExpr(e.GetProperty("target"))!,
                    ReadExpr(e.GetProperty("index"))!), e);
            case "Slice":
                return Located(new SliceExpr(
                    Has(e, "start") ? ReadExpr(e.GetProperty("start")) : null,
                    Has(e, "stop") ? ReadExpr(e.GetProperty("stop")) : null,
                    Has(e, "step") ? ReadExpr(e.GetProperty("step")) : null), e);
            case "List":
            {
                var elements = new List<Expression>();
                foreach (var x in e.GetProperty("elements").EnumerateArray()) elements.Add(ReadExpr(x)!);
                return Located(new ListExpr(elements), e);
            }
            case "Tuple":
            {
                var elements = new List<Expression>();
                foreach (var x in e.GetProperty("elements").EnumerateArray()) elements.Add(ReadExpr(x)!);
                return Located(new TupleExpr(elements), e);
            }
            case "Set":
            {
                var elements = new List<Expression>();
                foreach (var x in e.GetProperty("elements").EnumerateArray()) elements.Add(ReadExpr(x)!);
                return Located(new SetExpr(elements), e);
            }
            case "Dict":
            {
                var entries = new List<(Expression, Expression)>();
                foreach (var kv in e.GetProperty("entries").EnumerateArray())
                    entries.Add((ReadExpr(kv.GetProperty("key"))!, ReadExpr(kv.GetProperty("value"))!));
                return Located(new DictExpr(entries), e);
            }
            case "FString":
            {
                var parts = new List<FStringPart>();
                foreach (var p in e.GetProperty("parts").EnumerateArray())
                    parts.Add(new FStringPart
                    {
                        IsExpr = Flag(p, "isExpr"),
                        Text = Str(p, "text"),
                        Expr = Has(p, "expr") ? ReadExpr(p.GetProperty("expr")) : null,
                        FormatSpec = Str(p, "spec"),
                    });
                return Located(new FStringExpr(parts), e);
            }
            case "ListComp":
                return Located(new ListCompExpr(
                    ReadExpr(e.GetProperty("element"))!, Str(e, "varName"),
                    ReadExpr(e.GetProperty("iterable"))!, Str(e, "var2Name"),
                    Has(e, "iterable2") ? ReadExpr(e.GetProperty("iterable2")) : null,
                    Has(e, "filter") ? ReadExpr(e.GetProperty("filter")) : null), e);
            case "Ternary":
                return Located(new TernaryExpr(ReadExpr(e.GetProperty("trueVal"))!,
                    ReadExpr(e.GetProperty("condition"))!, ReadExpr(e.GetProperty("falseVal"))!), e);
            case "Walrus":
                return Located(new WalrusExpr(Str(e, "varName"), ReadExpr(e.GetProperty("value"))!), e);
            case "Lambda":
            {
                var parameters = new List<Param>();
                foreach (var p in e.GetProperty("params").EnumerateArray()) parameters.Add(ReadParam(p));
                return Located(new LambdaExpr(parameters, ReadExpr(e.GetProperty("body"))!), e);
            }
            case "Await": return Located(new AwaitExpr(ReadExpr(e.GetProperty("operand"))!), e);
            case "Yield":
                return Located(new YieldExpr(Has(e, "value") ? ReadExpr(e.GetProperty("value")) : null), e);
            default:
                throw new CompilerError("PyParser", $"unknown expression kind '{kind}' from the translator",
                    Int(e, "line"), 0);
        }
    }
}

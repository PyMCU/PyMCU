namespace PyMCU.Frontend;

/// <summary>
/// An unannotated parameter every call hands a string is a `str` parameter.
///
/// TypeInference joins integer evidence only, so `def g(msg): print(msg)` called as
/// `g("hi")` left `msg` untyped: a real subroutine received the text's flash address in a
/// slot read as a number and printed it (`144`), and a method received the interned id
/// (`1`). With the annotation filled in, the parameter takes the path `msg: str` takes.
///
/// Evidence is a string literal, or a name the module binds to exactly one string literal.
/// One-character literals alone decide only for a plain subroutine (they double as
/// character codes everywhere else).
/// A parameter that also receives anything else keeps its empty annotation: the text and
/// the number need two representations, and the call site says so where it happens. A
/// `None` (as an argument or the default) is neither and does not decide the type.
/// </summary>
public static class StrParamInference
{
    private enum Seen { None = 0, Str = 1, Other = 2, Char = 4 }

    public static void InferProgram(ProgramNode main, IEnumerable<ProgramNode> modules)
    {
        var programs = new List<ProgramNode> { main };
        programs.AddRange(modules);

        var functions = new Dictionary<string, List<FunctionDef>>();
        var methods = new Dictionary<string, List<FunctionDef>>();
        foreach (var prog in programs)
        {
            foreach (var f in prog.Functions) Add(functions, f.Name, f);
            foreach (var m in TypeInference.ClassMethods(prog)) Add(methods, m.Name, m);
        }

        var seen = new Dictionary<FunctionDef, Seen[]>();
        Seen[] SeenOf(FunctionDef f)
        {
            if (!seen.TryGetValue(f, out var s))
            {
                s = new Seen[f.Params.Count];
                for (int i = 0; i < f.Params.Count; i++)
                    if (f.Params[i].DefaultValue is { } d and not NoneLiteral)
                        s[i] = d is StringLiteral ? Classify(d, new HashSet<string>()) : Seen.Other;
                seen[f] = s;
            }
            return s;
        }

        foreach (var prog in programs)
        {
            var strNames = ModuleStrNames(prog);
            var bodies = new List<List<Statement>> { prog.GlobalStatements };
            bodies.AddRange(prog.Functions.Select(f => f.Body.Statements));
            bodies.AddRange(TypeInference.ClassMethods(prog).Select(m => m.Body.Statements));

            foreach (var body in bodies)
            foreach (var e in TypeInference.WalkExpressions(body))
            {
                if (e is not CallExpr call) continue;
                List<FunctionDef>? targets = null;
                int offset = 0;
                if (call.Callee is VariableExpr fn && functions.TryGetValue(fn.Name, out var fs))
                    targets = fs;
                else if (call.Callee is MemberAccessExpr ma && methods.TryGetValue(ma.Member, out var ms))
                {
                    targets = ms;
                    offset = 1;   // self
                }
                if (targets == null) continue;

                foreach (var f in targets)
                {
                    var s = SeenOf(f);
                    int pos = offset;
                    foreach (var arg in call.Args)
                    {
                        int index;
                        Expression value;
                        if (arg is KeywordArgExpr kw)
                        {
                            index = f.Params.FindIndex(p => p.Name == kw.Key);
                            value = kw.Value;
                        }
                        else
                        {
                            index = pos++;
                            value = arg;
                        }
                        if (index < 0 || index >= s.Length) continue;
                        s[index] |= Classify(value, strNames);
                    }
                }
            }
        }

        var methodDefs = methods.Values.SelectMany(l => l).ToHashSet();
        foreach (var (f, s) in seen)
            for (int i = 0; i < s.Length; i++)
            {
                if (f.Params[i].Type.Length > 0 || (s[i] & Seen.Other) != 0) continue;
                // One character alone is a string only where it already travelled as one:
                // a plain subroutine received its flash address. A method or an @inline body
                // received the character code, and code that does arithmetic on it is right.
                bool text = (s[i] & Seen.Str) != 0
                    || (s[i] == Seen.Char && !f.IsInline && !methodDefs.Contains(f));
                if (text) f.Params[i].Type = "str";
            }
    }

    private static Seen Classify(Expression e, HashSet<string> strNames) => e switch
    {
        NoneLiteral => Seen.None,
        // One character is also a character code in PyMCU (`write_char('A')`); see the
        // decision below.
        StringLiteral { Value.Length: 1 } => Seen.Char,
        StringLiteral => Seen.Str,
        VariableExpr v when strNames.Contains(v.Name) => Seen.Str,
        // `"h" + "i"`: a sum of strings the compiler folds is one more string.
        BinaryExpr { Op: BinaryOp.Add } b when IsText(Classify(b.Left, strNames))
                                            && IsText(Classify(b.Right, strNames)) => Seen.Str,
        _ => Seen.Other,
    };

    private static bool IsText(Seen s) => s is Seen.Str or Seen.Char;

    // Module-level names bound once, to a string literal.
    private static HashSet<string> ModuleStrNames(ProgramNode prog)
    {
        var count = new Dictionary<string, int>();
        var str = new HashSet<string>();
        void Bind(string name, Expression? value)
        {
            count[name] = count.GetValueOrDefault(name) + 1;
            if (value is StringLiteral) str.Add(name);
        }
        foreach (var s in prog.GlobalStatements)
        {
            switch (s)
            {
                case AssignStmt { Target: VariableExpr t } a: Bind(t.Name, a.Value); break;
                case VarDecl vd: Bind(vd.Name, vd.Init); break;
                case AnnAssign aa: Bind(aa.Target, aa.Value); break;
            }
        }
        str.RemoveWhere(n => count[n] != 1);
        return str;
    }

    private static void Add(Dictionary<string, List<FunctionDef>> map, string key, FunctionDef f)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<FunctionDef>();
        list.Add(f);
    }
}

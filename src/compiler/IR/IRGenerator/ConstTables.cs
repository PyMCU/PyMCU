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

using PyMCU.Frontend;

namespace PyMCU.IR.IRGenerator;

// A lookup table written as a plain list and read with an index only known at run time.
//
//     DIGITS = [0x3F, 0x06, 0x5B, ...]      # gfedcba, ten entries
//     def show(self, digit):
//         pattern = DIGITS[digit]
//
// This is how every 7-segment guide, every font and every gamma table is written, in
// CircuitPython and in MicroPython alike. An all-constant list lives as separate variables
// (`DIGITS__0`, `DIGITS__1`, ..), which is free and exactly right for a constant subscript
// and has nothing to index when the subscript is a run-time value: the program was refused
// and told to write `DIGITS: uint8[10] = [...]`, an annotation the idiom does not have.
//
// The values are constants and the table is never written, so the storage it wants is FLASH.
// It is materialised ON DEMAND, at the first run-time subscript that needs it, which is what
// keeps a table only ever indexed with a constant at zero cost: nothing is emitted for it and
// its element variables fold away as before.
public partial class IRGenerator
{
    // The constant elements of an all-constant list, filed under the same key as its size, so
    // a run-time subscript can build a flash table without going back to the AST.
    private readonly Dictionary<string, List<int>> ctArrayConstElements = new();

    // How many times each bare name is bound or written anywhere in the program: a subscript
    // store, a slice store, a rebinding, a loop variable, or being handed to a call that could
    // store through it. Counted once over the whole program and deliberately unscoped, because
    // a table that is written anywhere must not become flash anywhere.
    private readonly Dictionary<string, int> nameWriteCounts = new();

    // Flash tables already materialised, keyed by the array they came from.
    private readonly Dictionary<string, string> materialisedConstTables = new();

    private int _constTableCounter;

    // The values of every MODULE-LEVEL all-constant list, by the name the source wrote,
    // collected before any function is lowered.
    //
    // The module's own statements run inside the synthesized main, so such a list is filed under
    // `main.<name>` while a plain function reading it looks under `<fn>.<name>` and then the bare
    // name. Both miss, and the subscript used to fall through to the register-bit path: a lookup
    // table read from an ordinary function reported "Bit index must be constant for reading",
    // about a program with no register in it. Collecting the values in the prescan answers it
    // whatever order the functions are lowered in.
    private readonly Dictionary<string, List<int>> moduleConstLists = new();

    private List<int>? ModuleConstListValues(string name)
        => moduleConstLists.TryGetValue(name, out var values) ? values : null;

    private void ScanNameWrites(ProgramNode mainAst, IEnumerable<ProgramNode> importedModules)
    {
        nameWriteCounts.Clear();
        materialisedConstTables.Clear();
        ctArrayConstElements.Clear();
        moduleConstLists.Clear();
        _constTableCounter = 0;

        void Note(string? name)
        {
            if (string.IsNullOrEmpty(name)) return;
            nameWriteCounts[name!] = nameWriteCounts.GetValueOrDefault(name!) + 1;
        }

        void NoteTarget(Expression? target)
        {
            switch (target)
            {
                case VariableExpr ve: Note(ve.Name); break;
                case IndexExpr { Target: VariableExpr iv }: Note(iv.Name); break;
                // A field is counted under its MEMBER name, which is what the field-held table
                // is checked against: `self._levels = levels` is the one binding that creates it
                // and `self._levels[i] = v` is the write that rules flash out.
                case MemberAccessExpr me: Note(me.Member); break;
                case IndexExpr { Target: MemberAccessExpr im }: Note(im.Member); break;
                case TupleExpr te: foreach (var el in te.Elements) NoteTarget(el); break;
            }
        }

        foreach (var prog in importedModules.Prepend(mainAst))
        {
            foreach (var stmt in prog.GlobalStatements)
            {
                if (stmt is not AssignStmt { Target: VariableExpr mv } ga) continue;
                var elements = ga.Value switch
                {
                    ListExpr le => le.Elements,
                    TupleExpr tp => tp.Elements,
                    _ => null,
                };
                if (elements is { Count: > 0 } && ConstValuesOf(elements) is { } values)
                    moduleConstLists[mv.Name] = values;
            }
        }

        foreach (var prog in importedModules.Prepend(mainAst))
        foreach (var node in AstNodes(prog, descendIntoFunctions: true))
        {
            switch (node)
            {
                case AssignStmt asg: NoteTarget(asg.Target); break;
                case AugAssignStmt aug: NoteTarget(aug.Target); break;
                case AnnAssign ann: Note(ann.Target); break;
                case ForStmt fs: Note(fs.VarName); break;
                case CallExpr call:
                    // A table handed to a function could be stored through there, and such a
                    // write lands on the element variables rather than on the table in flash.
                    // Declining costs nothing: the program keeps the behaviour it has today.
                    foreach (var arg in call.Args)
                    {
                        if (arg is VariableExpr av) Note(av.Name);
                        else if (arg is KeywordArgExpr { Value: VariableExpr kv }) Note(kv.Name);
                        else if (arg is StarArgExpr { Value: VariableExpr sv }) Note(sv.Name);
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// The elements of a compile-time list of numbers, as constants, or null when any element
    /// is not one.
    /// </summary>
    private List<int>? ConstValuesOf(IEnumerable<Expression> elements)
    {
        var values = new List<int>();
        foreach (var e in elements)
        {
            if (!TryEvalElemConst(e, out int v)) return null;
            values.Add(v);
        }
        return values.Count > 0 ? values : null;
    }

    /// <summary>
    /// The same materialisation for a list that never became an array: one bound to a parameter,
    /// or one held in a `self` field. <paramref name="cacheKey"/> names the binding so repeated
    /// subscripts share one table; <paramref name="writtenName"/> is the source name whose
    /// writes decide whether the values can live in flash at all, and
    /// <paramref name="bindings"/> how many of its counted writes are the binding that created
    /// it: one for a name or a field, none for a parameter, whose binding is the call site and
    /// is not a write that appears in the source at all.
    /// </summary>
    private string? TryMaterialiseConstTableFromValues(string cacheKey, string writtenName,
                                                       List<int> values, int bindings = 1)
    {
        if (materialisedConstTables.TryGetValue(cacheKey, out var already)) return already;
        if (values.Count == 0) return null;
        if (nameWriteCounts.GetValueOrDefault(writtenName) > bindings) return null;

        DataType elemDt = WidestElemType(values);
        int elemSize = elemDt.SizeOf();
        string name = "__cttab_" + (++_constTableCounter) + "_" + Sanitise(writtenName);

        var bytes = new List<int>(values.Count * elemSize);
        foreach (int v in values)
            for (int b = 0; b < elemSize; b++)
                bytes.Add((v >> (8 * b)) & 0xFF);

        pendingFlashData.Add(new FlashData(name, bytes));
        flashArrays.Add(name);
        arraySizes[name] = values.Count;
        arrayElemTypes[name] = elemDt;

        materialisedConstTables[cacheKey] = name;
        return name;
    }

    /// <summary>
    /// Turns the all-constant array filed under <paramref name="key"/> into a flash table and
    /// returns the table's name, or null when it cannot: the values were never recorded, or
    /// the program binds the name more than the once that created the table. Each array is
    /// materialised at most once, however many run-time subscripts reach it.
    /// </summary>
    private string? TryMaterialiseConstTable(string key)
    {
        if (materialisedConstTables.TryGetValue(key, out var already)) return already;
        if (!ctArrayConstElements.TryGetValue(key, out var values) || values.Count == 0) return null;

        int dot = key.LastIndexOf('.');
        string bare = dot >= 0 ? key[(dot + 1)..] : key;
        // The definition itself is one of the writes counted, so exactly one is the table being
        // created and anything beyond that is a write this table could not follow.
        if (nameWriteCounts.GetValueOrDefault(bare) != 1) return null;

        DataType elemDt = arrayElemTypes.TryGetValue(key, out var dt) && dt != DataType.UNKNOWN
            ? dt : WidestElemType(values);
        int elemSize = elemDt.SizeOf();
        string name = "__cttab_" + (++_constTableCounter) + "_" + Sanitise(bare);

        // FlashData is byte-oriented and the element is stored little-endian, which is what the
        // flash branch of the subscript lowering already knows how to read back, so a table of
        // 16-bit entries needs no new IR node and no backend change.
        var bytes = new List<int>(values.Count * elemSize);
        foreach (int v in values)
            for (int b = 0; b < elemSize; b++)
                bytes.Add((v >> (8 * b)) & 0xFF);

        pendingFlashData.Add(new FlashData(name, bytes));
        flashArrays.Add(name);
        arraySizes[name] = values.Count;
        arrayElemTypes[name] = elemDt;

        materialisedConstTables[key] = name;
        return name;
    }


    // ------------------------------------------------------------------ dict of rows
    //
    // `{0: [1,1,1,1,1,1,0], 1: [0,1,1,0,0,0,0], ...}` is the canonical digit table, and every
    // font and every gamma curve is written the same way. It is not a container: it is ten rows
    // of seven constants, a rectangle with a known shape, so it is a flat table in flash and the
    // key picks the row.
    //
    // A ROW VIEW is what a run-time lookup yields: the table, the row width, and the run-time
    // row index. It is not a value -- there is nothing to hold seven bytes in -- so it lives
    // under the name it was assigned to, and `row[k]`, `len(row)`, `for v in row` and
    // `zip(seq, row)` read through it.
    private readonly Dictionary<string, (string Table, int Width, Val Index)> rowViews = new();

    /// <summary>
    /// The rows of a dict whose values are all lists of the same length and all constants,
    /// with the constant integer key of each. Character codes count: a one-character string
    /// literal folds to its code, which is what makes `ch in table` work against a run-time
    /// byte. Null when the dict is not that rectangle.
    /// </summary>
    private List<(int Key, List<int> Row)>? DictKeyedRows(Frontend.DictExpr d)
    {
        if (d.Entries.Count == 0) return null;
        var rows = new List<(int Key, List<int> Row)>();
        var seen = new HashSet<int>();
        int width = -1;
        foreach (var (kE, vE) in d.Entries)
        {
            int key;
            switch (kE)
            {
                case IntegerLiteral kl: key = kl.Value; break;
                case BooleanLiteral kb: key = kb.Value ? 1 : 0; break;
                case UnaryExpr { Op: Frontend.UnaryOp.Negate, Operand: IntegerLiteral kn }:
                    key = -kn.Value; break;
                // A multi-character key is an interned id, which no run-time value can equal,
                // so a table keyed by one cannot be indexed at run time and is not this shape.
                case StringLiteral ks when ks.Value.Length == 1: key = ks.Value[0]; break;
                default: return null;
            }
            if (!seen.Add(key)) return null;

            var elements = vE switch
            {
                ListExpr le => le.Elements,
                TupleExpr te => te.Elements,
                _ => null,
            };
            if (elements == null) return null;
            if (ConstValuesOf(elements) is not { } values) return null;
            if (width < 0) width = values.Count;
            else if (values.Count != width) return null;                     // not a rectangle
            rows.Add((key, values));
        }
        return width > 0 ? rows : null;
    }

    /// <summary>Just the rows, in the order written.</summary>
    private List<List<int>>? DictRows(Frontend.DictExpr d)
        => DictKeyedRows(d)?.Select(r => r.Row).ToList();

    /// <summary>
    /// Lays the rows out end to end in flash and returns the table name, or null when the values
    /// cannot live there. Row k starts at k * width.
    /// </summary>
    private string? MaterialiseDictRows(string cacheKey, string writtenName, List<List<int>> rows,
                                        int bindings = 1)
    {
        var flat = new List<int>();
        foreach (var row in rows) flat.AddRange(row);
        return TryMaterialiseConstTableFromValues(cacheKey, writtenName, flat, bindings);
    }

    /// <summary>
    /// The ROW INDEX a key selects, for a rectangle whose keys are arbitrary constants.
    /// Contiguous ascending keys are a subtraction; anything else is a search over a key row
    /// laid in flash next to the rectangle, so a 96-glyph font costs the same code as a
    /// 7-glyph one. A key that matches nothing raises KeyError, which is what the author's own
    /// `if k not in table: raise` has usually excluded already.
    /// </summary>
    private Val EmitDictRowIndex(List<(int Key, List<int> Row)> rows, string cacheKey,
                                 string writtenName, Val keyVal)
    {
        int n = rows.Count;
        bool contiguous = true;
        for (int i = 1; i < n && contiguous; i++)
            if (rows[i].Key != rows[0].Key + i) contiguous = false;

        if (contiguous)
        {
            int baseKey = rows[0].Key;
            Temporary idx = MakeTemp(DataType.UINT16);
            if (baseKey == 0) Emit(new Copy(keyVal, idx));
            else Emit(new Binary(BinaryOp.Sub, keyVal, new Constant(baseKey), idx));
            EmitKeyErrorIfOutOfRange(idx, n);
            return idx;
        }

        // The keys, in the order written, as their own flash table. It is a separate table so
        // the rectangle stays exactly the bytes the rows have.
        string? keysTable = TryMaterialiseConstTableFromValues(
            "dictkeys:" + cacheKey, writtenName + "_keys",
            rows.Select(r => r.Key).ToList(), bindings: int.MaxValue);
        if (keysTable == null)
        {
            // No table to search: fall back to a compare chain, which is what the scalar dict
            // lookup does and costs one compare per key.
            Temporary chainIdx = MakeTemp(DataType.UINT16);
            string chainEnd = MakeLabel();
            Emit(new Copy(new Constant(n), chainIdx));
            for (int i = 0; i < n; i++)
            {
                string next = MakeLabel();
                Emit(new JumpIfNotEqual(keyVal, new Constant(rows[i].Key), next));
                Emit(new Copy(new Constant(i), chainIdx));
                Emit(new Jump(chainEnd));
                Emit(new Label(next));
            }
            Emit(new Label(chainEnd));
            EmitKeyErrorIfOutOfRange(chainIdx, n);
            return chainIdx;
        }

        Temporary found = MakeTemp(DataType.UINT16);
        Temporary i2 = MakeTemp(DataType.UINT16);
        string loop = MakeLabel(), done = MakeLabel(), next2 = MakeLabel();
        Emit(new Copy(new Constant(n), found));       // n means "not found"
        Emit(new Copy(new Constant(0), i2));
        Emit(new Label(loop));
        Emit(new JumpIfGreaterOrEqual(i2, new Constant(n), done));
        Temporary k = MakeTemp(DataType.UINT8);
        Emit(new ArrayLoadFlash(keysTable, i2, k));
        Emit(new JumpIfNotEqual(k, keyVal, next2));
        Emit(new Copy(i2, found));
        Emit(new Jump(done));
        Emit(new Label(next2));
        Emit(new AugAssign(BinaryOp.Add, i2, new Constant(1)));
        Emit(new Jump(loop));
        Emit(new Label(done));
        EmitKeyErrorIfOutOfRange(found, n);
        return found;
    }

    private void EmitKeyErrorIfOutOfRange(Val idx, int n)
    {
        string ok = MakeLabel();
        Emit(new JumpIfLessThan(idx, new Constant(n), ok));
        string? localCatch = tryCatchStack.Count > 0 ? tryCatchStack[^1] : null;
        Emit(new SignalError(new Constant(4 /* KeyError */), localCatch));
        Emit(new Label(ok));
    }

    /// <summary>
    /// The row view a name holds, following the same qualification order every other lookup
    /// here uses. Null when the name is not one.
    /// </summary>
    private (string Table, int Width, Val Index)? ResolveRowView(string name)
    {
        string?[] candidates =
        {
            !string.IsNullOrEmpty(currentInlinePrefix) ? currentInlinePrefix + name : null,
            !string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + name : null,
            name,
        };
        foreach (var c in candidates)
            if (c != null && rowViews.TryGetValue(c, out var rv)) return rv;
        return null;
    }

    /// <summary>
    /// The flash offset of element <paramref name="k"/> of a row: `index * width + k`, folded
    /// when the row index is itself a constant.
    /// </summary>
    private Val EmitRowOffset((string Table, int Width, Val Index) rv, int k)
    {
        if (rv.Index is Constant ic) return new Constant(ic.Value * rv.Width + k);
        Temporary scaled = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Mul, rv.Index, new Constant(rv.Width), scaled));
        if (k == 0) return scaled;
        Temporary off = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, scaled, new Constant(k), off));
        return off;
    }

    private static string Sanitise(string s)
        => new(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}

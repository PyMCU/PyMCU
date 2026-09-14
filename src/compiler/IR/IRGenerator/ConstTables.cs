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

    private static string Sanitise(string s)
        => new(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}

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
using PyMCU.Frontend;
using PyMCU.IR;

namespace PyMCU.IR.IRGenerator;

/// <summary>
/// RFC 0008 phase 1 -- "files without a filesystem" (romfs).
///
/// `open("name", "rb")` resolves at compile time to a handle over a flash blob the
/// driver passed in with `--embed name=path`. There is no file table on the chip: a
/// handle is (blob label, length, position variable) kept in these compile-time maps,
/// and `read`/`seek`/`tell`/`close`/`with` are lowered here rather than dispatched to
/// any function. `read(n)` mints a VIEW -- (label, start, avail) -- never a copy, so
/// `f.read(2)[0]` and `struct.unpack(fmt, f.read(n))` read the blob with ArrayLoadFlash
/// straight out of program memory, one byte per index.
///
/// The class tag the handles carry is a marker, not a ZCA class: it exists so the
/// instance-alias machinery (`x = f`, `self._font = open(...)`, `with open() as f`)
/// renames the handle like any other object. No method on it is ever looked up --
/// ResolveRomfsHandleExpr intercepts member calls before function resolution runs.
/// </summary>
public partial class IRGenerator
{
    /// The class tag a romfs handle carries. Not a real class: present so instance
    /// tracking treats the handle like an object (aliasing, `x = f`, `with` ... as f).
    private const string RomfsClassMark = "pymcu.fs.RomFile";

    /// The `--embed NAME=PATH` pairs, handed over by IrGenerationPhase before Generate().
    public List<string>? EmbeddedFiles { get; set; }

    private sealed class RomfsHandle
    {
        public string Inst = "";      // the (qualified) name the handle answers to
        public string FileName = "";  // as open() spelled it -- diagnostics
        public string Label = "";     // the flash blob's label
        public byte[] Bytes = [];     // the blob itself, for compile-time folds
        public string PosVar = "";    // uint16 variable holding the position
        public int? ConstPos = 0;     // the position while it is compile-time known
        public bool Closed;
    }

    /// What `f.read(n)` hands back: a window over the blob. `Start`/`Avail` are Vals --
    /// constants while the position is compile-time known, temps once a runtime seek
    /// moved it -- so `view[i]` folds when it can and emits LPM when it cannot.
    private sealed class RomfsView
    {
        public string Label = "";
        public Val Start = new Constant(0);
        public Val Avail = new Constant(0);
        public byte[] Bytes = [];
    }

    private readonly Dictionary<string, string> romfsEmbedPaths = new();   // name -> host path
    private readonly Dictionary<string, (string Label, byte[] Bytes)> romfsBlobs = new();
    private readonly Dictionary<string, RomfsHandle> romfsHandles = new(); // instance key -> handle
    private readonly Dictionary<string, RomfsView> romfsViews = new();     // bound name -> view
    private readonly Dictionary<string, string> romfsBufLen = new();       // readline buffer -> len var
    private bool romfsEmbedParsed;
    private int romfsId;

    /// Parses EmbeddedFiles into the name-to-path table, once. A malformed pair is a
    /// compile error: the driver wrote it, so this names the flag, not the source.
    private void EnsureRomfsTable()
    {
        if (romfsEmbedParsed) return;
        romfsEmbedParsed = true;
        if (EmbeddedFiles == null) return;
        foreach (var spec in EmbeddedFiles)
        {
            int eq = spec.IndexOf('=');
            if (eq <= 0)
                throw new Common.CompilerError("CompileError",
                    $"--embed expects NAME=PATH, got '{spec}'", 0);
            romfsEmbedPaths[spec[..eq]] = spec[(eq + 1)..];
        }
    }

    /// The handle an expression names -- `f` resolves through ResolveNameKey, `self._font`
    /// through the field-flattening key every other field lookup uses. Null otherwise.
    private RomfsHandle? ResolveRomfsHandleExpr(Expression e)
    {
        string? key = e switch
        {
            VariableExpr ve => ResolveNameKey(ve.Name),
            MemberAccessExpr => SequenceKeyOf(e),
            _ => null,
        };
        return key != null && romfsHandles.TryGetValue(key, out var h) ? h : null;
    }

    /// True when <paramref name="c"/> is `handle.read(...)` on a romfs handle.
    private bool TryRomfsReadCall(CallExpr c, out RomfsHandle h)
    {
        h = null!;
        return c.Callee is MemberAccessExpr { Member: "read" } rm
               && ResolveRomfsHandleExpr(rm.Object) is { } found
               && (h = found) != null;
    }

    /// True when <paramref name="c"/> is `os.<member>(...)` on the os/uos module.
    private bool IsOsFsCall(CallExpr c, string member)
    {
        if (c.Callee is not MemberAccessExpr { Object: VariableExpr mv } ma || ma.Member != member)
            return false;
        if (!NamesAModuleMember(mv.Name, member)) return false;
        string realMod = TryImportedAlias(mv.Name, out var rm) && rm != null ? rm : mv.Name;
        return realMod.Replace('.', '_') is "os" or "uos" or "pymcu_os";
    }

    /// The blob for <paramref name="name"/>, materialised on first use: one FlashData
    /// through the same pendingFlashData path const[uint8[N]] tables already take --
    /// .mir serialises it as a `blob` and every backend reads it with the flash-load
    /// machinery it already has. Nothing is emitted for a file no open() reaches.
    private (string Label, byte[] Bytes) RomfsBlob(string name, ASTNode at)
    {
        if (romfsBlobs.TryGetValue(name, out var blob)) return blob;
        string path = romfsEmbedPaths[name];
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex)
        {
            throw UserError(
                $"open('{name}'): the embedded file '{path}' could not be read: {ex.Message}", at);
        }
        string label = "__romfs_" + (++romfsId) + "_" + Sanitise(name);
        pendingFlashData.Add(new FlashData(label, bytes.Select(b => (int)b).ToList()));
        flashArrays.Add(label);
        arraySizes[label] = bytes.Length;
        arrayElemTypes[label] = DataType.UINT8;
        blob = (label, bytes);
        romfsBlobs[name] = blob;
        return blob;
    }

    /// `open(name, mode)` -- the whole call. The handle is a compile-time object named
    /// after the binding target (pendingConstructorTarget, the same channel constructors
    /// use) or minted anonymously when the call is not an assignment.
    private Val EmitRomfsOpen(CallExpr expr)
    {
        EnsureRomfsTable();

        Expression? nameExpr = null;
        Expression? modeExpr = null;
        int pos = 0;
        foreach (var a in expr.Args)
        {
            if (a is KeywordArgExpr kw)
            {
                if (kw.Key is "file" or "name") nameExpr = kw.Value;
                else if (kw.Key == "mode") modeExpr = kw.Value;
                else throw UserError($"open() got an unexpected keyword argument '{kw.Key}'", a);
            }
            else
            {
                if (pos == 0) nameExpr = a;
                else if (pos == 1) modeExpr = a;
                else throw UserError("open() on an embedded file takes (name[, mode])", a);
                pos++;
            }
        }
        if (nameExpr == null) throw UserError("open() needs a file name", expr.Callee);

        string? name = StaticStringOf(nameExpr) ?? TryGetCompileTimeText(nameExpr);
        if (name == null)
            throw UserError(
                "open() needs the file name at compile time: an embedded file is bound to "
                + "its blob while the program is being built, and there is no filesystem on "
                + "the chip to look a run-time name up in. Write the name as a string "
                + "literal, or keep it in a field or name whose text the compiler knows.",
                nameExpr);

        string mode = "r";
        if (modeExpr != null)
        {
            string? m = StaticStringOf(modeExpr) ?? TryGetCompileTimeText(modeExpr);
            if (m == null)
                throw UserError(
                    "open() needs the mode at compile time for the same reason it needs the "
                    + "name: write it as a literal, 'rb' for a binary file.", modeExpr);
            mode = m;
        }
        if (mode.Any(c => c is 'w' or 'a' or 'x' or '+'))
            throw UserError(
                $"open('{name}', '{mode}'): an embedded file lives in flash, which is not "
                + "writable -- open it read-only ('rb'), or write the bytes with the EEPROM "
                + "helpers.", modeExpr ?? expr.Callee);
        if (mode.Length == 0 || mode.Any(c => c is not ('r' or 'b' or 't')))
            throw UserError(
                $"open('{name}', '{mode}'): an embedded file opens read-only -- 'r' or 'rb'.",
                modeExpr ?? expr.Callee);

        if (!romfsEmbedPaths.TryGetValue(name, out _))
            throw UserError(
                $"open('{name}'): no file of that name is embedded in this build. Embed it "
                + $"with `files = [\"{name}\"]` under [tool.pymcu] in pyproject.toml, or put a "
                + "file of that name in the project's sources directory -- the driver embeds "
                + "it automatically when an open() names it.", nameExpr);

        var blob = RomfsBlob(name, nameExpr);

        string inst = pendingConstructorTarget;
        if (string.IsNullOrEmpty(inst))
        {
            string bBase = !string.IsNullOrEmpty(currentFunction) ? currentFunction : "main";
            inst = bBase + ".__romfs" + (++romfsId);
        }
        pendingConstructorTarget = "";
        instanceClasses[inst] = RomfsClassMark;
        virtualInstances.Add(inst);

        string posVar = inst + "__pos";
        variableTypes[posVar] = DataType.UINT16;
        romfsHandles[inst] = new RomfsHandle
        {
            Inst = inst, FileName = name, Label = blob.Label, Bytes = blob.Bytes,
            PosVar = posVar, ConstPos = 0,
        };
        Emit(new Copy(new Constant(0), new Variable(posVar, DataType.UINT16)));
        return new Variable(inst, DataType.UINT16);
    }

    /// Every method a romfs handle answers. None of them is a function call: each lowers
    /// to at most a position update plus (for read) the view the caller subscripts or
    /// hands to struct.unpack.
    private Val EmitRomfsMethod(RomfsHandle h, CallExpr expr, MemberAccessExpr memC)
    {
        if (h.Closed && memC.Member is not ("__exit__" or "close"))
            throw UserError(
                $"I/O operation on a closed file: '{h.FileName}' was closed earlier -- "
                + "a romfs handle's state is known at compile time, so this is diagnosed "
                + "here rather than raised.", memC);

        switch (memC.Member)
        {
            case "read":
                return new Variable(EmitRomfsReadView(h, expr), DataType.UINT8);

            case "readinto":
            {
                if (expr.Args.Count != 1)
                    throw UserError("readinto(buf) takes exactly one argument", expr.Callee);
                string? bufKey = expr.Args[0] is VariableExpr bv ? ResolveNameKey(bv.Name)
                    : SequenceKeyOf(expr.Args[0]);
                if (bufKey == null || !arraySizes.TryGetValue(bufKey, out int bufSize))
                    throw UserError(
                        "readinto(buf) needs a fixed-size buffer: the count it reads is the "
                        + "buffer's own size, which must be known at compile time.",
                        expr.Args[0]);
                var (start, avail) = EmitRomfsSpan(h, bufSize);
                string iVar = "__romi_" + (++romfsId);
                variableTypes[iVar] = DataType.UINT16;
                Emit(new Copy(new Constant(0), new Variable(iVar, DataType.UINT16)));
                string top = MakeLabel();
                string done = MakeLabel();
                Emit(new Label(top));
                Emit(new JumpIfGreaterOrEqual(new Variable(iVar, DataType.UINT16), avail, done));
                var tByte = MakeTemp(DataType.UINT8);
                Emit(new ArrayLoadFlash(h.Label, AddSpanOffset(start, new Variable(iVar, DataType.UINT16)), tByte));
                Emit(new ArrayStore(bufKey, new Variable(iVar, DataType.UINT16), tByte,
                    DataType.UINT8, bufSize));
                Emit(new AugAssign(BinaryOp.Add, new Variable(iVar, DataType.UINT16), new Constant(1)));
                Emit(new Jump(top));
                Emit(new Label(done));
                return avail;
            }

            case "readline":
            {
                if (expr.Args.Count != 1)
                    throw UserError(
                        "readline() on an embedded file needs a bound: write readline(max) so "
                        + "the line buffer has a size the compiler can see -- there is no heap "
                        + "for an unbounded line to grow into.", expr.Callee);
                int max;
                try { max = EvaluateConstantExpr(expr.Args[0]); }
                catch (Common.CompilerError)
                {
                    throw UserError(
                        "readline(max) needs max at compile time -- the line buffer is "
                        + "allocated to it.", expr.Args[0]);
                }
                if (max <= 0)
                    throw UserError("readline(max) needs max > 0", expr.Args[0]);

                var (start, avail) = EmitRomfsSpan(h, max, advancePos: false);
                string arr = "__roml_" + (++romfsId);
                arraySizes[arr] = max;
                arrayElemTypes[arr] = DataType.UINT8;
                arraysWithVariableIndex.Add(arr);
                moduleSramArrays.Add(arr);
                string lenVar = arr + "_len";
                variableTypes[lenVar] = DataType.UINT16;
                romfsBufLen[arr] = lenVar;

                string iVar = "__romi_" + (++romfsId);
                variableTypes[iVar] = DataType.UINT16;
                Emit(new Copy(new Constant(0), new Variable(iVar, DataType.UINT16)));
                string top = MakeLabel();
                string done = MakeLabel();
                Emit(new Label(top));
                Emit(new JumpIfGreaterOrEqual(new Variable(iVar, DataType.UINT16), avail, done));
                var tByte = MakeTemp(DataType.UINT8);
                Emit(new ArrayLoadFlash(h.Label, AddSpanOffset(start, new Variable(iVar, DataType.UINT16)), tByte));
                Emit(new ArrayStore(arr, new Variable(iVar, DataType.UINT16), tByte,
                    DataType.UINT8, max));
                Emit(new AugAssign(BinaryOp.Add, new Variable(iVar, DataType.UINT16), new Constant(1)));
                // The newline is kept in the buffer and ends the line, as CPython's does.
                Emit(new JumpIfEqual(tByte, new Constant('\n'), done));
                Emit(new Jump(top));
                Emit(new Label(done));
                Emit(new Copy(new Variable(iVar, DataType.UINT16), new Variable(lenVar, DataType.UINT16)));
                // The file position is where the line ended: start + bytes consumed,
                // not start + the clamped span (CPython leaves pos just past '\n').
                Emit(new Binary(BinaryOp.Add, start,
                    new Variable(iVar, DataType.UINT16), new Variable(h.PosVar, DataType.UINT16)));
                h.ConstPos = null;
                return new Variable(arr, DataType.UINT8);
            }

            case "seek":
            {
                if (expr.Args.Count < 1 || expr.Args.Count > 2)
                    throw UserError("seek() takes (offset[, whence])", expr.Callee);
                Val off = VisitExpression(expr.Args[0]);
                int whence = 0;
                if (expr.Args.Count == 2)
                {
                    try { whence = EvaluateConstantExpr(expr.Args[1]); }
                    catch (Common.CompilerError)
                    {
                        throw UserError(
                            "seek(off, whence) needs whence at compile time -- write 0, 1 or 2.",
                            expr.Args[1]);
                    }
                }
                var pos = new Variable(h.PosVar, DataType.UINT16);
                switch (whence)
                {
                    case 0:
                        Emit(new Copy(off, pos));
                        h.ConstPos = off is Constant sc ? sc.Value : null;
                        break;
                    case 1:
                        Emit(new AugAssign(BinaryOp.Add, pos, off));
                        h.ConstPos = h.ConstPos is { } cp && off is Constant oc ? cp + oc.Value : null;
                        break;
                    case 2:
                        if (off is Constant ec)
                        {
                            Emit(new Copy(new Constant(h.Bytes.Length + ec.Value), pos));
                            h.ConstPos = h.Bytes.Length + ec.Value;
                        }
                        else
                        {
                            var tEnd = MakeTemp(DataType.UINT16);
                            Emit(new Binary(BinaryOp.Add, new Constant(h.Bytes.Length), off, tEnd));
                            Emit(new Copy(tEnd, pos));
                            h.ConstPos = null;
                        }
                        break;
                    default:
                        throw UserError($"seek(): whence must be 0, 1 or 2, got {whence}",
                            expr.Args[1]);
                }
                return h.ConstPos is { } np ? new Constant(np) : (Val)pos;
            }

            case "tell":
                return h.ConstPos is { } tp ? new Constant(tp) : (Val)new Variable(h.PosVar, DataType.UINT16);

            case "close":
                h.Closed = true;
                return new NoneVal();

            case "write" or "writelines" or "flush" or "truncate":
                throw UserError(
                    $"'{memC.Member}' does not exist on a romfs file: embedded files live in "
                    + "flash and are read-only (RFC 0008).", memC);

            case "__enter__":
                return new Variable(h.Inst, DataType.UINT16);

            case "__exit__":
                h.Closed = true;
                return new NoneVal();

            case "readable": return new Constant(1);
            case "writable": return new Constant(0);
            case "seekable": return new Constant(1);
            case "__bool__": return new Constant(h.Closed ? 0 : 1);

            default:
                throw UserError(
                    $"'{memC.Member}' is not part of the romfs file protocol: the protocol is "
                    + "read(n) with a compile-time n, readinto(buf), readline(max), "
                    + "seek/tell, close and `with` (RFC 0008).", memC);
        }
    }

    /// The (start, avail) of a read: the position is captured first, clamped to what the
    /// file still holds, and advanced by exactly that much. While the position is a
    /// compile-time value all three fold to constants and the only emission is the
    /// position variable kept honest; after a run-time seek the same arithmetic is code.
    /// readline() passes advancePos: false -- it consumes the span only up to the
    /// newline, so it stores start+i itself once the loop knows where the line ended.
    private (Val Start, Val Avail) EmitRomfsSpan(RomfsHandle h, int n, bool advancePos = true)
    {
        var pos = new Variable(h.PosVar, DataType.UINT16);
        if (h.ConstPos is { } cp)
        {
            int a = Math.Max(0, Math.Min(n, h.Bytes.Length - cp));
            if (advancePos)
            {
                h.ConstPos = cp + a;
                Emit(new Copy(new Constant(cp + a), pos));
            }
            return (new Constant(cp), new Constant(a));
        }

        var tStart = MakeTemp(DataType.UINT16);
        Emit(new Copy(pos, tStart));

        // rem = pos >= len ? 0 : len - pos
        var tRem = MakeTemp(DataType.UINT16);
        Emit(new Copy(new Constant(0), tRem));
        string remDone = MakeLabel();
        Emit(new JumpIfGreaterOrEqual(pos, new Constant(h.Bytes.Length), remDone));
        Emit(new Binary(BinaryOp.Sub, new Constant(h.Bytes.Length), pos, tRem));
        Emit(new Label(remDone));

        // avail = min(n, rem)
        var tAvail = MakeTemp(DataType.UINT16);
        Emit(new Copy(new Constant(n), tAvail));
        string avDone = MakeLabel();
        Emit(new JumpIfGreaterOrEqual(tRem, new Constant(n), avDone));
        Emit(new Copy(tRem, tAvail));
        Emit(new Label(avDone));

        if (advancePos) Emit(new AugAssign(BinaryOp.Add, pos, tAvail));
        return (tStart, tAvail);
    }

    /// `f.read(n)`: the count is part of the lowering, so it is a compile-time
    /// constant or the call is refused -- mint the view and register it under its own
    /// name, so a direct `f.read(n)[i]` and an unpack over it resolve it. A name bound
    /// to the result (`hdr = f.read(2)`) records the SAME view under the binding --
    /// Assign.cs does that, so no byte ever copies.
    private string EmitRomfsReadView(RomfsHandle h, CallExpr expr)
    {
        if (expr.Args.Count > 1)
            throw UserError("read() on an embedded file takes at most one argument",
                expr.Callee);
        int n = h.Bytes.Length;   // bare read(): the rest of the file
        if (expr.Args.Count == 1)
        {
            try { n = EvaluateConstantExpr(expr.Args[0]); }
            catch (Common.CompilerError)
            {
                throw UserError(
                    "read(n) on an embedded file needs n at compile time: the bytes "
                    + "come straight out of flash, so the count is part of the "
                    + "lowering. For a run-time length use readinto(buf), which is "
                    + "bounded by the buffer's own size.", expr.Args[0]);
            }
            if (n < 0) n = h.Bytes.Length;
        }
        return EmitRomfsView(h, n);
    }

    private string EmitRomfsView(RomfsHandle h, int n)
    {
        var (start, avail) = EmitRomfsSpan(h, n);
        string view = "__romv" + (++romfsId);
        romfsViews[view] = new RomfsView { Label = h.Label, Start = start, Avail = avail, Bytes = h.Bytes };
        return view;
    }

    /// `view[i]`: a constant index into a constant window folds to the byte itself;
    /// anything else is one ArrayLoadFlash at (start + i). A negative index counts back
    /// from the window's end, like Python's.
    private Val EmitRomfsViewIndex(RomfsView v, Expression idxExpr)
    {
        Val idx = VisitExpression(idxExpr);
        int? availConst = v.Avail is Constant ac ? ac.Value : null;

        if (v.Start is Constant sc && idx is Constant ic)
        {
            int i = ic.Value;
            if (i < 0 && availConst is { } avNeg) i += avNeg;
            if (i < 0 || (availConst is { } avHi && i >= avHi))
                throw new IndexError(
                    $"read result index {ic.Value} out of range for {availConst ?? 0} bytes",
                    idxExpr.Line > 0 ? idxExpr.Line : lastLine, idxExpr.Column);
            return new Constant(v.Bytes[sc.Value + i]);
        }

        var dst = MakeTemp(DataType.UINT8);
        Emit(new ArrayLoadFlash(v.Label, AddSpanOffset(v.Start, idx), dst));
        return dst;
    }

    /// `start + i` as a Val: constant folds; otherwise one Add into a uint16 temp.
    private Val AddSpanOffset(Val start, Val idx)
    {
        if (start is Constant sc && idx is Constant ic) return new Constant(sc.Value + ic.Value);
        if (idx is Constant { Value: 0 }) return start;
        var t = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, start, idx, t));
        return t;
    }

    /// The ten stat fields of an embedded file as literal expressions: everything is 0
    /// except size at [6] -- the only field a compile-time file has that is worth a value.
    private List<Expression> OsStatExprs(string name, ASTNode at)
    {
        EnsureRomfsTable();
        if (!romfsEmbedPaths.TryGetValue(name, out var path))
            throw UserError(
                $"os.stat('{name}'): no file of that name is embedded in this build. Embed it "
                + $"with `files = [\"{name}\"]` under [tool.pymcu] in pyproject.toml, or put a "
                + "file of that name in the project's sources directory.", at);
        int size;
        try { size = (int)new FileInfo(path).Length; }
        catch (Exception ex)
        {
            throw UserError($"os.stat('{name}'): '{path}' could not be read: {ex.Message}", at);
        }
        var exprs = new List<Expression>(new Expression[10]);
        for (int i = 0; i < 10; i++) exprs[i] = new IntegerLiteral(i == 6 ? size : 0);
        return exprs;
    }

    /// `os.stat(name)` in expression position. The tuple exists only while compiling, so
    /// the answer is a marker name -- `os.stat(x)[6]` and `st = os.stat(x)` are bound to
    /// the field exprs by the index and assign hooks.
    private Val EmitOsStat(CallExpr expr)
    {
        if (expr.Args.Count != 1)
            throw UserError("os.stat() on an embedded file takes the file name", expr.Callee);
        string? nm = StaticStringOf(expr.Args[0]) ?? TryGetCompileTimeText(expr.Args[0]);
        if (nm == null)
            throw UserError(
                "os.stat() needs the file name at compile time -- the romfs table exists "
                + "only while the program is being built.", expr.Args[0]);
        var exprs = OsStatExprs(nm, expr.Args[0]);
        string statName = "__romstat" + (++romfsId);
        constSequenceBindings[statName] = exprs;
        return new Variable(statName, DataType.UINT8);
    }

    /// The names `os.listdir(dir)` answers, as string-literal expressions: the embedded
    /// names under dir's prefix, one segment deep, sorted so the lowering is stable.
    private List<Expression> OsListdirExprs(CallExpr expr)
    {
        EnsureRomfsTable();
        string dir = "";
        if (expr.Args.Count == 1)
        {
            dir = StaticStringOf(expr.Args[0]) ?? TryGetCompileTimeText(expr.Args[0])
                ?? throw UserError(
                    "os.listdir() needs the directory at compile time -- the romfs table "
                    + "exists only while the program is being built.", expr.Args[0]);
        }
        else if (expr.Args.Count > 1)
            throw UserError("os.listdir() takes at most one argument", expr.Callee);

        dir = dir.Trim('/');
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var name in romfsEmbedPaths.Keys)
        {
            string rest = name;
            if (dir.Length > 0)
            {
                if (!name.StartsWith(dir + "/", StringComparison.Ordinal)) continue;
                rest = name[(dir.Length + 1)..];
            }
            int slash = rest.IndexOf('/');
            names.Add(slash >= 0 ? rest[..slash] : rest);
        }
        return names.Select(n => (Expression)new StringLiteral(n)).ToList();
    }

    /// `os.listdir(dir)` in expression position -- same marker trick as os.stat.
    private Val EmitOsListdir(CallExpr expr)
    {
        var exprs = OsListdirExprs(expr);
        string lsName = "__romls" + (++romfsId);
        constSequenceBindings[lsName] = exprs;
        return new Variable(lsName, DataType.UINT8);
    }
}

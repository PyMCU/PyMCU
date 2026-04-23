// SPDX-License-Identifier: MIT
// PyMCU ARM LLVM IR Backend — Cortex-M0+ (RP2040) / Cortex-M33 (RP2350).
//
// Translates PyMCU Tacky IR to LLVM IR text format (.ll).
// clang (ARM LLVM Embedded Toolchain) then compiles the .ll file to ELF.
//
// Strategy: alloca-based SSA.
//   Every PyMCU variable/temporary is allocated with alloca in the function
//   entry block.  Every use emits a load; every definition emits a store.
//   LLVM's mem2reg pass (run automatically by clang -O1+) promotes these to
//   true SSA registers, so the final binary is fully optimised.

using System.Text;
using PyMCU.Common.Models;
using PyMCU.IR;

namespace PyMCU.Backend.Targets.ARM;

public class ArmLlvmCodeGen(DeviceConfig cfg) : CodeGen
{
    // ── Output buffer ──────────────────────────────────────────────────────
    private readonly StringBuilder _sb = new();

    // ── Per-function state ─────────────────────────────────────────────────
    // alloca slot name for each local variable/temporary: "name" → "%_s_name"
    private readonly Dictionary<string, string> _slots = new();
    // LLVM type for each slot: "name" → "i16"
    private readonly Dictionary<string, string> _types = new();
    // Names declared as globals in ProgramIR.Globals
    private readonly HashSet<string> _globals = new();
    // Flash-data arrays declared inside function bodies (name → element count)
    private readonly Dictionary<string, int> _flashArrays = new();

    private int _ctr;            // counter for fresh SSA names / labels
    private string _funcName = "";
    private bool _terminated;   // true when the current basic block has a terminator

    // ── Helpers ────────────────────────────────────────────────────────────

    private string NT() => $"%_t{_ctr++}";        // fresh SSA value name
    private string NL() => $"_bb{_ctr++}";        // fresh basic-block label

    /// LLVM target triple for the configured chip.
    private string TargetTriple => cfg.Arch?.ToLowerInvariant() switch
    {
        "rp2350" or "arm-cm33" or "thumbv8m" or "thumbv8m.main"
            => "thumbv8m.main-none-eabi",
        _ => "thumbv6m-none-eabi"   // default: RP2040 Cortex-M0+
    };

    private static string LlvmType(DataType dt) => dt switch
    {
        DataType.UINT8  => "i8",
        DataType.INT8   => "i8",
        DataType.UINT16 => "i16",
        DataType.INT16  => "i16",
        DataType.UINT32 => "i32",
        DataType.INT32  => "i32",
        DataType.VOID   => "void",
        _               => "i32"
    };

    private static int TypeBits(string t) => t switch
    {
        "i1"  => 1,
        "i8"  => 8,
        "i16" => 16,
        "i32" => 32,
        "i64" => 64,
        _     => 32
    };

    /// Emit a raw line (no indent).
    private void Raw(string s) => _sb.AppendLine(s);

    /// Emit an indented instruction line (only when current block is live).
    private void Ins(string s)
    {
        if (_terminated) return;
        _sb.AppendLine("  " + s);
    }

    // ── Load a Val into a fresh SSA name, return (ssaName, llvmType) ───────

    private (string ssa, string type) Ld(Val v)
    {
        switch (v)
        {
            case Constant c:
                return (c.Value.ToString(), "i32");

            case NoneVal:
                return ("0", "i32");

            case MemoryAddress mem:
            {
                var llT     = LlvmType(mem.Type);
                var ptrTmp  = NT();
                var valTmp  = NT();
                Ins($"{ptrTmp} = inttoptr i32 {mem.Address} to {llT}*");
                Ins($"{valTmp} = load volatile {llT}, {llT}* {ptrTmp}");
                return (valTmp, llT);
            }

            case Variable vr:
            {
                var llT = _types.TryGetValue(vr.Name, out var t) ? t : LlvmType(vr.Type);
                if (_globals.Contains(vr.Name))
                {
                    var tmp = NT();
                    Ins($"{tmp} = load {llT}, {llT}* @{vr.Name}");
                    return (tmp, llT);
                }
                if (!_slots.TryGetValue(vr.Name, out var slot))
                    return ("0", llT);
                var ssa = NT();
                Ins($"{ssa} = load {llT}, {llT}* {slot}");
                return (ssa, llT);
            }

            case Temporary tr:
            {
                var llT = _types.TryGetValue(tr.Name, out var t) ? t : LlvmType(tr.Type);
                if (!_slots.TryGetValue(tr.Name, out var slot))
                    return ("0", llT);
                var ssa = NT();
                Ins($"{ssa} = load {llT}, {llT}* {slot}");
                return (ssa, llT);
            }

            default:
                return ("0", "i32");
        }
    }

    // ── Store an SSA value into a destination Val ──────────────────────────

    private void St(string ssa, string srcType, Val dst)
    {
        switch (dst)
        {
            case NoneVal: break;

            case MemoryAddress mem:
            {
                var llT    = LlvmType(mem.Type);
                var coerced = Coerce(ssa, srcType, llT);
                var ptrTmp  = NT();
                Ins($"{ptrTmp} = inttoptr i32 {mem.Address} to {llT}*");
                Ins($"store volatile {llT} {coerced}, {llT}* {ptrTmp}");
                break;
            }

            case Variable vr:
            {
                var llT = _types.TryGetValue(vr.Name, out var t) ? t : LlvmType(vr.Type);
                var coerced = Coerce(ssa, srcType, llT);
                if (_globals.Contains(vr.Name))
                {
                    Ins($"store {llT} {coerced}, {llT}* @{vr.Name}");
                    return;
                }
                if (_slots.TryGetValue(vr.Name, out var slot))
                    Ins($"store {llT} {coerced}, {llT}* {slot}");
                break;
            }

            case Temporary tr:
            {
                var llT = _types.TryGetValue(tr.Name, out var t) ? t : LlvmType(tr.Type);
                var coerced = Coerce(ssa, srcType, llT);
                if (_slots.TryGetValue(tr.Name, out var slot))
                    Ins($"store {llT} {coerced}, {llT}* {slot}");
                break;
            }
        }
    }

    /// Emit a trunc/zext/sext to coerce ssa from srcType to dstType.
    /// Returns the (possibly new) SSA name to use.
    private string Coerce(string ssa, string srcType, string dstType)
    {
        if (srcType == dstType) return ssa;
        // Literal integers don't need a cast instruction.
        if (int.TryParse(ssa, out _)) return ssa;

        var tmp      = NT();
        int srcBits  = TypeBits(srcType);
        int dstBits  = TypeBits(dstType);

        if (srcBits > dstBits)
            Ins($"{tmp} = trunc {srcType} {ssa} to {dstType}");
        else
            Ins($"{tmp} = zext {srcType} {ssa} to {dstType}");

        return tmp;
    }

    // ── Public entry point ─────────────────────────────────────────────────

    public override void Compile(ProgramIR program, TextWriter output)
    {
        _sb.Clear();
        _ctr = 0;
        _globals.Clear();
        _flashArrays.Clear();

        // ── Module header ──────────────────────────────────────────────────
        Raw($"; Generated by pymcuc-arm for {(string.IsNullOrEmpty(cfg.TargetChip) ? "ARM" : cfg.TargetChip)}");
        Raw($"target triple = \"{TargetTriple}\"");
        Raw("target datalayout = \"e-m:e-p:32:32-Fi8-i64:64-v128:64:128-a:0:32-n32-S64\"");
        Raw("");

        // ── Register global names ──────────────────────────────────────────
        foreach (var g in program.Globals)
            _globals.Add(g.Name);

        // ── Collect FlashData from all function bodies ─────────────────────
        foreach (var fn in program.Functions)
        {
            foreach (var instr in fn.Body)
            {
                if (instr is FlashData fd)
                    _flashArrays[fd.Name] = fd.Bytes.Count;
            }
        }

        // ── Emit scalar globals ────────────────────────────────────────────
        foreach (var g in program.Globals)
        {
            if (_flashArrays.ContainsKey(g.Name)) continue; // emitted below
            var llT = LlvmType(g.Type);
            Raw($"@{g.Name} = global {llT} 0");
        }

        // ── Emit flash (read-only) arrays ──────────────────────────────────
        foreach (var fn in program.Functions)
        {
            foreach (var instr in fn.Body)
            {
                if (instr is FlashData fd)
                {
                    var byteList = string.Join(", ", fd.Bytes.Select(b => $"i8 {b}"));
                    Raw($"@{fd.Name} = constant [{fd.Bytes.Count} x i8] [{byteList}]");
                }
            }
        }

        if (program.Globals.Any() || _flashArrays.Any()) Raw("");

        // ── External symbol declarations ───────────────────────────────────
        foreach (var sym in program.ExternSymbols)
            Raw($"declare i32 @{sym}(...)");
        if (program.ExternSymbols.Any()) Raw("");

        // ── Interrupt handler attribute set (emitted if any ISR exists) ───
        bool hasIsr = program.Functions.Any(f => f.IsInterrupt);

        // ── Function bodies ────────────────────────────────────────────────
        foreach (var fn in program.Functions)
        {
            if (!fn.IsInline)
                CompileFunction(fn);
        }

        // ── Attribute group for interrupt handlers ─────────────────────────
        if (hasIsr)
        {
            Raw("");
            Raw("attributes #0 = { \"interrupt\" }");
        }

        output.Write(_sb.ToString());
    }

    // ── Function compilation ───────────────────────────────────────────────

    private void CompileFunction(Function fn)
    {
        _funcName  = fn.Name;
        _slots.Clear();
        _types.Clear();
        _terminated = false;

        // First pass: collect all local variable/temporary names + types.
        CollectSlots(fn);

        // ── Function signature ─────────────────────────────────────────────
        var retType   = fn.IsInterrupt ? "void" : LlvmType(fn.ReturnType);
        var paramList = string.Join(", ", fn.Params.Select(p => $"i32 %{p}"));
        var attrs     = fn.IsInterrupt ? " #0" : "";
        Raw($"define {retType} @{fn.Name}({paramList}){attrs} {{");
        Raw("entry:");

        // Allocate stack slots for all locals in entry block.
        foreach (var (name, llT) in _types)
        {
            var slot = $"%_s_{name}";
            _slots[name] = slot;
            _sb.AppendLine($"  {slot} = alloca {llT}");
        }

        // Copy incoming parameters into their alloca slots.
        foreach (var p in fn.Params)
        {
            if (_slots.TryGetValue(p, out var pslot))
            {
                var ptype = _types.TryGetValue(p, out var pt) ? pt : "i32";
                _sb.AppendLine($"  store {ptype} %{p}, {ptype}* {pslot}");
            }
        }

        // Branch into the real function body.
        _sb.AppendLine("  br label %_body");
        Raw("_body:");
        _terminated = false;

        // ── Emit instructions ──────────────────────────────────────────────
        foreach (var instr in fn.Body)
            CompileInstruction(instr);

        // Safety terminator: if the last block is still open, close it.
        if (!_terminated)
        {
            if (_funcName == "main")
            {
                EmitEndlessLoop();
            }
            else if (fn.IsInterrupt || fn.ReturnType == DataType.VOID)
            {
                _sb.AppendLine("  ret void");
            }
            else
            {
                _sb.AppendLine($"  ret {LlvmType(fn.ReturnType)} 0");
            }
        }

        Raw("}");
        Raw("");
    }

    /// Collect all local variable/temporary names from the function body.
    private void CollectSlots(Function fn)
    {
        void Track(Val v)
        {
            string name;
            DataType dt;
            switch (v)
            {
                case Variable vr: name = vr.Name; dt = vr.Type; break;
                case Temporary tr: name = tr.Name; dt = tr.Type; break;
                default: return;
            }
            // Skip names that are module-level globals.
            if (_globals.Contains(name)) return;
            if (!_types.ContainsKey(name))
                _types[name] = LlvmType(dt);
        }

        foreach (var p in fn.Params)
            if (!_types.ContainsKey(p))
                _types[p] = "i32";

        foreach (var instr in fn.Body)
        {
            switch (instr)
            {
                case Copy c: Track(c.Src); Track(c.Dst); break;
                case Unary u: Track(u.Src); Track(u.Dst); break;
                case Binary b: Track(b.Src1); Track(b.Src2); Track(b.Dst); break;
                case Call ca:
                    foreach (var a in ca.Args) Track(a);
                    Track(ca.Dst);
                    break;
                case Return r: Track(r.Value); break;
                case JumpIfZero jz: Track(jz.Condition); break;
                case JumpIfNotZero jnz: Track(jnz.Condition); break;
                case JumpIfEqual je: Track(je.Src1); Track(je.Src2); break;
                case JumpIfNotEqual jne: Track(jne.Src1); Track(jne.Src2); break;
                case JumpIfLessThan jlt: Track(jlt.Src1); Track(jlt.Src2); break;
                case JumpIfLessOrEqual jle: Track(jle.Src1); Track(jle.Src2); break;
                case JumpIfGreaterThan jgt: Track(jgt.Src1); Track(jgt.Src2); break;
                case JumpIfGreaterOrEqual jge: Track(jge.Src1); Track(jge.Src2); break;
                case BitSet bs: Track(bs.Target); break;
                case BitClear bc: Track(bc.Target); break;
                case BitCheck bck: Track(bck.Source); Track(bck.Dst); break;
                case BitWrite bw: Track(bw.Target); Track(bw.Src); break;
                case JumpIfBitSet jbs: Track(jbs.Source); break;
                case JumpIfBitClear jbc: Track(jbc.Source); break;
                case AugAssign aug: Track(aug.Target); Track(aug.Operand); break;
                case ArrayLoad al: Track(al.Index); Track(al.Dst); break;
                case ArrayLoadFlash alf: Track(alf.Index); Track(alf.Dst); break;
                case ArrayStore as2: Track(as2.Index); Track(as2.Src); break;
            }
        }
    }

    // ── Instruction dispatch ───────────────────────────────────────────────

    private void CompileInstruction(Instruction instr)
    {
        switch (instr)
        {
            case Copy c:                   CompileCopy(c);                    break;
            case Unary u:                  CompileUnary(u);                   break;
            case Binary b:                 CompileBinary(b);                  break;
            case Return r:                 CompileReturn(r);                  break;
            case Jump j:                   CompileJump(j);                    break;
            case JumpIfZero jz:            CompileJumpIfZero(jz);             break;
            case JumpIfNotZero jnz:        CompileJumpIfNotZero(jnz);         break;
            case JumpIfEqual je:           CompileCmpJump(je.Src1, je.Src2, "eq",  false, je.Target);  break;
            case JumpIfNotEqual jne:       CompileCmpJump(jne.Src1, jne.Src2, "ne", false, jne.Target); break;
            case JumpIfLessThan jlt:       CompileCmpJump(jlt.Src1, jlt.Src2, "slt", false, jlt.Target); break;
            case JumpIfLessOrEqual jle:    CompileCmpJump(jle.Src1, jle.Src2, "sle", false, jle.Target); break;
            case JumpIfGreaterThan jgt:    CompileCmpJump(jgt.Src1, jgt.Src2, "sgt", false, jgt.Target); break;
            case JumpIfGreaterOrEqual jge: CompileCmpJump(jge.Src1, jge.Src2, "sge", false, jge.Target); break;
            case Label lbl:                CompileLabel(lbl);                 break;
            case Call ca:                  CompileCall(ca);                   break;
            case BitSet bs:                CompileBitSet(bs);                 break;
            case BitClear bc:              CompileBitClear(bc);               break;
            case BitCheck bck:             CompileBitCheck(bck);              break;
            case BitWrite bw:              CompileBitWrite(bw);               break;
            case JumpIfBitSet jbs:         CompileJumpIfBitSet(jbs);          break;
            case JumpIfBitClear jbc:       CompileJumpIfBitClear(jbc);        break;
            case AugAssign aug:            CompileAugAssign(aug);             break;
            case LoadIndirect li:          CompileLoadIndirect(li);           break;
            case StoreIndirect si:         CompileStoreIndirect(si);          break;
            case ArrayLoad al:             CompileArrayLoad(al);              break;
            case ArrayLoadFlash alf:       CompileArrayLoadFlash(alf);        break;
            case ArrayStore as2:           CompileArrayStore(as2);            break;
            case FlashData:                break; // emitted at module level in Compile()
            case InlineAsm ia:             Ins($"; inline asm (not lowered): {ia.Code}"); break;
            case DebugLine dbg:
                var src = string.IsNullOrEmpty(dbg.SourceFile) ? "" : $"{dbg.SourceFile}:";
                Ins($"; {src}{dbg.Line}: {dbg.Text}");
                break;
        }
    }

    // ── Individual instruction compilers ──────────────────────────────────

    private void CompileCopy(Copy c)
    {
        var (ssa, t) = Ld(c.Src);
        St(ssa, t, c.Dst);
    }

    private void CompileUnary(Unary u)
    {
        var (src, t) = Ld(u.Src);
        var res      = NT();
        switch (u.Op)
        {
            case UnaryOp.Neg:    Ins($"{res} = sub {t} 0, {src}");            break;
            case UnaryOp.Not:    Ins($"{res} = icmp eq {t} {src}, 0");
                                 var ext = NT();
                                 Ins($"{ext} = zext i1 {res} to {t}");
                                 res = ext;                                    break;
            case UnaryOp.BitNot: Ins($"{res} = xor {t} {src}, -1");           break;
        }
        St(res, t, u.Dst);
    }

    private void CompileBinary(Binary b)
    {
        var (s1, t1) = Ld(b.Src1);
        var (s2, t2) = Ld(b.Src2);

        // Use the wider of the two types.
        var t  = TypeBits(t1) >= TypeBits(t2) ? t1 : t2;
        s1 = Coerce(s1, t1, t);
        s2 = Coerce(s2, t2, t);

        var res = NT();
        switch (b.Op)
        {
            case BinaryOp.Add:          Ins($"{res} = add {t} {s1}, {s2}");                break;
            case BinaryOp.Sub:          Ins($"{res} = sub {t} {s1}, {s2}");                break;
            case BinaryOp.Mul:          Ins($"{res} = mul {t} {s1}, {s2}");                break;
            case BinaryOp.Div:          Ins($"{res} = sdiv {t} {s1}, {s2}");               break;
            case BinaryOp.FloorDiv:     Ins($"{res} = sdiv {t} {s1}, {s2}");               break;
            case BinaryOp.Mod:          Ins($"{res} = srem {t} {s1}, {s2}");               break;
            case BinaryOp.BitAnd:       Ins($"{res} = and {t} {s1}, {s2}");                break;
            case BinaryOp.BitOr:        Ins($"{res} = or {t} {s1}, {s2}");                 break;
            case BinaryOp.BitXor:       Ins($"{res} = xor {t} {s1}, {s2}");                break;
            case BinaryOp.LShift:       Ins($"{res} = shl {t} {s1}, {s2}");                break;
            case BinaryOp.RShift:       Ins($"{res} = lshr {t} {s1}, {s2}");               break;
            case BinaryOp.Equal:        Ins($"{res} = icmp eq {t} {s1}, {s2}");
                                        res = ZextToType(res, "i1", t);                     break;
            case BinaryOp.NotEqual:     Ins($"{res} = icmp ne {t} {s1}, {s2}");
                                        res = ZextToType(res, "i1", t);                     break;
            case BinaryOp.LessThan:     Ins($"{res} = icmp slt {t} {s1}, {s2}");
                                        res = ZextToType(res, "i1", t);                     break;
            case BinaryOp.LessEqual:    Ins($"{res} = icmp sle {t} {s1}, {s2}");
                                        res = ZextToType(res, "i1", t);                     break;
            case BinaryOp.GreaterThan:  Ins($"{res} = icmp sgt {t} {s1}, {s2}");
                                        res = ZextToType(res, "i1", t);                     break;
            case BinaryOp.GreaterEqual: Ins($"{res} = icmp sge {t} {s1}, {s2}");
                                        res = ZextToType(res, "i1", t);                     break;
        }
        St(res, t, b.Dst);
    }

    /// Emit zext i1 → target type and return the new SSA name.
    private string ZextToType(string ssa, string from, string to)
    {
        if (from == to) return ssa;
        var tmp = NT();
        Ins($"{tmp} = zext {from} {ssa} to {to}");
        return tmp;
    }

    private void CompileReturn(Return r)
    {
        if (_funcName == "main")
        {
            // main must not return in bare-metal firmware; spin forever.
            EmitEndlessLoop();
            return;
        }

        if (r.Value is NoneVal || _funcName == "" )
        {
            Ins("ret void");
        }
        else
        {
            var retType = _types.TryGetValue(_funcName, out _) ? "i32" : "i32";
            var (ssa, t) = Ld(r.Value);
            var coerced  = Coerce(ssa, t, "i32");
            Ins($"ret i32 {coerced}");
        }
        _terminated = true;
    }

    private void EmitEndlessLoop()
    {
        var lbl = NL();
        Ins($"br label %{lbl}");
        _terminated = true;
        Raw($"{lbl}:");
        _terminated = false;
        Ins($"br label %{lbl}");
        _terminated = true;
    }

    private void CompileJump(Jump j)
    {
        Ins($"br label %{j.Target}");
        _terminated = true;
    }

    /// When a label is encountered: close the previous block (if open) with a
    /// fallthrough branch, then start the new block.
    private void CompileLabel(Label lbl)
    {
        if (!_terminated)
            _sb.AppendLine($"  br label %{lbl.Name}");
        Raw($"{lbl.Name}:");
        _terminated = false;
    }

    private void CompileJumpIfZero(JumpIfZero jz)
    {
        var (cond, t) = Ld(jz.Condition);
        var cmp       = NT();
        var fallThru  = NL();
        Ins($"{cmp} = icmp eq {t} {cond}, 0");
        Ins($"br i1 {cmp}, label %{jz.Target}, label %{fallThru}");
        _terminated = true;
        Raw($"{fallThru}:");
        _terminated = false;
    }

    private void CompileJumpIfNotZero(JumpIfNotZero jnz)
    {
        var (cond, t) = Ld(jnz.Condition);
        var cmp       = NT();
        var fallThru  = NL();
        Ins($"{cmp} = icmp ne {t} {cond}, 0");
        Ins($"br i1 {cmp}, label %{jnz.Target}, label %{fallThru}");
        _terminated = true;
        Raw($"{fallThru}:");
        _terminated = false;
    }

    /// Generic relational conditional branch.
    private void CompileCmpJump(Val src1, Val src2, string pred, bool unsigned, string target)
    {
        var (s1, t1) = Ld(src1);
        var (s2, t2) = Ld(src2);
        var t        = TypeBits(t1) >= TypeBits(t2) ? t1 : t2;
        s1 = Coerce(s1, t1, t);
        s2 = Coerce(s2, t2, t);

        var cmp      = NT();
        var fallThru = NL();
        Ins($"{cmp} = icmp {pred} {t} {s1}, {s2}");
        Ins($"br i1 {cmp}, label %{target}, label %{fallThru}");
        _terminated = true;
        Raw($"{fallThru}:");
        _terminated = false;
    }

    private void CompileCall(Call ca)
    {
        var argParts = new List<string>();
        foreach (var arg in ca.Args)
        {
            var (ssa, t) = Ld(arg);
            var coerced  = Coerce(ssa, t, "i32");
            argParts.Add($"i32 {coerced}");
        }
        var argStr = string.Join(", ", argParts);

        if (ca.Dst is NoneVal)
        {
            Ins($"call void @{ca.FunctionName}({argStr})");
        }
        else
        {
            var res = NT();
            Ins($"{res} = call i32 @{ca.FunctionName}({argStr})");
            St(res, "i32", ca.Dst);
        }
    }

    // ── Bit operations ─────────────────────────────────────────────────────

    private void CompileBitSet(BitSet bs)
    {
        var (val, t) = Ld(bs.Target);
        var mask     = 1 << bs.Bit;
        var res      = NT();
        Ins($"{res} = or {t} {val}, {mask}");
        St(res, t, bs.Target);
    }

    private void CompileBitClear(BitClear bc)
    {
        var (val, t) = Ld(bc.Target);
        var mask     = ~(1 << bc.Bit);
        var res      = NT();
        Ins($"{res} = and {t} {val}, {mask}");
        St(res, t, bc.Target);
    }

    private void CompileBitCheck(BitCheck bck)
    {
        var (val, t) = Ld(bck.Source);
        var shifted  = NT();
        var masked   = NT();
        var extended = NT();
        Ins($"{shifted}  = lshr {t} {val}, {bck.Bit}");
        Ins($"{masked}   = and {t} {shifted}, 1");
        Ins($"{extended} = zext {t} {masked} to i32");
        St(extended, "i32", bck.Dst);
    }

    private void CompileBitWrite(BitWrite bw)
    {
        var (target, tT) = Ld(bw.Target);
        var (src, sT)    = Ld(bw.Src);
        var srcCoerced   = Coerce(src, sT, tT);

        // Normalise to bit 0: bit = src & 1
        var bit1     = NT();
        Ins($"{bit1} = and {tT} {srcCoerced}, 1");

        // Shift to position
        var shifted  = NT();
        Ins($"{shifted} = shl {tT} {bit1}, {bw.Bit}");

        // Clear old bit
        var clearMask = ~(1 << bw.Bit);
        var cleared   = NT();
        Ins($"{cleared} = and {tT} {target}, {clearMask}");

        // OR in new bit
        var res = NT();
        Ins($"{res} = or {tT} {cleared}, {shifted}");
        St(res, tT, bw.Target);
    }

    private void CompileJumpIfBitSet(JumpIfBitSet jbs)
    {
        var (val, t) = Ld(jbs.Source);
        var shifted  = NT();
        var masked   = NT();
        var cmp      = NT();
        var fallThru = NL();
        Ins($"{shifted}  = lshr {t} {val}, {jbs.Bit}");
        Ins($"{masked}   = and {t} {shifted}, 1");
        Ins($"{cmp}      = icmp ne {t} {masked}, 0");
        Ins($"br i1 {cmp}, label %{jbs.Target}, label %{fallThru}");
        _terminated = true;
        Raw($"{fallThru}:");
        _terminated = false;
    }

    private void CompileJumpIfBitClear(JumpIfBitClear jbc)
    {
        var (val, t) = Ld(jbc.Source);
        var shifted  = NT();
        var masked   = NT();
        var cmp      = NT();
        var fallThru = NL();
        Ins($"{shifted}  = lshr {t} {val}, {jbc.Bit}");
        Ins($"{masked}   = and {t} {shifted}, 1");
        Ins($"{cmp}      = icmp eq {t} {masked}, 0");
        Ins($"br i1 {cmp}, label %{jbc.Target}, label %{fallThru}");
        _terminated = true;
        Raw($"{fallThru}:");
        _terminated = false;
    }

    // ── AugAssign ──────────────────────────────────────────────────────────

    private void CompileAugAssign(AugAssign aug)
    {
        var (target, tT) = Ld(aug.Target);
        var (operand, oT) = Ld(aug.Operand);
        var t    = TypeBits(tT) >= TypeBits(oT) ? tT : oT;
        target   = Coerce(target, tT, t);
        operand  = Coerce(operand, oT, t);
        var res  = NT();

        switch (aug.Op)
        {
            case BinaryOp.Add:    Ins($"{res} = add {t} {target}, {operand}");    break;
            case BinaryOp.Sub:    Ins($"{res} = sub {t} {target}, {operand}");    break;
            case BinaryOp.Mul:    Ins($"{res} = mul {t} {target}, {operand}");    break;
            case BinaryOp.BitOr:  Ins($"{res} = or {t} {target}, {operand}");     break;
            case BinaryOp.BitAnd: Ins($"{res} = and {t} {target}, {operand}");    break;
            case BinaryOp.BitXor: Ins($"{res} = xor {t} {target}, {operand}");    break;
            case BinaryOp.LShift: Ins($"{res} = shl {t} {target}, {operand}");    break;
            case BinaryOp.RShift: Ins($"{res} = lshr {t} {target}, {operand}");   break;
            default:              Ins($"; TODO AugAssign op {aug.Op}"); res = target; break;
        }
        St(res, t, aug.Target);
    }

    // ── Indirect memory access ─────────────────────────────────────────────

    private void CompileLoadIndirect(LoadIndirect li)
    {
        var (ptr, _) = Ld(li.SrcPtr);
        var dstType  = li.Dst is Variable vr ? LlvmType(vr.Type)
                       : li.Dst is Temporary tr ? LlvmType(tr.Type)
                       : "i32";
        var ptrCast  = NT();
        var loaded   = NT();
        Ins($"{ptrCast} = inttoptr i32 {ptr} to {dstType}*");
        Ins($"{loaded}  = load {dstType}, {dstType}* {ptrCast}");
        St(loaded, dstType, li.Dst);
    }

    private void CompileStoreIndirect(StoreIndirect si)
    {
        var (src, sT) = Ld(si.Src);
        var (ptr, _)  = Ld(si.DstPtr);
        var ptrCast   = NT();
        Ins($"{ptrCast} = inttoptr i32 {ptr} to {sT}*");
        Ins($"store {sT} {src}, {sT}* {ptrCast}");
    }

    // ── Array operations ───────────────────────────────────────────────────

    private void CompileArrayLoad(ArrayLoad al)
    {
        var (idx, _) = Ld(al.Index);
        var idxCoerced = Coerce(idx, "i32", "i32");
        var llT      = LlvmType(al.ElemType);
        var gep      = NT();
        var loaded   = NT();
        Ins($"{gep}    = getelementptr inbounds {llT}, {llT}* @{al.ArrayName}, i32 {idxCoerced}");
        Ins($"{loaded} = load {llT}, {llT}* {gep}");
        St(loaded, llT, al.Dst);
    }

    private void CompileArrayLoadFlash(ArrayLoadFlash alf)
    {
        var (idx, _)   = Ld(alf.Index);
        var idxCoerced = Coerce(idx, "i32", "i32");
        var gep        = NT();
        var loaded     = NT();
        Ins($"{gep}    = getelementptr inbounds i8, i8* @{alf.ArrayName}, i32 {idxCoerced}");
        Ins($"{loaded} = load i8, i8* {gep}");
        St(loaded, "i8", alf.Dst);
    }

    private void CompileArrayStore(ArrayStore as2)
    {
        var (idx, _) = Ld(as2.Index);
        var idxCoerced = Coerce(idx, "i32", "i32");
        var (src, sT) = Ld(as2.Src);
        var llT       = LlvmType(as2.ElemType);
        var srcCoerced = Coerce(src, sT, llT);
        var gep       = NT();
        Ins($"{gep} = getelementptr inbounds {llT}, {llT}* @{as2.ArrayName}, i32 {idxCoerced}");
        Ins($"store {llT} {srcCoerced}, {llT}* {gep}");
    }

    // ── ISR / context-save stubs ───────────────────────────────────────────
    // On ARM Cortex-M the hardware saves/restores the exception frame
    // automatically; context save/restore are no-ops at the IR level.

    public override void EmitContextSave()    { }
    public override void EmitContextRestore() { }
    public override void EmitInterruptReturn()
    {
        // ARM exception return is via EXC_RETURN value in LR — handled by
        // clang when it sees the "interrupt" function attribute.
        Ins("ret void");
        _terminated = true;
    }
}

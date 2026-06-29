/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
 * -----------------------------------------------------------------------------
 *
 * Two-pass assembler that turns an @asm_pio / @rp2.asm_pio function body (a PIO
 * DSL whose syntax matches MicroPython's rp2.asm_pio) into 16-bit PIO machine
 * words plus a PioConfig. Pass 1 collects labels and wrap markers; pass 2
 * encodes each instruction. Operates directly on the parsed AST.
 */

namespace PyMCU.Frontend.Pio;

public static class PioAssembler
{
    public static AssembledPioProgram Assemble(FunctionDef fn)
    {
        var config = BuildConfig(fn.PioParams);
        var stmts = fn.Body.Statements;

        // ── Pass 1: labels, wrap markers, instruction count ──────────────────
        var symbols = new Dictionary<string, int>();
        int pc = 0, wrapTarget = -1, wrap = -1;
        foreach (var s in stmts)
        {
            var expr = ExprOf(s);
            if (expr is null) continue;            // ignore docstrings / pass
            var (mnemonic, args) = BaseCall(StripDelaySide(expr, out _, out _));
            switch (mnemonic)
            {
                case "label":
                    if (args.Count != 1 || args[0] is not StringLiteral lbl)
                        throw new PioAsmException("label() takes a single string name");
                    if (!symbols.TryAdd(lbl.Value, pc))
                        throw new PioAsmException($"duplicate label '{lbl.Value}'");
                    break;
                case "wrap_target":
                    wrapTarget = pc;
                    break;
                case "wrap":
                    wrap = pc - 1;
                    break;
                default:
                    pc++;
                    break;
            }
        }
        if (pc == 0) throw new PioAsmException($"@asm_pio program '{fn.Name}' is empty");
        if (pc > 32) throw new PioAsmException(
            $"@asm_pio program '{fn.Name}' has {pc} instructions (max 32)");
        if (wrapTarget < 0) wrapTarget = 0;
        if (wrap < 0) wrap = pc - 1;

        // ── Pass 2: encode ───────────────────────────────────────────────────
        var words = new List<ushort>(pc);
        foreach (var s in stmts)
        {
            var expr = ExprOf(s);
            if (expr is null) continue;
            var (mnemonic, _) = BaseCall(StripDelaySide(expr, out _, out _));
            if (mnemonic is "label" or "wrap_target" or "wrap") continue;
            words.Add(Encode(expr, config, symbols));
        }

        return new AssembledPioProgram
        {
            Words = words.ToArray(),
            Config = config,
            WrapTarget = wrapTarget,
            Wrap = wrap,
        };
    }

    // ── Statement / expression helpers ───────────────────────────────────────

    private static Expression? ExprOf(Statement s) => s switch
    {
        ExprStmt es => es.Expr,
        _ => null,   // PassStmt, comments, etc. are ignored inside a PIO body
    };

    // Strip a trailing [delay] subscript and a .side(v) call (in either order),
    // returning the core instruction call and the extracted delay / side value.
    private static Expression StripDelaySide(Expression expr, out int delay, out int? side)
    {
        delay = 0;
        side = null;
        while (true)
        {
            if (expr is IndexExpr ix)
            {
                delay = EvalInt(ix.Index, "delay");
                expr = ix.Target;
                continue;
            }
            if (expr is CallExpr { Callee: MemberAccessExpr { Member: "side" } m } sc)
            {
                if (sc.Args.Count != 1)
                    throw new PioAsmException(".side() takes exactly one value");
                side = EvalInt(sc.Args[0], "side-set");
                expr = m.Object;
                continue;
            }
            return expr;
        }
    }

    // Returns the mnemonic (callee identifier) and argument list of a base
    // instruction call, after .side()/[delay] have been stripped.
    private static (string Mnemonic, List<Expression> Args) BaseCall(Expression expr)
    {
        if (expr is CallExpr { Callee: VariableExpr v } c)
            return (v.Name, c.Args);
        throw new PioAsmException("expected a PIO instruction call");
    }

    // ── Instruction encoding ─────────────────────────────────────────────────

    private static ushort Encode(Expression full, PioConfig cfg, Dictionary<string, int> symbols)
    {
        var core = StripDelaySide(full, out int delay, out int? side);
        var (mn, args) = BaseCall(core);
        ushort body = mn switch
        {
            "jmp"  => EncodeJmp(args, symbols),
            "wait" => EncodeWait(args),
            "in_"  => (ushort)(PioEnc.IN  | (PioEnc.InSrc(RegName(args, 0))  << 5) | Count32(args, 1)),
            "out"  => (ushort)(PioEnc.OUT | (PioEnc.OutDest(RegName(args, 0)) << 5) | Count32(args, 1)),
            "push" => EncodePushPull(args, pull: false),
            "pull" => EncodePushPull(args, pull: true),
            "mov"  => EncodeMov(args),
            "irq"  => EncodeIrq(args),
            "set"  => (ushort)(PioEnc.SET | (PioEnc.SetDest(RegName(args, 0)) << 5) | Mask5(EvalInt(args[1], "set value"))),
            "nop"  => 0xA042,                       // mov(y, y)
            "word" => (ushort)EvalInt(args[0], "word"),
            _ => throw new PioAsmException($"unknown PIO instruction '{mn}'"),
        };
        return (ushort)(body | PackDelaySide(delay, side, cfg));
    }

    private static ushort EncodeJmp(List<Expression> args, Dictionary<string, int> symbols)
    {
        if (args.Count is < 1 or > 2)
            throw new PioAsmException("jmp() takes (label) or (condition, label)");
        int cond = 0;
        Expression labelExpr = args[^1];
        if (args.Count == 2)
            cond = PioEnc.JmpCond(((VariableExpr)args[0]).Name);
        int addr = ResolveLabel(labelExpr, symbols);
        return (ushort)(PioEnc.JMP | (cond << 5) | (addr & 0x1F));
    }

    private static ushort EncodeWait(List<Expression> args)
    {
        if (args.Count != 3)
            throw new PioAsmException("wait() takes (polarity, source, index)");
        int pol = EvalInt(args[0], "wait polarity") & 1;
        int src = PioEnc.WaitSrc(RegName(args, 1));
        int idx = EvalInt(args[2], "wait index") & 0x1F;
        return (ushort)(PioEnc.WAIT | (pol << 7) | (src << 5) | idx);
    }

    private static ushort EncodePushPull(List<Expression> args, bool pull)
    {
        // Defaults: block = 1; iffull/ifempty = 0.
        int block = 1, cond = 0;
        foreach (var a in args)
        {
            string n = ((VariableExpr)a).Name;
            switch (n)
            {
                case "block":   block = 1; break;
                case "noblock": block = 0; break;
                case "iffull" when !pull:  cond = 1; break;
                case "ifempty" when pull:  cond = 1; break;
                default: throw new PioAsmException($"invalid {(pull ? "pull" : "push")} modifier '{n}'");
            }
        }
        ushort baseOp = pull ? PioEnc.PULL : PioEnc.PUSH;
        return (ushort)(baseOp | (cond << 6) | (block << 5));
    }

    private static ushort EncodeMov(List<Expression> args)
    {
        if (args.Count != 2)
            throw new PioAsmException("mov() takes (destination, source)");
        int dest = PioEnc.MovDest(RegName(args, 0));
        int op = PioEnc.MovOpNone;
        Expression srcExpr = args[1];
        if (srcExpr is CallExpr { Callee: VariableExpr mod } mc)
        {
            op = mod.Name switch
            {
                "invert"  => PioEnc.MovOpInvert,
                "reverse" => PioEnc.MovOpReverse,
                _ => throw new PioAsmException($"invalid mov modifier '{mod.Name}'"),
            };
            if (mc.Args.Count != 1) throw new PioAsmException($"{mod.Name}() takes one register");
            srcExpr = mc.Args[0];
        }
        int src = PioEnc.MovSrc(((VariableExpr)srcExpr).Name);
        return (ushort)(PioEnc.MOV | (dest << 5) | (op << 3) | src);
    }

    private static ushort EncodeIrq(List<Expression> args)
    {
        // irq(index) | irq(mod, index) | irq(rel(n)) with mods block/clear.
        int wait = 0, clear = 0, index = -1, rel = 0;
        foreach (var a in args)
        {
            switch (a)
            {
                case VariableExpr { Name: "block" }: wait = 1; break;
                case VariableExpr { Name: "clear" }: clear = 1; break;
                case CallExpr { Callee: VariableExpr { Name: "rel" } } rc:
                    index = EvalInt(rc.Args[0], "irq index"); rel = 0x10; break;
                case IntegerLiteral il: index = il.Value; break;
                default: throw new PioAsmException("invalid irq() argument");
            }
        }
        if (index < 0) throw new PioAsmException("irq() requires an index");
        return (ushort)(PioEnc.IRQ | (clear << 6) | (wait << 5) | ((index & 0x07) | rel));
    }

    // ── Side-set / delay packing (bits [12:8]) ───────────────────────────────

    private static ushort PackDelaySide(int delay, int? side, PioConfig cfg)
    {
        int sideTotalBits = cfg.SideSetCount + (cfg.SideSetOpt ? 1 : 0);
        int delayBits = 5 - sideTotalBits;
        if (delayBits < 0)
            throw new PioAsmException("side-set uses more than 5 bits");
        if (delay < 0 || delay >= (1 << delayBits))
            throw new PioAsmException($"delay {delay} too large for {delayBits} delay bits");

        int field = delay;
        if (cfg.SideSetCount > 0)
        {
            int sv = side ?? 0;
            if (sv < 0 || sv >= (1 << cfg.SideSetCount))
                throw new PioAsmException($"side-set value {sv} overflows {cfg.SideSetCount} bits");
            int sideField = sv;
            if (cfg.SideSetOpt && side.HasValue)
                sideField |= 1 << cfg.SideSetCount;       // optional enable bit
            field |= sideField << delayBits;
        }
        else if (side.HasValue)
        {
            throw new PioAsmException(".side() used but @asm_pio has no sideset_init");
        }
        return (ushort)(field << 8);
    }

    // ── Operand evaluation ───────────────────────────────────────────────────

    private static string RegName(List<Expression> args, int i)
    {
        if (i >= args.Count) throw new PioAsmException("missing register operand");
        if (args[i] is VariableExpr v) return v.Name;
        throw new PioAsmException("expected a register name (e.g. pins, x, y)");
    }

    // OUT/IN bit count: 1..32, with 32 encoded as 0.
    private static int Count32(List<Expression> args, int i)
    {
        int n = EvalInt(args[i], "bit count");
        if (n < 1 || n > 32) throw new PioAsmException($"bit count {n} out of range 1..32");
        return n & 0x1F;
    }

    private static int Mask5(int v)
    {
        if (v < 0 || v > 31) throw new PioAsmException($"value {v} out of range 0..31");
        return v & 0x1F;
    }

    private static int ResolveLabel(Expression e, Dictionary<string, int> symbols)
    {
        string name = e switch
        {
            StringLiteral s => s.Value,
            VariableExpr v  => v.Name,
            _ => throw new PioAsmException("jmp target must be a label name"),
        };
        if (symbols.TryGetValue(name, out int addr)) return addr;
        throw new PioAsmException($"undefined label '{name}'");
    }

    private static int EvalInt(Expression e, string what) => e switch
    {
        IntegerLiteral i => i.Value,
        BooleanLiteral b => b.Value ? 1 : 0,
        UnaryExpr { Op: UnaryOp.Negate, Operand: IntegerLiteral i } => -i.Value,
        _ => throw new PioAsmException($"{what} must be a compile-time integer constant"),
    };

    // ── Decorator config (@asm_pio(...) keyword arguments) ───────────────────

    private static PioConfig BuildConfig(Dictionary<string, Expression> p)
    {
        var c = new PioConfig();
        foreach (var (key, val) in p)
        {
            switch (key)
            {
                case "sideset_init":
                    c.SideSetCount = c.SideSetInitCount = SeqLen(val); break;
                case "out_init":
                    c.OutInitCount = SeqLen(val); break;
                case "set_init":
                    c.SetInitCount = SeqLen(val); break;
                case "sideset_opt":
                    c.SideSetOpt = AsBool(val); break;
                case "side_pindir":
                    c.SideSetPinDir = AsBool(val); break;
                case "autopush":
                    c.AutoPush = AsBool(val); break;
                case "autopull":
                    c.AutoPull = AsBool(val); break;
                case "push_thresh":
                    c.PushThreshold = EvalInt(val, "push_thresh"); break;
                case "pull_thresh":
                    c.PullThreshold = EvalInt(val, "pull_thresh"); break;
                case "in_shiftdir":
                    c.InShiftDir = ShiftDir(val); break;
                case "out_shiftdir":
                    c.OutShiftDir = ShiftDir(val); break;
                case "fifo_join":
                    c.FifoJoin = FifoJoin(val); break;
                default:
                    throw new PioAsmException($"unknown @asm_pio argument '{key}'");
            }
        }
        return c;
    }

    // Length of a sequence kwarg: a list/tuple counts its elements; a single
    // value (e.g. sideset_init=PIO.OUT_LOW) counts as 1.
    private static int SeqLen(Expression e) => e switch
    {
        ListExpr l  => l.Elements.Count,
        TupleExpr t => t.Elements.Count,
        _ => 1,
    };

    private static bool AsBool(Expression e) => e switch
    {
        BooleanLiteral b => b.Value,
        IntegerLiteral i => i.Value != 0,
        _ => throw new PioAsmException("expected True/False"),
    };

    private static PioShiftDir ShiftDir(Expression e) =>
        MemberName(e).EndsWith("RIGHT") ? PioShiftDir.Right : PioShiftDir.Left;

    private static PioFifoJoin FifoJoin(Expression e)
    {
        string n = MemberName(e);
        if (n.EndsWith("TX")) return PioFifoJoin.Tx;
        if (n.EndsWith("RX")) return PioFifoJoin.Rx;
        return PioFifoJoin.None;
    }

    private static string MemberName(Expression e) => e switch
    {
        MemberAccessExpr m => m.Member,
        VariableExpr v => v.Name,
        _ => "",
    };
}

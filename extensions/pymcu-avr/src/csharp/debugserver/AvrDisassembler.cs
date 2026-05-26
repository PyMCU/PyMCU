// SPDX-License-Identifier: MIT
namespace PyMCU.AVR.DebugServer;

/// <summary>
/// Minimal AVR disassembler covering the instruction set emitted by the PyMCU compiler.
/// Decodes 16-bit program-memory words (little-endian words as stored in ProgramMemory[]).
/// </summary>
public static class AvrDisassembler
{
    public readonly record struct Instruction(uint Pc, string Mnemonic, int WordSize);

    /// <summary>
    /// Disassembles up to <paramref name="wordCount"/> words from <paramref name="mem"/>.
    /// Skips regions filled with 0xFFFF (unprogrammed flash).
    /// Returns one <see cref="Instruction"/> per logical instruction.
    /// </summary>
    public static List<Instruction> Disassemble(ushort[] mem, int wordCount)
    {
        var result = new List<Instruction>(wordCount / 2);
        uint i = 0;
        while (i < (uint)wordCount)
        {
            uint op = mem[i];
            // Skip long stretches of unprogrammed flash (0xFFFF) — also NOP (0x0000)
            // at padding boundaries.
            if (op == 0xFFFF) { i++; continue; }

            var (mnemonic, size) = Decode(mem, i, wordCount);
            result.Add(new Instruction(i, mnemonic, size));
            i += (uint)size;
        }
        return result;
    }

    // ── Decoder ─────────────────────────────────────────────────────────────

    private static (string mnemonic, int size) Decode(ushort[] mem, uint pc, int wordCount)
    {
        uint op  = (uint)mem[pc];
        uint op2 = (pc + 1 < (uint)wordCount) ? (uint)mem[pc + 1] : 0u;

        // ── NOP ──────────────────────────────────────────────────────────────
        if (op == 0x0000) return ("NOP", 1);

        // ── MOVW Rd+1:Rd, Rs+1:Rs ────────────────────────────────────────────
        if ((op & 0xFF00) == 0x0100)
        {
            int d = (int)((op >> 4) & 0xF) * 2;
            int s = (int)(op & 0xF) * 2;
            return ($"MOVW R{d + 1}:R{d}, R{s + 1}:R{s}", 1);
        }

        // ── 2-operand arithmetic/logic (Rd, Rs) ──────────────────────────────
        if ((op & 0xFC00) == 0x0400) return ($"CPC  R{Rd5(op)}, R{Rs5(op)}", 1);
        if ((op & 0xFC00) == 0x0800) return ($"SBC  R{Rd5(op)}, R{Rs5(op)}", 1);
        if ((op & 0xFC00) == 0x0C00) return ($"ADD  R{Rd5(op)}, R{Rs5(op)}", 1);  // also LSL
        if ((op & 0xFC00) == 0x1000) return ($"CPSE R{Rd5(op)}, R{Rs5(op)}", 1);
        if ((op & 0xFC00) == 0x1400) return ($"CP   R{Rd5(op)}, R{Rs5(op)}", 1);
        if ((op & 0xFC00) == 0x1800) return ($"SUB  R{Rd5(op)}, R{Rs5(op)}", 1);
        if ((op & 0xFC00) == 0x1C00) return ($"ADC  R{Rd5(op)}, R{Rs5(op)}", 1);  // also ROL
        if ((op & 0xFC00) == 0x2000) return ($"AND  R{Rd5(op)}, R{Rs5(op)}", 1);  // also TST
        if ((op & 0xFC00) == 0x2400) return ($"EOR  R{Rd5(op)}, R{Rs5(op)}", 1);  // also CLR
        if ((op & 0xFC00) == 0x2800) return ($"OR   R{Rd5(op)}, R{Rs5(op)}", 1);
        if ((op & 0xFC00) == 0x2C00) return ($"MOV  R{Rd5(op)}, R{Rs5(op)}", 1);

        // ── Immediate (Rd ∈ R16..R31) ────────────────────────────────────────
        if ((op & 0xF000) == 0x3000) return ($"CPI  R{RdH(op)}, 0x{K8(op):X2}", 1);
        if ((op & 0xF000) == 0x4000) return ($"SBCI R{RdH(op)}, 0x{K8(op):X2}", 1);
        if ((op & 0xF000) == 0x5000) return ($"SUBI R{RdH(op)}, 0x{K8(op):X2}", 1);
        if ((op & 0xF000) == 0x6000) return ($"ORI  R{RdH(op)}, 0x{K8(op):X2}", 1);
        if ((op & 0xF000) == 0x7000) return ($"ANDI R{RdH(op)}, 0x{K8(op):X2}", 1);
        if ((op & 0xF000) == 0xE000) return ($"LDI  R{RdH(op)}, 0x{K8(op):X2}", 1);

        // ── IN / OUT ─────────────────────────────────────────────────────────
        if ((op & 0xF800) == 0xB000)
        {
            int a = IoAddr(op);
            int d = (int)((op >> 4) & 0x1F);
            return ($"IN   R{d}, 0x{a:X2}", 1);
        }
        if ((op & 0xF800) == 0xB800)
        {
            int a = IoAddr(op);
            int d = (int)((op >> 4) & 0x1F);
            return ($"OUT  0x{a:X2}, R{d}", 1);
        }

        // ── RJMP / RCALL ─────────────────────────────────────────────────────
        if ((op & 0xF000) == 0xC000)
        {
            int k = (int)(op & 0x0FFF);
            if (k >= 0x800) k -= 0x1000;
            uint target = (uint)((int)(pc + 1) + k);
            return ($"RJMP 0x{target:X4}", 1);
        }
        if ((op & 0xF000) == 0xD000)
        {
            int k = (int)(op & 0x0FFF);
            if (k >= 0x800) k -= 0x1000;
            uint target = (uint)((int)(pc + 1) + k);
            return ($"RCALL 0x{target:X4}", 1);
        }

        // ── Branch instructions (F000-F7FF) ─────────────────────────────────
        if ((op & 0xF800) == 0xF000)   // BRBS: F000-F3FF, bit10=0 → set
        {
            bool set = (op & 0x0400) == 0;
            int  s   = (int)(op & 0x7);
            int  k   = (int)((op >> 3) & 0x7F);
            if (k >= 64) k -= 128;
            uint target = (uint)((int)(pc + 1) + k);
            string name = set ? BrSetName(s) : BrClrName(s);
            return ($"{name} 0x{target:X4}", 1);
        }

        // ── SBRC / SBRS / BLD / BST ──────────────────────────────────────────
        if ((op & 0xFE08) == 0xF800) return ($"BLD  R{Rd5(op)}, {op & 7}", 1);
        if ((op & 0xFE08) == 0xFA00) return ($"BST  R{Rd5(op)}, {op & 7}", 1);
        if ((op & 0xFE08) == 0xFC00) return ($"SBRC R{Rd5(op)}, {op & 7}", 1);
        if ((op & 0xFE08) == 0xFE00) return ($"SBRS R{Rd5(op)}, {op & 7}", 1);

        // ── 0x9x00 block ─────────────────────────────────────────────────────

        // LDS Rd, addr  (0x9000, d in bits 8:4, addr in next word)
        if ((op & 0xFE0F) == 0x9000)
        {
            int d = (int)((op >> 4) & 0x1F);
            return ($"LDS  R{d}, 0x{op2:X4}", 2);
        }
        // STS addr, Rs  (0x9200, s in bits 8:4, addr in next word)
        if ((op & 0xFE0F) == 0x9200)
        {
            int s = (int)((op >> 4) & 0x1F);
            return ($"STS  0x{op2:X4}, R{s}", 2);
        }

        // LD / ST with X, Y+, -Y, Z, Z+, -Z ─────────────────────────────────
        if ((op & 0xFE0F) == 0x900C) return ($"LD   R{Rd5(op)}, X",    1);
        if ((op & 0xFE0F) == 0x900D) return ($"LD   R{Rd5(op)}, X+",   1);
        if ((op & 0xFE0F) == 0x900E) return ($"LD   R{Rd5(op)}, -X",   1);
        if ((op & 0xFE0F) == 0x9009) return ($"LD   R{Rd5(op)}, Y+",   1);
        if ((op & 0xFE0F) == 0x900A) return ($"LD   R{Rd5(op)}, -Y",   1);
        if ((op & 0xFE0F) == 0x9001) return ($"LD   R{Rd5(op)}, Z+",   1);
        if ((op & 0xFE0F) == 0x9002) return ($"LD   R{Rd5(op)}, -Z",   1);
        if ((op & 0xFE0F) == 0x8000) return ($"LD   R{Rd5(op)}, Z",    1);
        if ((op & 0xFE0F) == 0x8008) return ($"LD   R{Rd5(op)}, Y",    1);

        if ((op & 0xFE0F) == 0x920C) return ($"ST   X, R{Rd5(op)}",    1);
        if ((op & 0xFE0F) == 0x920D) return ($"ST   X+, R{Rd5(op)}",   1);
        if ((op & 0xFE0F) == 0x920E) return ($"ST   -X, R{Rd5(op)}",   1);
        if ((op & 0xFE0F) == 0x9209) return ($"ST   Y+, R{Rd5(op)}",   1);
        if ((op & 0xFE0F) == 0x920A) return ($"ST   -Y, R{Rd5(op)}",   1);
        if ((op & 0xFE0F) == 0x9201) return ($"ST   Z+, R{Rd5(op)}",   1);
        if ((op & 0xFE0F) == 0x9202) return ($"ST   -Z, R{Rd5(op)}",   1);
        if ((op & 0xFE0F) == 0x8200) return ($"ST   Z, R{Rd5(op)}",    1);
        if ((op & 0xFE0F) == 0x8208) return ($"ST   Y, R{Rd5(op)}",    1);

        // LDD / STD with displacement ─────────────────────────────────────────
        if ((op & 0xD208) == 0x8000 && (op & 0x0200) == 0)
        {
            int d   = (int)((op >> 4) & 0x1F);
            int q   = LddQ(op);
            bool isY = (op & 0x0008) != 0;
            return ($"LDD  R{d}, {(isY ? "Y" : "Z")}+{q}", 1);
        }
        if ((op & 0xD208) == 0x8200 && (op & 0x0200) == 0)
        {
            int d   = (int)((op >> 4) & 0x1F);
            int q   = LddQ(op);
            bool isY = (op & 0x0008) != 0;
            return ($"STD  {(isY ? "Y" : "Z")}+{q}, R{d}", 1);
        }

        // ── Single-register 0x94xx / 0x95xx ──────────────────────────────────
        if ((op & 0xFE0F) == 0x9400) return ($"COM  R{Rd5(op)}", 1);
        if ((op & 0xFE0F) == 0x9401) return ($"NEG  R{Rd5(op)}", 1);
        if ((op & 0xFE0F) == 0x9402) return ($"SWAP R{Rd5(op)}", 1);
        if ((op & 0xFE0F) == 0x9403) return ($"INC  R{Rd5(op)}", 1);
        if ((op & 0xFE0F) == 0x9405) return ($"ASR  R{Rd5(op)}", 1);
        if ((op & 0xFE0F) == 0x9406) return ($"LSR  R{Rd5(op)}", 1);
        if ((op & 0xFE0F) == 0x9407) return ($"ROR  R{Rd5(op)}", 1);
        if ((op & 0xFE0F) == 0x940A) return ($"DEC  R{Rd5(op)}", 1);
        if ((op & 0xFE0F) == 0x920F) return ($"PUSH R{Rd5(op)}", 1);
        if ((op & 0xFE0F) == 0x900F) return ($"POP  R{Rd5(op)}", 1);

        // JMP / CALL (2-word) ─────────────────────────────────────────────────
        if ((op & 0xFE0E) == 0x940C)
        {
            uint addr = ((op & 0x01F0) >> 3 | (op & 1)) << 16 | op2;
            return ($"JMP  0x{addr:X4}", 2);
        }
        if ((op & 0xFE0E) == 0x940E)
        {
            uint addr = ((op & 0x01F0) >> 3 | (op & 1)) << 16 | op2;
            return ($"CALL 0x{addr:X4}", 2);
        }

        // Misc 0x9408 group ──────────────────────────────────────────────────
        if (op == 0x9408) return ("SEC",  1);
        if (op == 0x9418) return ("SEZ",  1);
        if (op == 0x9428) return ("SEN",  1);
        if (op == 0x9438) return ("SEV",  1);
        if (op == 0x9448) return ("SES",  1);
        if (op == 0x9458) return ("SEH",  1);
        if (op == 0x9468) return ("SET",  1);
        if (op == 0x9478) return ("SEI",  1);
        if (op == 0x9488) return ("CLC",  1);
        if (op == 0x9498) return ("CLZ",  1);
        if (op == 0x94A8) return ("CLN",  1);
        if (op == 0x94B8) return ("CLV",  1);
        if (op == 0x94C8) return ("CLS",  1);
        if (op == 0x94D8) return ("CLH",  1);
        if (op == 0x94E8) return ("CLT",  1);
        if (op == 0x94F8) return ("CLI",  1);
        if (op == 0x9508) return ("RET",  1);
        if (op == 0x9518) return ("RETI", 1);
        if (op == 0x9409) return ("IJMP", 1);
        if (op == 0x9509) return ("ICALL",1);
        if (op == 0x95A8) return ("WDR",  1);
        if (op == 0x95C8) return ("LPM",  1);
        if (op == 0x95E8) return ("SPM",  1);
        if (op == 0x9598) return ("BREAK", 1);

        // ADIW / SBIW ─────────────────────────────────────────────────────────
        if ((op & 0xFF00) == 0x9600)
        {
            int d = (int)((op >> 4) & 0x3) * 2 + 24;   // R24, R26, R28, R30
            int k = (int)((op & 0xC0) >> 2) | (int)(op & 0xF);
            return ($"ADIW R{d + 1}:R{d}, {k}", 1);
        }
        if ((op & 0xFF00) == 0x9700)
        {
            int d = (int)((op >> 4) & 0x3) * 2 + 24;
            int k = (int)((op & 0xC0) >> 2) | (int)(op & 0xF);
            return ($"SBIW R{d + 1}:R{d}, {k}", 1);
        }

        // CBI / SBI / SBIC / SBIS ─────────────────────────────────────────────
        if ((op & 0xFF00) == 0x9800) return ($"CBI  0x{(op >> 3) & 0x1F:X2}, {op & 7}", 1);
        if ((op & 0xFF00) == 0x9900) return ($"SBIC 0x{(op >> 3) & 0x1F:X2}, {op & 7}", 1);
        if ((op & 0xFF00) == 0x9A00) return ($"SBI  0x{(op >> 3) & 0x1F:X2}, {op & 7}", 1);
        if ((op & 0xFF00) == 0x9B00) return ($"SBIS 0x{(op >> 3) & 0x1F:X2}, {op & 7}", 1);

        // MUL ─────────────────────────────────────────────────────────────────
        if ((op & 0xFC00) == 0x9C00) return ($"MUL  R{Rd5(op)}, R{Rs5(op)}", 1);

        // Unknown ─────────────────────────────────────────────────────────────
        return ($".word 0x{op:X4}", 1);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static int Rd5(uint op) => (int)((op >> 4) & 0x1F);
    private static int Rs5(uint op) => (int)((op & 0xF) | ((op >> 5) & 0x10));
    private static int RdH(uint op) => (int)(((op >> 4) & 0xF) + 16);
    private static int K8(uint op)  => (int)(((op >> 4) & 0xF0) | (op & 0xF));
    private static int IoAddr(uint op) => (int)(((op >> 5) & 0x30) | (op & 0xF));

    private static int LddQ(uint op)
        => (int)(((op >> 8) & 0x20) | ((op >> 7) & 0x18) | (op & 0x7));

    private static string BrSetName(int s) => s switch
    {
        0 => "BRCS", 1 => "BREQ", 2 => "BRMI", 3 => "BRVS",
        4 => "BRLT", 5 => "BRHS", 6 => "BRTS", 7 => "BRIE",
        _ => $"BRBS {s},"
    };

    private static string BrClrName(int s) => s switch
    {
        0 => "BRCC", 1 => "BRNE", 2 => "BRPL", 3 => "BRVC",
        4 => "BRGE", 5 => "BRHC", 6 => "BRTC", 7 => "BRID",
        _ => $"BRBC {s},"
    };
}

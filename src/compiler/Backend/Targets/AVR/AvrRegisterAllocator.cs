using PyMCU.IR;

namespace PyMCU.Backend.Targets.AVR;

public static class AvrRegisterAllocator
{
    private static int SizeOfType(DataType t) =>
        t is DataType.UINT16 or DataType.INT16 ? 2 : 1;

    public static Dictionary<string, string> Allocate(ProgramIR program)
    {
        var useCount = new Dictionary<string, int>();
        var varTypes = new Dictionary<string, DataType>();

        foreach (var instr in program.Functions.SelectMany(func => func.Body))
        {
            switch (instr)
            {
                case Copy c:
                    CountVal(c.Src);
                    CountVal(c.Dst);
                    break;
                case Binary b:
                    CountVal(b.Src1);
                    CountVal(b.Src2);
                    CountVal(b.Dst);
                    break;
                case Unary u:
                    CountVal(u.Src);
                    CountVal(u.Dst);
                    break;
                case Return r: CountVal(r.Value); break;
                case JumpIfZero jz: CountVal(jz.Condition); break;
                case JumpIfNotZero jnz: CountVal(jnz.Condition); break;
                case JumpIfEqual je:
                    CountVal(je.Src1);
                    CountVal(je.Src2);
                    break;
                case JumpIfNotEqual jne:
                    CountVal(jne.Src1);
                    CountVal(jne.Src2);
                    break;
                case JumpIfLessThan jlt:
                    CountVal(jlt.Src1);
                    CountVal(jlt.Src2);
                    break;
                case JumpIfLessOrEqual jle:
                    CountVal(jle.Src1);
                    CountVal(jle.Src2);
                    break;
                case JumpIfGreaterThan jgt:
                    CountVal(jgt.Src1);
                    CountVal(jgt.Src2);
                    break;
                case JumpIfGreaterOrEqual jge:
                    CountVal(jge.Src1);
                    CountVal(jge.Src2);
                    break;
                case BitCheck bc:
                    CountVal(bc.Source);
                    CountVal(bc.Dst);
                    break;
                case BitWrite bw:
                    CountVal(bw.Target);
                    CountVal(bw.Src);
                    break;
                case BitSet bs: CountVal(bs.Target); break;
                case BitClear bcl: CountVal(bcl.Target); break;
                case AugAssign aa:
                    CountVal(aa.Target);
                    CountVal(aa.Operand);
                    break;
                case JumpIfBitSet jbs: CountVal(jbs.Source); break;
                case JumpIfBitClear jbc: CountVal(jbc.Source); break;
                case Call cl:
                    CountVal(cl.Dst);
                    foreach (var a in cl.Args) CountVal(a);
                    break;
                case ArrayLoad al:
                    CountVal(al.Index);
                    CountVal(al.Dst);
                    break;
                case ArrayStore ast:
                    CountVal(ast.Index);
                    CountVal(ast.Src);
                    break;
            }
        }

        var sorted = useCount.OrderByDescending(kv => kv.Value).ToList();

        var result = new Dictionary<string, string>();
        var nextReg = 4;

        foreach (var (name, _) in sorted)
        {
            if (nextReg > 15) break;
            var sz = varTypes.TryGetValue(name, out var dt) ? SizeOfType(dt) : 1;
            if (nextReg + sz - 1 > 15) break;
            result[name] = $"R{nextReg}";
            nextReg += sz;
        }

        return result;

        static int DotCount(string s) => s.Count(c => c == '.');

        void CountVal(Val val)
        {
            if (val is not Variable v) return;
            if (DotCount(v.Name) >= 2) return;
            useCount.TryGetValue(v.Name, out int count);
            useCount[v.Name] = count + 1;
            varTypes[v.Name] = v.Type;
        }
    }
}
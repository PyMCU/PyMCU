using PyMCU.IR;

namespace PyMCU.Backend.Targets.AVR;

public static class AvrLinearScan
{
    private class LiveInterval
    {
        public string Name = "";
        public DataType Type;
        public int Def;
        public int LastUse;
        public bool SpansCall;
    }

    public static Dictionary<string, string> Allocate(Function func)
    {
        var intervals = new Dictionary<string, LiveInterval>();
        var callIndices = new HashSet<int>();

        void VisitVal(Val val, int i)
        {
            if (val is not Temporary t) return;
            if (intervals.TryGetValue(t.Name, out var iv))
                iv.LastUse = i;
            else
                intervals[t.Name] = new LiveInterval { Name = t.Name, Type = t.Type, Def = i, LastUse = i };
        }

        for (int i = 0; i < func.Body.Count; ++i)
        {
            var instr = func.Body[i];
            if (instr is Call) callIndices.Add(i);

            switch (instr)
            {
                case Copy c:
                    VisitVal(c.Src, i);
                    VisitVal(c.Dst, i);
                    break;
                case Binary b:
                    VisitVal(b.Src1, i);
                    VisitVal(b.Src2, i);
                    VisitVal(b.Dst, i);
                    break;
                case Unary u:
                    VisitVal(u.Src, i);
                    VisitVal(u.Dst, i);
                    break;
                case Return r: VisitVal(r.Value, i); break;
                case JumpIfZero jz: VisitVal(jz.Condition, i); break;
                case JumpIfNotZero jnz: VisitVal(jnz.Condition, i); break;
                case JumpIfEqual je:
                    VisitVal(je.Src1, i);
                    VisitVal(je.Src2, i);
                    break;
                case JumpIfNotEqual jne:
                    VisitVal(jne.Src1, i);
                    VisitVal(jne.Src2, i);
                    break;
                case JumpIfLessThan jlt:
                    VisitVal(jlt.Src1, i);
                    VisitVal(jlt.Src2, i);
                    break;
                case JumpIfLessOrEqual jle:
                    VisitVal(jle.Src1, i);
                    VisitVal(jle.Src2, i);
                    break;
                case JumpIfGreaterThan jgt:
                    VisitVal(jgt.Src1, i);
                    VisitVal(jgt.Src2, i);
                    break;
                case JumpIfGreaterOrEqual jge:
                    VisitVal(jge.Src1, i);
                    VisitVal(jge.Src2, i);
                    break;
                case BitCheck bc:
                    VisitVal(bc.Source, i);
                    VisitVal(bc.Dst, i);
                    break;
                case BitWrite bw:
                    VisitVal(bw.Target, i);
                    VisitVal(bw.Src, i);
                    break;
                case BitSet bs: VisitVal(bs.Target, i); break;
                case BitClear bcl: VisitVal(bcl.Target, i); break;
                case AugAssign aa:
                    VisitVal(aa.Target, i);
                    VisitVal(aa.Operand, i);
                    break;
                case JumpIfBitSet jbs: VisitVal(jbs.Source, i); break;
                case JumpIfBitClear jbc: VisitVal(jbc.Source, i); break;
                case Call cl:
                    VisitVal(cl.Dst, i);
                    foreach (var a in cl.Args) VisitVal(a, i);
                    break;
                case LoadIndirect li:
                    VisitVal(li.SrcPtr, i);
                    VisitVal(li.Dst, i);
                    break;
                case StoreIndirect si:
                    VisitVal(si.DstPtr, i);
                    VisitVal(si.Src, i);
                    break;
            }
        }

        // Mark intervals that strictly span a Call
        foreach (var iv in intervals.Values)
        {
            foreach (int ci in callIndices)
            {
                if (iv.Def < ci && ci < iv.LastUse)
                {
                    iv.SpansCall = true;
                    break;
                }
            }
        }

        // Collect eligible (UINT8, no call span), sort by def
        var eligible = intervals.Values
            .Where(iv => !iv.SpansCall && iv.Type == DataType.UINT8)
            .OrderBy(iv => iv.Def)
            .ToList();

        // Greedy assignment to R16/R17
        var result = new Dictionary<string, string>();
        var active = new LiveInterval?[2];

        foreach (var iv in eligible)
        {
            for (int k = 0; k < 2; ++k)
            {
                if (active[k] != null && active[k]!.LastUse < iv.Def)
                    active[k] = null;
            }

            for (int k = 0; k < 2; ++k)
            {
                if (active[k] == null)
                {
                    result[iv.Name] = k == 0 ? "R16" : "R17";
                    active[k] = iv;
                    break;
                }
            }
        }

        return result;
    }
}
/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *
 * -----------------------------------------------------------------------------
 * SAFETY WARNING / HIGH RISK ACTIVITIES:
 * THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
 * ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
 * NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
 * TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
 * -----------------------------------------------------------------------------
 */

using PyMCU.IR;

namespace PyMCU.Backend.Analysis;

public class DynamicStackAllocator(int wordSize = 4, int reservedTop = 8)
{
    public (Dictionary<string, int> Offsets, int TotalSize) Allocate(Function func)
    {
        var offsets = new Dictionary<string, int>();
        var currentOffset = -reservedTop;

        foreach (var param in func.Params)
            AllocVar(param);

        foreach (var instr in func.Body)
        {
            switch (instr)
            {
                case Copy c:
                    CheckVal(c.Src);
                    CheckVal(c.Dst);
                    break;
                case Binary b:
                    CheckVal(b.Src1);
                    CheckVal(b.Src2);
                    CheckVal(b.Dst);
                    break;
                case Unary u:
                    CheckVal(u.Src);
                    CheckVal(u.Dst);
                    break;
                case Call cl:
                    foreach (var a in cl.Args) CheckVal(a);
                    CheckVal(cl.Dst);
                    break;
                case Return r: CheckVal(r.Value); break;
                case JumpIfZero jz: CheckVal(jz.Condition); break;
                case JumpIfNotZero jnz: CheckVal(jnz.Condition); break;
                case BitSet bs: CheckVal(bs.Target); break;
                case BitClear bc: CheckVal(bc.Target); break;
                case BitCheck bck:
                    CheckVal(bck.Source);
                    CheckVal(bck.Dst);
                    break;
                case BitWrite bw:
                    CheckVal(bw.Target);
                    CheckVal(bw.Src);
                    break;
            }

            continue;

            void CheckVal(Val v)
            {
                switch (v)
                {
                    case Variable var2:
                        AllocVar(var2.Name);
                        break;
                    case Temporary tmp:
                        AllocVar(tmp.Name);
                        break;
                }
            }
        }

        var totalSize = -currentOffset;
        if (totalSize % 16 != 0)
            totalSize += 16 - (totalSize % 16);

        return (offsets, totalSize);

        void AllocVar(string name)
        {
            if (offsets.ContainsKey(name)) return;
            currentOffset -= wordSize;
            offsets[name] = currentOffset;
        }
    }
}
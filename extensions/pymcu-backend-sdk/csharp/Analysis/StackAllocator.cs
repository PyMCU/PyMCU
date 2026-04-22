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

using PyMCU.IR;

namespace PyMCU.Backend.Analysis;

public class StackAllocator
{
    private class FunctionNode
    {
        public string Name = "";
        public int LocalSize;
        public List<string> Callees = new();
        public HashSet<string> Locals = new();
        public bool Visited;
    }

    private readonly Dictionary<string, FunctionNode> _callGraph = new();
    private readonly Dictionary<string, int> _offsets = new();
    private readonly Dictionary<string, int> _offsetsBase = new();
    private readonly HashSet<string> _globalNames = [];
    private int _maxStackUsage;

    public Dictionary<string, int> VariableSizes { get; } = new();

    public (Dictionary<string, int> Offsets, int MaxStack) Allocate(ProgramIR program)
    {
        _offsets.Clear();
        _offsetsBase.Clear();
        _callGraph.Clear();
        _globalNames.Clear();
        VariableSizes.Clear();
        _maxStackUsage = 0;

        var globalOffset = 0;
        foreach (var globalVar in program.Globals)
        {
            VariableSizes[globalVar.Name] = globalVar.Type.SizeOf();
            _offsets[globalVar.Name] = globalOffset;
            _globalNames.Add(globalVar.Name);
            globalOffset += VariableSizes[globalVar.Name];
        }

        if (globalOffset > _maxStackUsage) _maxStackUsage = globalOffset;

        BuildGraph(program);

        if (_callGraph.ContainsKey("main"))
            CalculateOffsets("main", globalOffset);

        foreach (var func in program.Functions.Where(func => func.IsInterrupt && _callGraph.ContainsKey(func.Name)))
        {
            CalculateOffsets(func.Name, globalOffset);
        }

        return (_offsets, _maxStackUsage);
    }

    private void BuildGraph(ProgramIR program)
    {
        foreach (var func in program.Functions)
        {
            var node = new FunctionNode { Name = func.Name };
            _callGraph[func.Name] = node;

            foreach (var param in func.Params)
                node.Locals.Add(param);

            void RegisterVar(Val val)
            {
                if (val is Variable v && !_globalNames.Contains(v.Name))
                {
                    node.Locals.Add(v.Name);
                    VariableSizes[v.Name] = v.Type.SizeOf();
                }

                if (val is Temporary t)
                {
                    node.Locals.Add(t.Name);
                    VariableSizes[t.Name] = t.Type.SizeOf();
                }
            }

            foreach (var instr in func.Body)
            {
                switch (instr)
                {
                    case Copy c:
                        RegisterVar(c.Src);
                        RegisterVar(c.Dst);
                        break;
                    case Binary b:
                        RegisterVar(b.Src1);
                        RegisterVar(b.Src2);
                        RegisterVar(b.Dst);
                        break;
                    case Unary u:
                        RegisterVar(u.Src);
                        RegisterVar(u.Dst);
                        break;
                    case BitSet bs: RegisterVar(bs.Target); break;
                    case BitClear bc: RegisterVar(bc.Target); break;
                    case BitCheck bck:
                        RegisterVar(bck.Source);
                        RegisterVar(bck.Dst);
                        break;
                    case BitWrite bw:
                        RegisterVar(bw.Src);
                        RegisterVar(bw.Target);
                        break;
                    case Call cl:
                        node.Callees.Add(cl.FunctionName);
                        switch (cl.Dst)
                        {
                            case Variable cv:
                                node.Locals.Add(cv.Name);
                                break;
                            case Temporary ct:
                                node.Locals.Add(ct.Name);
                                break;
                        }

                        break;
                    case Return r: RegisterVar(r.Value); break;
                    case JumpIfZero jz: RegisterVar(jz.Condition); break;
                    case JumpIfNotZero jnz: RegisterVar(jnz.Condition); break;
                    case JumpIfBitSet jbs: RegisterVar(jbs.Source); break;
                    case JumpIfBitClear jbc: RegisterVar(jbc.Source); break;
                    case ArrayLoad al:
                        if (!_globalNames.Contains(al.ArrayName) && node.Locals.Add(al.ArrayName))
                        {
                            VariableSizes[al.ArrayName] = al.Count * al.ElemType.SizeOf();
                        }

                        RegisterVar(al.Index);
                        RegisterVar(al.Dst);
                        break;
                    case ArrayStore ast:
                        if (!_globalNames.Contains(ast.ArrayName) && node.Locals.Add(ast.ArrayName))
                        {
                            VariableSizes[ast.ArrayName] = ast.Count * ast.ElemType.SizeOf();
                        }

                        RegisterVar(ast.Index);
                        RegisterVar(ast.Src);
                        break;
                }
            }

            node.LocalSize = node.Locals.Count;
        }
    }

    private void CalculateOffsets(string funcName, int currentBase)
    {
        var node = _callGraph[funcName];
        if (node.Visited) return;
        node.Visited = true;

        if (node.Locals.Count > 0)
        {
            var first = node.Locals.GetEnumerator();
            first.MoveNext();
            if (_offsets.ContainsKey(first.Current))
            {
                if (currentBase <= (_offsetsBase.GetValueOrDefault(funcName, 0)))
                {
                    node.Visited = false;
                    return;
                }
            }
        }
        else if (_offsetsBase.TryGetValue(funcName, out var value))
        {
            if (currentBase <= value)
            {
                node.Visited = false;
                return;
            }
        }

        _offsetsBase[funcName] = currentBase;

        int currentFrameSize = 0;
        foreach (var varName in node.Locals)
        {
            if (_globalNames.Contains(varName)) continue;
            _offsets[varName] = currentBase + currentFrameSize;
            currentFrameSize += VariableSizes.GetValueOrDefault(varName, 1);
        }

        int childrenBase = currentBase + currentFrameSize;
        if (childrenBase > _maxStackUsage) _maxStackUsage = childrenBase;

        foreach (var callee in node.Callees)
        {
            if (_callGraph.ContainsKey(callee))
                CalculateOffsets(callee, childrenBase);
        }

        node.Visited = false;
    }
}
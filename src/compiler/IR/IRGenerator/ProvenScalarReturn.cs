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

using System.Collections.Generic;
using System.Linq;
using PyMCU.Common;
using PyMCU.Frontend;

namespace PyMCU.IR.IRGenerator;

public partial class IRGenerator
{
    // Whole-program, pre-lowering pass computing functionsReturnProvenScalar (Call.cs):
    // every function whose return value is structurally proven, from its own declared
    // buffer parameters alone, to always be a single scalar element.
    //
    // This cannot wait until each function's body is lowered: a call site can be lowered
    // BEFORE its callee's own body is (functions have no fixed lowering order), and the
    // result needs to be a program-wide fact by the time the first call to it is checked,
    // not something built up incrementally the way provenScalarElements is for a NAME
    // within the one function currently being lowered. Computed purely from the AST and
    // each function's own declared parameter types -- no lowering-time table (arraySizes,
    // bytearrayParams) is touched or needed here, because a parameter's declared type
    // alone already says whether it is a buffer.
    private void ComputeFunctionsReturnProvenScalar(ProgramNode ast)
    {
        functionsReturnProvenScalar = new();

        void Check(FunctionDef fn, string fullName)
        {
            var bufferParams = new HashSet<string>(
                fn.Params.Where(p => DeclaredTypeIsBuffer(p.Type)).Select(p => p.Name));
            if (bufferParams.Count == 0) return;
            bool any = false, allScalar = true;
            foreach (var ret in TypeInference.WalkStatements(fn.Body).OfType<ReturnStmt>())
            {
                any = true;
                if (ret.Value == null || !ReturnValueProvesScalarOfParam(ret.Value, bufferParams))
                {
                    allScalar = false;
                    break;
                }
            }
            if (any && allScalar) functionsReturnProvenScalar.Add(fullName);
        }

        foreach (var fn in ast.Functions) Check(fn, fn.Name);

        void WalkClass(ClassDef cls, string prefix)
        {
            string className = prefix + cls.Name;
            if (cls.Body is not Block body) return;
            foreach (var member in body.Statements)
            {
                if (member is FunctionDef fn) Check(fn, className + "_" + fn.Name);
                else if (member is ClassDef nested) WalkClass(nested, className + "_");
            }
        }
        foreach (var s in ast.GlobalStatements)
            if (s is ClassDef cls) WalkClass(cls, "");
    }

    // Whether a subscript TARGET is one of this function's own buffer parameters, or a
    // slice of one -- slicing a buffer gives another buffer of the SAME element type, so
    // whether it holds scalar elements is the same question asked of what it was sliced
    // from. A field is not covered: none of the cases this fixes return an element of a
    // field's buffer, only of a parameter's.
    private static bool IsBufferExprOverParams(Expression target, HashSet<string> bufferParamNames) =>
        target switch
        {
            VariableExpr tv => bufferParamNames.Contains(tv.Name),
            IndexExpr { Index: SliceExpr, Target: var inner } => IsBufferExprOverParams(inner, bufferParamNames),
            _ => false,
        };

    // Whether `e` -- a `return` statement's value -- structurally proves a single scalar
    // element of one of this function's OWN buffer parameters. Mirrors
    // ExpressionIsProvenScalarElement's shapes (subscript, walrus, ternary) but answers
    // from declared parameter types alone, not from any lowering-time table, which is the
    // one most call expressions would need (another function's own proven-scalar return) --
    // deliberately NOT recursed into here: this answers for ONE function's own body, not
    // the whole call graph, and a call graph cycle would make a general version non-terminating.
    private static bool ReturnValueProvesScalarOfParam(Expression e, HashSet<string> bufferParamNames) =>
        e switch
        {
            IndexExpr { Index: not SliceExpr, Target: var t } => IsBufferExprOverParams(t, bufferParamNames),
            WalrusExpr we => ReturnValueProvesScalarOfParam(we.Value, bufferParamNames),
            TernaryExpr te => ReturnValueProvesScalarOfParam(te.TrueVal, bufferParamNames)
                              && ReturnValueProvesScalarOfParam(te.FalseVal, bufferParamNames),
            _ => false,
        };
}

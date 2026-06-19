/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
 * -----------------------------------------------------------------------------
 */

using PyMCU.Common;

namespace PyMCU.IR;

/// <summary>
/// Determines which functions in a <see cref="ProgramIR"/> can propagate errors
/// to their callers (i.e. contain or transitively reach a <see cref="SignalError"/>
/// that is not caught inside the function itself).
///
/// Algorithm:
///   Phase 1 — seed: mark every function that contains a <see cref="SignalError"/>
///             instruction as CanFail = true.
///   Phase 2 — propagate: repeat until stable: if function A calls function B and
///             B.CanFail is true AND the call is not wrapped in a try/catch that
///             swallows the error, mark A.CanFail = true.
///   Phase 3 — validate: enforce FFI and ISR boundary rules.
///
/// "Lazy propagation" means we only propagate when the error ESCAPES the function.
/// A <see cref="BranchOnError"/> that jumps to a local catch handler (a label inside
/// the same function) does NOT cause the function to be CanFail.
/// </summary>
public static class CanFailAnalyzer
{
    public static void Analyze(ProgramIR program)
    {
        // Build a fast name → Function lookup.
        var byName = program.Functions.ToDictionary(f => f.Name);

        // Extern stubs: @extern functions never signal errors (C ABI, no T-flag protocol).
        foreach (var f in program.Functions.Where(f => f.IsExtern))
            f.CanFail = false;

        // Phase 1 — seed: direct SignalError that is NOT locally handled.
        foreach (var func in program.Functions)
        {
            if (func.IsExtern) continue;
            if (HasUnhandledSignalError(func))
                func.CanFail = true;
        }

        // Phase 2 — fixed-point propagation through the call graph.
        // We keep iterating until no new function is marked CanFail.
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var func in program.Functions)
            {
                if (func.CanFail || func.IsExtern) continue;

                if (CallsCanFailAndEscapes(func, byName))
                {
                    func.CanFail = true;
                    changed = true;
                }
            }
        }

        // Phase 3 — boundary validation.
        foreach (var func in program.Functions)
        {
            if (!func.CanFail) continue;

            // @export_c boundary: C callers have no T-flag protocol.
            if (func.IsExportC)
                throw new ArchitectureError(
                    $"Function '{func.Name}' is exported to C (@export_c) but can propagate " +
                    "an error to its caller. Catch all errors inside the function or remove " +
                    "the error-raising path before exporting.",
                    line: 0, column: 0);

            // ISR boundary: no caller exists to receive the T-flag signal.
            if (func.IsInterrupt)
                throw new ArchitectureError(
                    $"ISR '{func.Name}' can propagate an error, but ISRs have no caller to " +
                    "receive the error signal. Handle errors inside the ISR or use a volatile " +
                    "error-flag global instead of raising.",
                    line: 0, column: 0);
        }
    }

    /// <summary>
    /// Makes uncaught errors fail loudly instead of being silently ignored. After CanFail is
    /// known, every call to a CanFail callee that is NOT already guarded (no BranchOnError after
    /// it — i.e. it is not inside a try) gets a guard:
    ///   - in main / an ISR / an @export_c entry (no caller to propagate to): branch to
    ///     __pymcu_unhandled_exn, which halts (and prints the exception name where a UART exists);
    ///   - in any other (propagating) function: branch to a per-function epilogue that returns with
    ///     the T-flag still set, re-raising the SAME error (R22 untouched) to the caller.
    /// Without this, an error raised outside any try set T but no one checked it: the next CanFail
    /// callee's happy-path CLT cleared T and execution continued with a garbage value.
    /// </summary>
    public static void InsertUncaughtPropagation(ProgramIR program)
    {
        var canFail = program.Functions.Where(f => !f.IsExtern).Select(f => f.Name).ToHashSet();

        foreach (var func in program.Functions)
        {
            if (func.IsExtern) continue;

            // A function that can propagate (not a top-level/boundary frame) re-raises via an
            // epilogue; main/ISR/@export_c cannot propagate, so an uncaught error halts there.
            bool propagates = func.CanFail && func.Name != "main"
                              && !func.IsInterrupt && !func.IsExportC;
            string propLabel = $"__exn_prop_{func.Name}";
            string target = propagates ? propLabel : "__pymcu_unhandled_exn";

            bool usedProp = false;
            var body = func.Body;
            for (int i = 0; i < body.Count; i++)
            {
                if (body[i] is not Call call) continue;
                if (!canFail.Contains(call.FunctionName)) continue;
                if (!program.Functions.First(f => f.Name == call.FunctionName).CanFail) continue;

                // Already guarded (a BranchOnError follows — this call is inside a try)?
                int j = i + 1;
                while (j < body.Count && body[j] is DebugLine) j++;
                if (j < body.Count && body[j] is BranchOnError) continue;

                body.Insert(i + 1, new BranchOnError(target));
                usedProp |= propagates;
                i++;  // skip past the inserted guard
            }

            // Re-raise epilogue: keep the caller-set error code (SignalError with code 0 does not
            // reload R22) and return with T still set (SignalError emits RET without the CLT that
            // a normal CanFail return injects).
            if (usedProp)
            {
                body.Add(new Label(propLabel));
                body.Add(new SignalError(new Constant(0), null));
            }
        }
    }

    // Returns true if `func` contains a SignalError that is not covered by a local
    // BranchOnError that jumps to a label inside the same function body.
    private static bool HasUnhandledSignalError(Function func)
    {
        // Collect labels that are targets of BranchOnError within this function —
        // these represent local catch dispatch points.
        var localCatchLabels = new HashSet<string>();
        foreach (var instr in func.Body)
            if (instr is BranchOnError boe)
                localCatchLabels.Add(boe.ErrorLabel);

        // A SignalError is "unhandled" when none of its enclosing BranchOnError
        // targets are local labels (i.e. the error escapes to the caller).
        // For now we use a conservative rule: if ANY SignalError exists and there
        // are no local BranchOnError handlers covering the same region, we mark
        // CanFail. A future DFA pass can refine this to a per-path analysis.
        // Only SignalErrors that propagate to the caller (CatchLabel == null) can make
        // this function CanFail. A SignalError with a CatchLabel is a raise caught inside
        // this same function (delivered to a local catch dispatcher) — it never escapes.
        bool hasSignalError = func.Body.OfType<SignalError>().Any(se => se.CatchLabel is null);
        bool hasCatchAll = localCatchLabels.Count > 0;

        // If there are no local handlers at all, every SignalError escapes.
        if (hasSignalError && !hasCatchAll) return true;

        // If there are handlers, conservatively assume they may not cover all paths
        // unless the count of SignalError sites exceeds the count of BranchOnError
        // guards. A precise analysis requires a CFG walk; that can be added later
        // using the existing CFG infrastructure in IR/CFG/.
        if (hasSignalError && hasCatchAll)
        {
            int raiseCount  = func.Body.OfType<SignalError>().Count(se => se.CatchLabel is null);
            int guardCount  = func.Body.OfType<BranchOnError>().Count();
            // More raises than guards → at least one path escapes.
            if (raiseCount > guardCount) return true;
        }

        return false;
    }

    // Returns true if `func` calls a CanFail callee AND does not have a BranchOnError
    // immediately after EVERY such call (i.e. at least one call's error escapes).
    private static bool CallsCanFailAndEscapes(Function func, Dictionary<string, Function> byName)
    {
        var body = func.Body;
        for (int i = 0; i < body.Count; i++)
        {
            if (body[i] is not Call call) continue;

            if (!byName.TryGetValue(call.FunctionName, out var callee)) continue;
            if (!callee.CanFail) continue;

            // The call site is guarded if the very next non-debug instruction is
            // BranchOnError. This is the contract enforced by the IR transform pass.
            bool guarded = false;
            for (int j = i + 1; j < body.Count; j++)
            {
                if (body[j] is DebugLine) continue;  // skip debug markers
                guarded = body[j] is BranchOnError;
                break;
            }

            if (!guarded) return true;  // at least one call site's error escapes
        }

        return false;
    }
}

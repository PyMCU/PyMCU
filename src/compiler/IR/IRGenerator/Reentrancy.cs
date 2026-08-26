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

namespace PyMCU.IR.IRGenerator;

/// <summary>
/// An outlined function is not reentrant. Its parameters, its `self` pointer and its
/// temporaries are statically allocated NAMES, one set per subroutine rather than one per
/// invocation, so if the same body is entered twice at once the second entry overwrites the
/// first one's state and the first reads back the second's answer.
///
/// That happens whenever one body is reachable from two contexts that can interrupt each other.
/// It is silent, it is intermittent, and it is on the default path: `@inline` avoids it only
/// because there is no shared body left to re-enter.
///
/// The condition is NOT "reachable from an ISR and from main". Two ISRs reproduce it without
/// main in the picture, which is the harder case to notice because it needs one of them to
/// re-enable interrupts before the other can nest inside it.
/// </summary>
public partial class IRGenerator
{
    /// <summary>Bit 7 of SREG is the AVR global interrupt enable; setting it allows nesting.</summary>
    private const int GlobalInterruptEnableBit = 7;

    /// <summary>
    /// Refuses a program where one outlined body can be entered from two contexts that can
    /// interrupt each other.
    /// </summary>
    /// <remarks>
    /// Refusing rather than repairing is deliberate. Force-inlining the body would multiply
    /// flash on a part that may have 4 KB, for a hazard the user cannot see in their source;
    /// saving and restoring the statics costs cycles inside an ISR, which is the one place in
    /// an embedded program where cycles are the point, and it would pay that whether or not the
    /// race is ever reachable. Both price a tradeoff on the user's behalf. Refusing hands them
    /// the information and the choice: mark the function `@inline` and pay flash where they can
    /// see it, or restructure so the two contexts do not share it.
    ///
    /// Runs on the finished IR rather than the AST, which is what makes it correct about
    /// `@inline`: an inlined body has already been expanded into its callers and is not a
    /// function here at all, so it cannot be flagged.
    ///
    /// KNOWN GAPS, measured rather than assumed:
    ///
    ///   * An INDIRECT call (a function pointer, `icall`) cannot be resolved, so anything
    ///     reachable only through one is invisible to this. Measured: none of the 21
    ///     interrupt-using programs in the AVR corpus emits a single `icall`, the RTOS example
    ///     included -- its function pointers resolve before the IR. So the gap is real in
    ///     principle and unexercised in practice today, which is a reason to record it rather
    ///     than to close it.
    ///   * Interrupts re-enabled by any route other than `asm("SEI")` or setting SREG bit 7 --
    ///     a backend intrinsic, say. Those two are what the stdlib and user code actually use.
    ///   * Chips whose global-interrupt-enable is not SREG bit 7. The SREG lookup below simply
    ///     finds nothing there, and every ISR is then treated as non-nesting, which is the
    ///     conservative direction: main-versus-ISR sharing is still refused.
    /// </remarks>
    private void CheckReentrancy(ProgramIR program)
    {
        var byName = new Dictionary<string, Function>();
        foreach (var f in program.Functions) byName[f.Name] = f;

        var isrs = program.Functions.Where(f => f.IsInterrupt).Select(f => f.Name).ToList();
        if (isrs.Count == 0) return;   // nothing can preempt anything

        var entries = new List<string>(isrs);
        if (byName.ContainsKey("main")) entries.Insert(0, "main");

        // The call graph, direct calls only. An icall is a gap, documented above.
        Dictionary<string, List<string>> callees = new();
        foreach (var f in program.Functions)
            callees[f.Name] = f.Body.OfType<Call>()
                .Select(c => c.FunctionName)
                .Where(byName.ContainsKey)
                .Distinct()
                .ToList();

        HashSet<string> ReachableFrom(string root)
        {
            var seen = new HashSet<string>();
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
                foreach (var c in callees.GetValueOrDefault(stack.Pop(), new List<string>()))
                    if (seen.Add(c))
                        stack.Push(c);
            return seen;
        }

        var reach = entries.ToDictionary(e => e, ReachableFrom);
        var nesting = isrs.Where(i => ReenablesInterrupts(i, byName, callees, new HashSet<string>()))
                          .ToHashSet();

        // main is interruptible by every ISR. An ISR is interruptible only by another ISR, and
        // only once it has re-enabled interrupts: the hardware clears the enable bit on entry,
        // so without that a second interrupt simply waits.
        bool CanInterrupt(string a, string b) =>
            (b == "main" && isrs.Contains(a)) || (a != b && isrs.Contains(a) && nesting.Contains(b));

        foreach (var f in program.Functions)
        {
            if (f.IsInterrupt || f.Name == "main") continue;
            var from = entries.Where(e => reach[e].Contains(f.Name)).ToList();

            for (int i = 0; i < from.Count; ++i)
                for (int j = i + 1; j < from.Count; ++j)
                {
                    if (!CanInterrupt(from[i], from[j]) && !CanInterrupt(from[j], from[i])) continue;

                    string one = from[i], two = from[j];
                    string why = one == "main" || two == "main"
                        ? "an interrupt can arrive while the other one is still inside it"
                        : $"'{(nesting.Contains(one) ? one : two)}' re-enables interrupts, so the "
                          + "other can nest inside it";

                    throw new CompilerError("CompileError",
                        $"'{f.Name}' is reached from '{one}' and from '{two}', and {why}. It is "
                        + "compiled once as a shared subroutine, so its parameters and temporaries "
                        + "are one set of fixed locations rather than one set per call: the second "
                        + "entry overwrites the first one's, and the first reads back the second's "
                        + "answer. Mark it @inline so each caller gets its own copy, or arrange for "
                        + "only one of the two to call it.", 1);
                }
        }
    }

    /// <summary>True when this function, or anything it calls, turns interrupts back on.</summary>
    private bool ReenablesInterrupts(string name, Dictionary<string, Function> byName,
                                     Dictionary<string, List<string>> callees, HashSet<string> seen)
    {
        if (!seen.Add(name) || !byName.TryGetValue(name, out var fn)) return false;

        foreach (var insn in fn.Body)
        {
            // `asm("SEI")`, which is how a user writes it.
            if (insn is InlineAsm asm && asm.Code.ToUpperInvariant().Contains("SEI"))
                return true;

            // `SREG[7] = 1`, which is how the HAL writes it -- pin_irq_setup ends that way. It
            // is a bit-set on a register address, not an asm node, so an asm-only scan misses
            // it: measured on a two-ISR program where the nesting went undetected.
            if (insn is BitSet bs && bs.Bit == GlobalInterruptEnableBit
                && bs.Target is MemoryAddress addr && addr.Address == StatusRegisterAddress())
                return true;
        }

        return callees.GetValueOrDefault(name, new List<string>())
            .Any(c => ReenablesInterrupts(c, byName, callees, seen));
    }

    /// <summary>The chip's SREG address, or -1 when this chip does not name one.</summary>
    private int StatusRegisterAddress()
    {
        foreach (var key in new[] { "SREG", currentModulePrefix + "SREG" })
            if (globals.TryGetValue(key, out var sym) && sym.IsMemoryAddress)
                return sym.Value;

        foreach (var kv in globals)
            if (kv.Key.EndsWith("SREG", StringComparison.Ordinal) && kv.Value.IsMemoryAddress)
                return kv.Value.Value;

        return -1;
    }
}

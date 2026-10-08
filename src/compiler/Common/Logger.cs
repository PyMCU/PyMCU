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

using Spectre.Console;

namespace PyMCU.Common;

// Logger — single stdout channel for the compiler.
//
// Two modes, selected automatically at Initialize() time:
//
//   Driver mode  (Console.IsOutputRedirected == true)
//     stdout emits structured plain-text tokens that the Python driver parses:
//       [PHASE_START] <name>
//       [PHASE_END]   <name> <elapsedMs>
//       [BUILD_OK]    <outputPath>
//       [BUILD_FAIL]  <phaseName>
//       [INFO]        [<component>] <message>
//       [VERBOSE]     [<component>] <message>
//       [NEEDS_ARENA]                -- a runtime-sized bytearray(n) met a missing
//                                       pymcu.arena import; the driver injects the
//                                       allocator and compiles again
//       [ARENA_USED]                 -- an arena allocation was lowered (the import
//                                       resolved); the driver still stages the shim
//                                       that carries the real ARENA_SIZE
//       [NEEDS_STRFMT]               -- a run-time string build (f-string value,
//                                       str()/repr()/hex()/bin()/oct() of a run-time
//                                       value...) met a missing pymcu.strfmt import
//       [NEEDS_ROUND2]               -- round(x, n) on a run-time float met a missing
//                                       pymcu.round2 import
//       [NEEDS_CLOCKS]               -- an RP2350 program lowered no clock_init()
//                                       call; the driver injects it
//       [STDOUT_OWNED]               -- the program constructs a UART; the driver
//                                       leaves stdout to it
//     All warnings/errors go to stderr (never pollute the token stream).
//
//   Interactive mode (stdout is a real TTY)
//     Uses Spectre.Console Markup for coloured, human-friendly output.
//     Warnings still go to stderr so VS Code problem-matcher keeps working.
//
// Diagnostic.cs remains the sole owner of machine-readable error lines on stderr.
public static class Logger
{
    private static bool _isVerbose;
    private static bool _isDriverMode;

    public static void Initialize(bool verbose)
    {
        _isVerbose = verbose;
        _isDriverMode = Console.IsOutputRedirected;
    }

    // ── Banner ──────────────────────────────────────────────────────────────

    public static void PrintBanner(string version)
    {
        if (_isDriverMode) return;
        var rule = new Rule($"[bold cyan]pymcuc[/] [dim]v{Markup.Escape(version)}[/]");
        rule.Justification = Justify.Left;
        AnsiConsole.Write(rule);
    }

    // ── Phase progress tokens ────────────────────────────────────────────────

    public static void PhaseStart(string name)
    {
        if (_isDriverMode)
            Console.WriteLine($"[PHASE_START] {name}");
        // interactive: suppress — result shown on PhaseEnd
    }

    public static void PhaseEnd(string name, long elapsedMs)
    {
        if (_isDriverMode)
            Console.WriteLine($"[PHASE_END] {name} {elapsedMs}");
        else
            AnsiConsole.MarkupLine(
                $"  [green]✓[/] [bold]{Markup.Escape(name)}[/] [dim]{elapsedMs}ms[/]");
    }

    public static void PrintTargetSummary(string chip, ulong freqHz)
    {
        var chipLabel = string.IsNullOrEmpty(chip) ? "unknown" : chip;
        var freqLabel = freqHz >= 1_000_000
            ? $"{freqHz / 1_000_000} MHz"
            : freqHz >= 1_000
                ? $"{freqHz / 1_000} kHz"
                : $"{freqHz} Hz";

        if (_isDriverMode)
            Console.WriteLine($"[BUILD_INFO] chip={chipLabel} freq={freqHz}");
        else
            AnsiConsole.MarkupLine(
                $"  [dim]Target:[/] [bold]{Markup.Escape(chipLabel)}[/] [dim]@[/] {Markup.Escape(freqLabel)}");
    }

    public static void BuildSuccess(string outputPath)
    {
        if (_isDriverMode)
            Console.WriteLine($"[BUILD_OK] {outputPath}");
        else
            AnsiConsole.MarkupLine(
                $"\n[bold green]Build successful[/] → [blue]{Markup.Escape(outputPath)}[/]");
    }

    public static void BuildFailed(string phase)
    {
        if (_isDriverMode)
            Console.WriteLine($"[BUILD_FAIL] {phase}");
        // interactive: Diagnostic already printed the error on stderr
    }

    // docs/rfcs/0004-arena-allocator.md: the driver cannot tell a runtime-sized
    // bytearray(n) from a foldable one by reading the sources, so the arena decision
    // is reported from here -- the only place the size has actually been folded.
    // NEEDS_ARENA precedes the missing-import UserError (the compile then fails);
    // ARENA_USED marks a successful arena lowering, which matters when the program
    // imported pymcu.arena itself: the compile succeeds, but the shipped module's
    // ARENA_SIZE is 0 until the driver stages the generated shim over it.
    public static void NeedsArena()
    {
        if (_isDriverMode)
            Console.WriteLine("[NEEDS_ARENA]");
    }

    public static void ArenaUsed()
    {
        if (_isDriverMode)
            Console.WriteLine("[ARENA_USED]");
    }

    // RFC 0014 decision 5: the driver does not scan source text to decide whether a
    // program needs the pymcu.strfmt or pymcu.round2 helpers -- only the IR generator
    // knows, from the call it just resolved, whether the value is a compile-time
    // constant (nothing to inject) or a run-time one (the helper module must be
    // loaded). NEEDS_STRFMT/NEEDS_ROUND2 precede the missing-import UserError the same
    // way NEEDS_ARENA does; the driver injects the import and compiles again.
    public static void NeedsStrfmt()
    {
        if (_isDriverMode)
            Console.WriteLine("[NEEDS_STRFMT]");
    }

    public static void NeedsRound2()
    {
        if (_isDriverMode)
            Console.WriteLine("[NEEDS_ROUND2]");
    }

    // RFC 0014 family 7 (phase 1): the remaining facts the driver used to scan source
    // text for, reported from the resolved binding instead. None of them fail the
    // compile -- they change what the driver stages around the program and then asks
    // for one more pass, the same way [ARENA_USED] does.
    //
    //   NeedsClocks -- an RP2350 program ended without a resolved clock_init() call;
    //     the driver injects the preamble the SDK runtime would have run.
    //   StdoutOwned -- the program constructs a UART itself, so the driver must not
    //     initialize a second one over stdout.
    public static void NeedsClocks()
    {
        if (_isDriverMode)
            Console.WriteLine("[NEEDS_CLOCKS]");
    }

    public static void StdoutOwned()
    {
        if (_isDriverMode)
            Console.WriteLine("[STDOUT_OWNED]");
    }

    // ── General logging ──────────────────────────────────────────────────────

    public static void Info(string component, string message)
    {
        if (_isDriverMode)
            Console.WriteLine($"[INFO] [{component}] {message}");
        else
            AnsiConsole.MarkupLine(
                $"[dim][[{Markup.Escape(component)}]][/] {Markup.Escape(message)}");
    }

    public static void Verbose(string component, string message)
    {
        if (!_isVerbose) return;

        if (_isDriverMode)
            Console.WriteLine($"[VERBOSE] [{component}] {message}");
        else
            AnsiConsole.MarkupLine(
                $"[dim][[{Markup.Escape(component)}]] {Markup.Escape(message)}[/]");
    }

    // Warnings always go to stderr — never pollute the stdout token stream.
    //
    // Under --error-format json they go through Diagnostic, like everything else on stderr:
    // a consumer promised machine-readable output gets it for warnings too. The human
    // renderings below are untouched, colour included, which is why this is a branch here
    // rather than one call in both modes.
    public static void Warning(string component, string message)
    {
        if (Diagnostic.Format == ErrorFormat.Json)
        {
            Diagnostic.Warning($"[{component}] {message}", code: "compiler-warning");
            return;
        }

        if (!Console.IsErrorRedirected)
            Console.Error.WriteLine($"\x1b[33m\u26a0\x1b[0m  [{component}] {message}");
        else
            Console.Error.WriteLine($"[Warning] [{component}] {message}");
    }

    // Instrumentation, not a diagnostic: the output of a debugging tool a compiler developer
    // switches on by hand (PYMCU_VERIFY_IR, PYMCU_RESOLVE_OBSERVE). Nothing here is about the
    // user's program, and nobody but the person who set the variable is meant to read it.
    //
    // So under --error-format json it does NOT go to stderr, as an object or otherwise:
    // stderr is the diagnostics of the program being compiled, and a verifier's chatter is
    // not one of them. It goes to stdout with the other Logger tokens, where the driver's
    // parser ignores what it does not recognise (a chain of startswith with no default
    // branch, commands/build.py), so a consumer that was not asking for it never sees it.
    //
    // In human mode it is byte-identical to a warning, which is what it was until now, so
    // nobody's PYMCU_VERIFY_IR workflow changes.
    public static void Tool(string component, string message)
    {
        if (Diagnostic.Format == ErrorFormat.Json)
        {
            Console.WriteLine($"[TOOL] [{component}] {message}");
            return;
        }

        if (!Console.IsErrorRedirected)
            Console.Error.WriteLine($"\x1b[33m\u26a0\x1b[0m  [{component}] {message}");
        else
            Console.Error.WriteLine($"[Warning] [{component}] {message}");
    }

    // Non-located errors (complement to Diagnostic.Report for positioned errors).
    public static void Error(string component, string message)
    {
        if (Diagnostic.Format == ErrorFormat.Json)
        {
            // An error, not a warning: this is the channel CompilerDriver uses for an
            // unhandled exception in a phase, and it fails the build.
            Diagnostic.ReportInternal($"[{component}] {message}", string.Empty);
            return;
        }

        Console.Error.WriteLine($"[{component}] Error: {message}");
    }
}

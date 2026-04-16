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
    public static void Warning(string component, string message)
    {
        if (!Console.IsErrorRedirected)
            Console.Error.WriteLine($"\x1b[33m\u26a0\x1b[0m  [{component}] {message}");
        else
            Console.Error.WriteLine($"[Warning] [{component}] {message}");
    }

    // Non-located errors (complement to Diagnostic.Report for positioned errors).
    public static void Error(string component, string message)
    {
        Console.Error.WriteLine($"[{component}] Error: {message}");
    }
}

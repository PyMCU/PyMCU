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

using PyMCU.Frontend;

namespace PyMCU.IR;

public class InlineContext
{
    public string ExitLabel { get; set; } = "";

    public Temporary? ResultTemp { get; set; }

    // Multi-return tuple: each result slot is a named variable "prefix.result_K"
    public List<string> ResultVars { get; set; } = [];
    public string CalleeName { get; set; } = "";
    public bool ResultAssigned { get; set; } = false;
}

public class ModuleScope
{
    public Dictionary<string, SymbolInfo> Globals { get; set; } = new();
    public Dictionary<string, DataType> MutableGlobals { get; set; } = new();
    public Dictionary<string, string> FunctionReturnTypes { get; set; } = new();
    public Dictionary<string, List<string>> FunctionParams { get; set; } = new();
    public Dictionary<string, FunctionDef> InlineFunctions { get; set; } = new();
}

public class LoopLabels
{
    public string ContinueLabel { get; set; } = "";
    public string BreakLabel { get; set; } = "";
}

public class FunctionEntry
{
    public string? Prefix { get; set; } = "";
    public FunctionDef Func { get; set; } = null!;
    public string SourceFile { get; set; } = "";
}
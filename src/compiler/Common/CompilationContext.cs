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

using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;

namespace PyMCU.Common;

public class CompilationContext(CompilerOptions options)
{
    public CompilerOptions Options { get; } = options;
    public DeviceConfig DeviceConfig { get; } = new();

    // Source Code State
    public string SourceCode { get; set; } = string.Empty;
    public List<string> SourceLines { get; } = [];
    public List<string> IncludePaths { get; } = ["." ];

    // AST and Module State
    public Dictionary<string, ProgramNode> ModuleCache { get; } = new();
    public Dictionary<string, ProgramNode> NamedModules { get; } = new();

    // Module names whose file lives inside the ENTRY file's own directory tree, so the user's
    // own modules as opposed to an installed distribution (the pymcu stdlib, the MicroPython
    // and CircuitPython compat layers). Only these have their module level executed on import:
    // the installed layers are written knowing that only the entry file's top level runs, and
    // several of them guard their top level on the target chip.
    public HashSet<string> ProjectModules { get; } = new();
    public Dictionary<string, List<string>> ModuleSourceLines { get; } = new();

    // Module name to the file it was actually loaded from. A diagnostic raised while lowering
    // an imported module has to name that module's file, and by IR generation the only thing
    // left of the module is a name and a prefix: the path the loader resolved is gone unless it
    // is kept here. Without it every such error was reported against the ENTRY file, at the
    // module's line number, which is a line of a different file (PyMCU#178).
    public Dictionary<string, string> ModulePaths { get; } = new();
    public HashSet<string> LoadingModules { get; } = [];
    public ProgramNode? RootAst { get; set; }

    // IR State
    public ProgramIR? IntermediateRepresentation { get; set; }

    public bool HasErrors { get; set; }

    // Set to true by BootstrapPhase after the target chip file has been loaded
    // and DeviceConfig.Arch/Chip are populated from it.
    // When true, PreScanVisitor treats any further device_info() calls as
    // module annotations to validate, not as target directives to apply.
    public bool IsTargetEstablished { get; set; }

    public List<ProgramNode> LinearImports { get; } = [];

    // Set by GcAnalysisPhase when GC_REF values are found in the IR.
    public bool ProgramNeedsGc { get; set; } = false;
}
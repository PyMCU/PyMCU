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
    public Dictionary<string, List<string>> ModuleSourceLines { get; } = new();
    public HashSet<string> LoadingModules { get; } = [];
    public ProgramNode? RootAst { get; set; }

    // IR State
    public ProgramIR? IntermediateRepresentation { get; set; }

    public bool HasErrors { get; set; }


    public List<ProgramNode> LinearImports { get; } = [];
}
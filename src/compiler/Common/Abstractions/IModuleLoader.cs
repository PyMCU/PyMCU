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

using PyMCU.Frontend;

namespace PyMCU.Common.Abstractions;

public interface IModuleLoader
{
    /// <param name="importedSymbols">
    /// The names of a `from &lt;module&gt; import a, b` — used only to word the not-found error,
    /// so that importing a name from a package that exists can say where the name really lives
    /// instead of claiming the package is missing.
    /// </param>
    ProgramNode LoadModule(string moduleName, string currentFilePath, CompilationContext context,
                           IReadOnlyList<string>? importedSymbols = null);
    string ResolveModulePath(string moduleName, string currentFilePath, CompilationContext context,
                             IReadOnlyList<string>? importedSymbols = null);
}
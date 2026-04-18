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

using PyMCU.Common;

namespace PyMCU.Pipeline;

// Template Method: defines the skeleton of Execute() so phases only implement Run().
// Guards (null checks) and error reporting are handled here once for all phases.
public abstract class CompilerPhaseBase : ICompilerPhase
{
    public abstract string Name { get; }

    public void Execute(CompilationContext context)
    {
        if (!Guard(context)) return;

        try
        {
            Run(context);
        }
        catch (CompilerError e)
        {
            Diagnostic.Report(e, context.SourceCode, context.Options.FilePath);
            context.HasErrors = true;
        }
        catch (Exception e)
        {
            Diagnostic.ReportInternal(e.Message, context.Options.FilePath);
            context.HasErrors = true;
        }
    }

    // Override to add preconditions. Return false to abort (sets HasErrors).
    protected virtual bool Guard(CompilationContext context) => true;

    protected abstract void Run(CompilationContext context);
}


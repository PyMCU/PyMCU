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
            // An error carrying its own File is IN another module (an import, most often).
            // Report it against that file so the caret lands on the line that has to change,
            // and fall back to the entry file if the source cannot be read.
            string file = context.Options.FilePath;
            string source = context.SourceCode;
            if (!string.IsNullOrEmpty(e.File) && e.File != file)
            {
                try
                {
                    source = System.IO.File.ReadAllText(e.File);
                    file = e.File;
                }
                catch (IOException) { /* keep the entry file's source */ }
                catch (UnauthorizedAccessException) { /* keep the entry file's source */ }
            }

            Diagnostic.Report(e, source, file);
            context.HasErrors = true;
        }
        catch (Exception e)
        {
            Diagnostic.ReportInternal(e, context.Options.FilePath);
            context.HasErrors = true;
        }
    }

    // Override to add preconditions. Return false to abort (sets HasErrors).
    protected virtual bool Guard(CompilationContext context) => true;

    protected abstract void Run(CompilationContext context);
}


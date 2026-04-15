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
using PyMCU.Frontend;

namespace PyMCU.Pipeline.Phases;

public class ParsingPhase : ICompilerPhase
{
    public string Name => "Lexical & Syntax Analysis";

    public void Execute(CompilationContext context)
    {
        try
        {
            var lexer = new Lexer(context.SourceCode);
            var tokens = lexer.Tokenize();

            var parser = new Parser(tokens);
            context.RootAst = parser.ParseProgram();
        }
        catch (CompilerError e)
        {
            Diagnostic.Report(e, context.SourceCode, context.Options.FilePath);
            context.HasErrors = true;
        }
    }
}
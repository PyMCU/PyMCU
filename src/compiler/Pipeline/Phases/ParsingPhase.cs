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

public class ParsingPhase : CompilerPhaseBase
{
    public override string Name => "Lexical & Syntax Analysis";

    protected override void Run(CompilationContext context)
    {
        // PYMCU_PY_PARSER=1 routes the entry file through CPython's parser. The AST is the
        // contract, so everything after this phase is identical either way -- which is what
        // makes the two comparable by building the corpus both ways.
        if (PythonAstReader.Enabled)
        {
            context.RootAst = PythonAstReader.ParseSource(context.SourceCode, context.Options.FilePath);
            DuplicateDefinitionCheck.Check(context.RootAst, context.Options.FilePath);
            return;
        }

        var lexer = new Lexer(context.SourceCode);
        var tokens = lexer.Tokenize();

        var parser = new Parser(tokens);
        context.RootAst = parser.ParseProgram();
        DuplicateDefinitionCheck.Check(context.RootAst, context.Options.FilePath);

        // PYMCU_DUMP_AST=<path> writes the tree in the translator's JSON shape, so the two
        // front ends can be compared node by node instead of only through the firmware they
        // produce. A divergence in a branch nothing calls never reaches code generation.
        string? dumpTo = Environment.GetEnvironmentVariable("PYMCU_DUMP_AST");
        if (!string.IsNullOrEmpty(dumpTo))
            File.WriteAllText(dumpTo, AstJsonWriter.Write(context.RootAst));
    }
}
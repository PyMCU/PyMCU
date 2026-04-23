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
using PyMCU.Infrastructure;
using PyMCU.Infrastructure.Cli;

namespace PyMCU;

public static class Program
{
    public static int Main(string[] args)
    {
        var rootCommand = CompilerCliBuilder.BuildRootCommand(RunCompiler);

        return rootCommand.Parse(args).Invoke();
    }

    private static int RunCompiler(CompilerOptions options)
    {
        var moduleLoader = new FileSystemModuleLoader();
        var graphBuilder = new DependencyGraphBuilder(moduleLoader);

        // When --emit-ir is specified, replace the backend phase with the IR serializer.
        var backendPhase = string.IsNullOrEmpty(options.EmitIrPath)
            ? (Pipeline.ICompilerPhase)new Pipeline.Phases.BackendPhase()
            : new Pipeline.Phases.IrSerializerPhase();

        var driver = new Pipeline.CompilerDriver()
            .AddPhase(new Pipeline.Phases.InitializationPhase())
            .AddPhase(new Pipeline.Phases.BootstrapPhase())
            .AddPhase(new Pipeline.Phases.ParsingPhase())
            .AddPhase(new Pipeline.Phases.FrontendResolutionPhase(moduleLoader, graphBuilder))
            .AddPhase(new Pipeline.Phases.IrGenerationPhase())
            .AddPhase(backendPhase);

        return driver.Run(options);
    }
}
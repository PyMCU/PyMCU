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
using PyMCU.IR;
using PyMCU.IR.IRGenerator;

namespace PyMCU.Pipeline.Phases;

public class IrGenerationPhase : CompilerPhaseBase
{
    public override string Name => "IR Generation";

    protected override bool Guard(CompilationContext context)
    {
        if (context.RootAst != null) return true;
        context.HasErrors = true;
        return false;
    }

    protected override void Run(CompilationContext context)
    {
        DataTypeExtensions.SetPointerWidth(context.DeviceConfig.PointerWidth);

        var irGen = new IRGenerator();
        // The entry file has no path inside IR generation -- that is how it is told apart from
        // an imported module -- so a diagnostic that cites one of its lines in its own sentence
        // has nothing to name the file with. Hand it the name this compilation was invoked on
        // (#303).
        if (!string.IsNullOrEmpty(context.Options.FilePath))
            irGen.EntryFileName = System.IO.Path.GetFileName(context.Options.FilePath);
        irGen.LibraryMode = context.Options.Library;
        // RFC 0008: --embed NAME=PATH pairs build the compile-time romfs table.
        irGen.EmbeddedFiles = context.Options.Embeds;
        irGen.WidthSeeds = context.WidthSeeds;
        ProgramIR ir;
        try
        {
            ir = irGen.Generate(context.RootAst!, context.NamedModules, context.DeviceConfig,
                context.SourceLines, context.ModuleSourceLines, context.ProjectModules,
                context.ModulePaths);
        }
        catch (CompilerError) when (context.WidthSeeds.Grew && context.RerunAllowed)
        {
            // Lowered with a slot too narrow, the program may fail for that reason alone
            // (a comparison folded against the truncated width, say). The widened run is the
            // one that decides; if the error is real it comes back there.
            context.RerunWithWiderSlots = true;
            return;
        }

        // An unannotated slot was narrower than a value stored into it: the driver compiles
        // again with the slot widened, so this IR goes no further.
        if (context.WidthSeeds.Grew && context.RerunAllowed)
        {
            context.RerunWithWiderSlots = true;
            return;
        }

        // One width per variable name, whether or not the optimizer runs: the backend sizes a
        // name once, so two widths for one name is a miscompile, not a missed optimisation.
        Optimizer.UnifyVariableWidths(ir);
        Verifier.Check(ir, "unify-widths");

        // PGO: a --profile JSON is advisory input to the optimizer. A file that
        // does not parse degrades to an ordinary build with a warning -- the
        // profile must never be what makes a build fail.
        PgoProfile? pgoProfile = null;
        if (!string.IsNullOrEmpty(context.Options.ProfilePath))
        {
            pgoProfile = PgoProfile.Load(context.Options.ProfilePath);
            if (pgoProfile == null)
                Logger.Warning("pgo",
                    $"could not read profile '{context.Options.ProfilePath}' -- " +
                    "building without it.");
        }

        // PYMCU_NO_OPT=1 skips the optimizer: lets a miscompile be bisected to the
        // IR generator (raw IR wrong) vs an optimizer pass (raw IR right).
        var optimized = Environment.GetEnvironmentVariable("PYMCU_NO_OPT") == "1"
            ? ir
            : Optimizer.Optimize(ir, pgoProfile);
        Verifier.Check(optimized, "optimize");

        // A library's exports are its whole interface, so one that did not come out as a
        // symbol is refused here, at its `def`, rather than left missing for a linker to find.
        if (context.Options.Library)
            RefuseExportsWithoutASymbol(context.RootAst!, optimized, irGen.ExpandedInlineFunctions);

        // CanFail analysis runs after optimization so that dead-code-eliminated
        // functions and cloned bodies are the final IR seen by the backend.
        CanFailAnalyzer.Analyze(optimized, libraryMode: context.Options.Library);

        // Guard every unguarded CanFail call so an uncaught error halts (top-level) or re-raises
        // to the caller, instead of being silently swallowed by the next happy-path CLT.
        CanFailAnalyzer.InsertUncaughtPropagation(optimized, libraryMode: context.Options.Library);
        optimized.IsLibrary = context.Options.Library;
        Verifier.Check(optimized, "canfail");

        context.IntermediateRepresentation = optimized;
    }

    /// <summary>
    /// `--library` roots every top-level function of the entry file, and a function with no
    /// subroutine ABI (it returns a tuple, takes a class instance...) is registered for expansion
    /// at its call sites instead. A library has none, so the function was expanded nowhere and
    /// emitted nowhere, with BUILD_OK and a .mir that simply lacked it (PyMCU#365 was one
    /// spelling, a `bytes` parameter; a tuple return another). Every function that was marked as
    /// an export and is not a symbol of the program is an error at its definition.
    /// </summary>
    private static void RefuseExportsWithoutASymbol(
        Frontend.ProgramNode ast, ProgramIR program,
        IReadOnlySet<(string Name, int Line)> expanded)
    {
        var emitted = program.Functions.Select(f => f.Name).ToHashSet();
        foreach (var fn in ast.Functions)
        {
            if (!fn.IsExportC || fn.IsInline || fn.IsInterrupt) continue;
            if (emitted.Contains(fn.Name)) continue;

            // A helper another function of the library calls is expanded into its callers, which
            // is how a function with no subroutine form is meant to be used: it has no symbol
            // and needs none. Only a function no call was ever expanded from has vanished, and
            // that is the bug.
            if (expanded.Contains((fn.Name, fn.Line))) continue;

            string why = fn.ReturnType.StartsWith("tuple", StringComparison.Ordinal)
                ? "it returns a tuple, which has no subroutine form (several values cannot come back through one return)"
                : fn.Params.FirstOrDefault(p => p.Type.Length > 0
                        && DataTypeExtensions.StringToDataType(p.Type) == DataType.UNKNOWN
                        && p.Type is not ("bytearray" or "str" or "const[str]")
                        && !p.Type.StartsWith("ptr", StringComparison.Ordinal)) is { } cp
                    ? $"its parameter '{cp.Name}: {cp.Type}' is read as a class instance, whose fields live in the caller's frame"
                    : "the compiler expanded it at its call sites or dropped it, and left no subroutine";
            throw new ArchitectureError(
                $"function '{fn.Name}' is an export of this library but cannot be emitted as a symbol: "
                + $"{why}. A library has no call site to expand it at. Mark it @inline if it is a "
                + "template for the other functions, or give it a form with a subroutine ABI "
                + "(scalar, 'bytearray' or 'ptr' parameters and a scalar or no return).",
                fn.Line, fn.Column);
        }
    }
}

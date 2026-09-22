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
using PyMCU.Common.Abstractions;
using PyMCU.Frontend;
using PyMCU.Pipeline.Phases.Processors;

namespace PyMCU.Pipeline.Phases;

public class FrontendResolutionPhase(
    IModuleLoader moduleLoader,
    IDependencyGraphBuilder graphBuilder) : CompilerPhaseBase
{
    public override string Name => "Semantic & Dependency Resolution";

    protected override bool Guard(CompilationContext context)
    {
        if (context.RootAst != null) return true;
        context.HasErrors = true;
        return false;
    }

    protected override void Run(CompilationContext context)
    {
        // The entry file is parsed by ParsingPhase, not by the module loader, so its own
        // relative imports are rewritten here before anything walks them.
        RelativeImportResolver.Rewrite(context.RootAst!, context.Options.FilePath, context.IncludePaths);

        var resolutionOrder = graphBuilder
            .Build(context.RootAst!, context.Options.FilePath, context)
            .GetTopologicalSort();

        // Build the processor chain once, bound to the shared DeviceConfig instance.
        IAstProcessor[] processors =
        [
            new PreScanProcessor(new PreScanVisitor(context.DeviceConfig)),
            new DeviceConfigFallbackProcessor(),
            new ConditionalCompilationProcessor(new ConditionalCompilator(context.DeviceConfig)),
        ];

        foreach (var node in resolutionOrder)
        {
            foreach (var processor in processors)
                processor.Process(node, context);
        }

        LoadPostConditionalModulesRecursive(processors, context);

        // Last, because it needs every module folded AND loaded: a facade binds its names in
        // the winning branch of an `if __CHIP__.name == ...`, and asking before that has run
        // would find none of them.
        ImportedNameCheck.Check(context);
    }

    // Recursively loads and processes any modules that conditional compilation revealed
    // after the initial graph was built. This ensures transitive imports are fully resolved.
    // For example: _lcd/gpio.py imports time.py → time.py's inline functions must be registered.
    private void LoadPostConditionalModulesRecursive(
        IAstProcessor[] processors,
        CompilationContext context)
    {
        const int maxIterations = 10;
        var processedModules = new HashSet<string>(context.NamedModules.Keys);
        var iteration = 0;

        Logger.Verbose("FrontendResolution",
            $"Starting post-conditional module loading (initial modules: {processedModules.Count})");

        while (iteration++ < maxIterations)
        {
            var newModules = new List<ProgramNode>();

            // Create snapshot of current modules to avoid modification-during-iteration
            var currentAsts = context.NamedModules.Select(m => new { Name = m.Key, Node = m.Value }).ToList();
            if (context.RootAst != null)
                currentAsts.Add(new { Name = "__main__", Node = context.RootAst });

            Logger.Verbose("FrontendResolution",
                $"Iteration {iteration}: Scanning {currentAsts.Count} modules for new imports");

            // Scan all currently processed modules for imports
            foreach (var item in currentAsts)
            {
                foreach (var imp in item.Node.Imports)
                {
                    if (BuiltinModuleNames.IsBuiltin(imp.ModuleName)) continue;

                    // A star promoted out of a compile-time branch reaches this loop with its
                    // module already loaded, so expand it before the already-processed skip.
                    if (StarImportExpander.IsStar(imp)
                        && context.NamedModules.TryGetValue(imp.ModuleName, out var loaded))
                        StarImportExpander.Expand(imp, loaded);

                    if (processedModules.Contains(imp.ModuleName)) continue;

                    Logger.Verbose("FrontendResolution",
                        $"Discovered new import: {imp.ModuleName} (from {item.Name})");

                    // Load the module if not yet loaded
                    if (!context.NamedModules.ContainsKey(imp.ModuleName))
                        moduleLoader.LoadModule(imp.ModuleName, context.Options.FilePath, context, imp.Symbols);

                    var importedModule = context.NamedModules[imp.ModuleName];

                    // A module discovered here missed the optional-import marking
                    // DependencyGraphBuilder does on dequeue, so a `try: from micropython
                    // import const / except ImportError:` inside one (the crickit.py idiom
                    // behind adafruit_seesaw's chip-id pinmaps) would keep its handler --
                    // and the handler's `def const` then fails as a nested function. Mark
                    // and resolve them the same way the graph does, so the fold the
                    // processors run next picks the branch the loader actually found.
                    foreach (var opt in ConditionalImportExtractor.Extract(importedModule, context.DeviceConfig))
                    {
                        if (!opt.IsOptional || BuiltinModuleNames.IsBuiltin(opt.ModuleName)) continue;
                        try
                        {
                            moduleLoader.LoadModule(opt.ModuleName, context.Options.FilePath, context, opt.Symbols);
                            if (!importedModule.Imports.Contains(opt))
                                importedModule.Imports.Add(opt);
                        }
                        catch (CompilerError)
                        {
                            opt.OptionalLoadFailed = true;
                            foreach (var fb in opt.FallbackImports)
                                if (!importedModule.Imports.Contains(fb))
                                    importedModule.Imports.Add(fb);
                        }
                    }

                    StarImportExpander.Expand(imp, importedModule);
                    if (processedModules.Add(imp.ModuleName))
                        newModules.Add(importedModule);
                }
            }

            // No new modules discovered → we're done
            if (newModules.Count == 0)
            {
                Logger.Verbose("FrontendResolution",
                    $"Iteration {iteration}: No new modules found, convergence reached");
                break;
            }

            Logger.Verbose("FrontendResolution",
                $"Iteration {iteration}: Processing {newModules.Count} new module(s)");

            // Process all newly discovered modules through the full processor pipeline
            foreach (var module in newModules)
            {
                foreach (var processor in processors)
                    processor.Process(module, context);
            }
        }

        if (iteration >= maxIterations)
            throw new CompilerError("ImportError",
                "Exceeded maximum iterations while loading transitive imports. Possible circular dependency.", 0, 0);

        Logger.Verbose("FrontendResolution",
            $"Post-conditional module loading complete (total modules: {context.NamedModules.Count})");
    }
}
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
using PyMCU.Common.Models;
using PyMCU.Frontend;

namespace PyMCU.IR.IRGenerator;

public partial class IRGenerator
{
    // Maps AST BinaryOp to IR BinaryOp (only for ops that have IR equivalents)
    private static BinaryOp MapBinaryOp(Frontend.BinaryOp op) => op switch
    {
        Frontend.BinaryOp.Add => BinaryOp.Add,
        Frontend.BinaryOp.Sub => BinaryOp.Sub,
        Frontend.BinaryOp.Mul => BinaryOp.Mul,
        Frontend.BinaryOp.Div => BinaryOp.Div,
        Frontend.BinaryOp.FloorDiv => BinaryOp.FloorDiv,
        Frontend.BinaryOp.Mod => BinaryOp.Mod,
        Frontend.BinaryOp.Pow => BinaryOp.Pow,
        Frontend.BinaryOp.Equal => BinaryOp.Equal,
        Frontend.BinaryOp.NotEqual => BinaryOp.NotEqual,
        Frontend.BinaryOp.Less => BinaryOp.LessThan,
        Frontend.BinaryOp.Greater => BinaryOp.GreaterThan,
        Frontend.BinaryOp.LessEq => BinaryOp.LessEqual,
        Frontend.BinaryOp.GreaterEq => BinaryOp.GreaterEqual,
        Frontend.BinaryOp.BitAnd => BinaryOp.BitAnd,
        Frontend.BinaryOp.BitOr => BinaryOp.BitOr,
        Frontend.BinaryOp.BitXor => BinaryOp.BitXor,
        Frontend.BinaryOp.LShift => BinaryOp.LShift,
        Frontend.BinaryOp.RShift => BinaryOp.RShift,
        _ => throw new Exception($"BinaryOp {op} has no IR equivalent"),
    };

    // Maps AST UnaryOp to IR UnaryOp
    private static UnaryOp MapUnaryOp(Frontend.UnaryOp op) => op switch
    {
        Frontend.UnaryOp.Negate => UnaryOp.Neg,
        Frontend.UnaryOp.Not => UnaryOp.Not,
        Frontend.UnaryOp.BitNot => UnaryOp.BitNot,
        _ => throw new Exception($"UnaryOp {op} has no IR equivalent"),
    };

    // Maps AST AugOp to IR BinaryOp
    private static BinaryOp MapAugOp(AugOp op) => op switch
    {
        AugOp.Add => BinaryOp.Add,
        AugOp.Sub => BinaryOp.Sub,
        AugOp.Mul => BinaryOp.Mul,
        AugOp.Div => BinaryOp.Div,
        AugOp.FloorDiv => BinaryOp.FloorDiv,
        AugOp.Mod => BinaryOp.Mod,
        AugOp.BitAnd => BinaryOp.BitAnd,
        AugOp.BitOr => BinaryOp.BitOr,
        AugOp.BitXor => BinaryOp.BitXor,
        AugOp.LShift => BinaryOp.LShift,
        AugOp.RShift => BinaryOp.RShift,
        _ => throw new Exception($"AugOp {op} has no IR equivalent"),
    };

    private bool IsConstType(string type)
    {
        return type == "const" || (type.StartsWith("const[") && type.EndsWith("]"));
    }

    private Temporary MakeTemp(DataType type = DataType.UINT8)
    {
        // Inside an inline expansion the temp is named inline{serial}_d{depth}_t{k}:
        // the frame's unique serial keeps the full name distinct from every sibling
        // expansion's, and the allocator's canonical strip folds it onto "d{depth}_t{k}"
        // -- shared with every other expansion at that depth. That merge is safe
        // because a temp's live range stays inside its own expansion and two
        // same-depth expansions never overlap (a nested expansion is strictly
        // deeper). The adafruit_ht16k33 matrix wing minted ~1800 unprefixed tmp_N in
        // main alone and needed 4KB of static data on a 2KB chip.
        // A name that outlives its frame must come from MakeGlobalTemp instead.
        if (inlineStack.Count > 0 && currentInlinePrefix.Length > 0)
        {
            string tag = InlineSerialTag();
            return new Temporary($"{tag}_d{inlineDepth}_t{inlineStack[^1].TempNext++}", type);
        }
        return new Temporary($"tmp_{tempCounter++}", type);
    }

    // A temp the caller keeps reading after the expansion's frame is gone (a lazily
    // minted ResultTemp/ResultTagTemp): it must NOT carry the frame prefix, because
    // the allocator folds every `inlineN_f_tK` onto the canonical `f_tK` slot --
    // two expansions whose results are both live would silently share one address.
    private Temporary MakeGlobalTemp(DataType type = DataType.UINT8)
        => new($"tmp_{tempCounter++}", type);

    // The leading "inline{serial}" token of the current expansion's prefix: the
    // part before the first '.' or '_' separator ("inline4.describe." / "inline4_f_"
    // both yield "inline4").
    private string InlineSerialTag()
    {
        int i = 6;
        while (i < currentInlinePrefix.Length && char.IsDigit(currentInlinePrefix[i])) i++;
        return currentInlinePrefix[..i];
    }

    private static string DataTypeToSuffixStr(DataType dt)
    {
        return dt switch
        {
            DataType.UINT8 => "uint8",
            DataType.UINT16 => "uint16",
            DataType.UINT32 => "uint32",
            DataType.INT8 => "int8",
            DataType.INT16 => "int16",
            DataType.INT32 => "int32",
            // Without this a FLOAT argument spelled itself "uint8", so `f(2.5)` could never
            // exact-match `f(x: float)` and every float call fell through to the arity
            // fallback (PyMCU#182).
            DataType.FLOAT => "float",
            _ => "uint8",
        };
    }

    public static string BuildOverloadSuffix(List<Param> parameters)
    {
        string suffix = "";
        bool first = true;
        foreach (var p in parameters)
        {
            if (p.Name == "self") continue;
            if (!first) suffix += "_";
            first = false;
            suffix += string.IsNullOrEmpty(p.Type) ? "uint8" : p.Type;
        }

        return string.IsNullOrEmpty(suffix) ? "void" : suffix;
    }

    private DataType InferExprType(Expression expr)
    {
        switch (expr)
        {
            case FloatLiteral:
                return DataType.FLOAT;
            case BooleanLiteral:
                break;
            case IntegerLiteral il:
                if (il.Value < short.MinValue) return DataType.INT32;
                if (il.Value < sbyte.MinValue) return DataType.INT16;
                if (il.Value < 0) return DataType.INT8;
                if (il.Value <= byte.MaxValue) break;
                if (il.Value <= ushort.MaxValue) return DataType.UINT16;
                return DataType.UINT32;
            case VariableExpr varExpr:
            {
                // Try the same qualifications the read side uses: the inline prefix, then the
                // enclosing function, then the bare name. Only the first was tried, so a plain
                // function LOCAL was never found -- it is registered as `main.x`, not `x` -- and
                // every such argument silently inferred UINT8. `x: float` then spelled itself
                // "uint8" for overload selection, so `math.floor(x)` could not exact-match
                // `floor(x: float)` and fell through to the arity fallback (PyMCU#182).
                foreach (var start in new[]
                {
                    string.IsNullOrEmpty(currentInlinePrefix) ? null : currentInlinePrefix + varExpr.Name,
                    string.IsNullOrEmpty(currentFunction) ? null : currentFunction + "." + varExpr.Name,
                    varExpr.Name,
                })
                {
                    if (start == null) continue;
                    var key = start;
                    for (var i = 0; i < 20; ++i)
                    {
                        if (variableTypes.TryGetValue(key, out var type)) return type;
                        if (variableAliases.TryGetValue(key, out var alias))
                            key = alias;
                        else
                            break;
                    }
                }

                break;
            }
            case BinaryExpr bin:
            {
                var lt = InferExprType(bin.Left);
                var rt = InferExprType(bin.Right);
                return (DataType)Math.Max((int)lt, (int)rt);
            }
            case IndexExpr ix when ix.Target is VariableExpr ixv:
            {
                // `xs[0]` inside a literal: the element type of the indexed list is
                // the read's type -- `[p[0], 0]` infers uint16, not the uint8 a
                // missed lookup would default to.
                string ixKey = ResolveListVarQualified(ixv.Name);
                if (ixKey.Length > 0 && listVarElemTypes.TryGetValue(ixKey, out var ixElem))
                    return ixElem;
                break;
            }
        }

        return DataType.UINT8;
    }

    private string MakeLabel()
    {
        return $"L_{labelCounter++}";
    }

    private void Emit(Instruction inst)
    {
        // Value-tracking aliases (a = b for plain scalars) are only valid straight-line:
        // at a control-flow join the aliased copy may not have executed on every path, so
        // following it would read (or index by!) the wrong variable. Clear them at labels,
        // exactly like PropagateCopies clears var-consts. Structural aliases (self, params,
        // nonlocal) are not value-tracking and survive.
        if (inst is Label && valueTrackingAliases.Count > 0)
        {
            foreach (var k in valueTrackingAliases) variableAliases.Remove(k);
            valueTrackingAliases.Clear();
        }
        currentInstructions.Add(inst);
    }

    private void PropagateCtState(string src, string dst)
    {
        if (instanceClasses.TryGetValue(src, out var cls))
        {
            instanceClasses[dst] = cls;
            virtualInstances.Add(dst);
        }

        // Copy every descendant key. Nested instance fields are flattened two ways depending
        // on the access path: dotted ("inst._pin") and underscore-joined ("inst__pin", from
        // self._pin field access). Both must follow the instance to its new binding (e.g. a
        // for-in loop variable) or a nested method like self._pin.mode() loses its class and
        // degrades to an undefined CALL.
        void CopyDescendants<T>(Dictionary<string, T> map, string sep)
        {
            string sp = src + sep, dp = dst + sep;
            foreach (var kv in map.Where(kv => kv.Key.StartsWith(sp, StringComparison.Ordinal)).ToList())
                map[dp + kv.Key[sp.Length..]] = kv.Value;
        }

        foreach (var sep in new[] { ".", "_" })
        {
            CopyDescendants(constantVariables, sep);
            CopyDescendants(strConstantVariables, sep);
            CopyDescendants(floatConstantVariables, sep);
            CopyDescendants(constantAddressVariables, sep);
            CopyDescendants(instanceClasses, sep);
            // A field holding a RUN-TIME value has no constant to copy: it is an alias onto
            // the variable that holds it. Leaving those behind bound the loop variable to a
            // field name nothing ever wrote, so `for o in objs: o.g()` read zero.
            CopyDescendants(variableAliases, sep);
        }
    }

    /// <summary>
    /// Bind the unrolled loop variable to the instance at <paramref name="src"/> so a write
    /// through the loop name lands on the SAME object the element named. Copying the fields
    /// into a fresh binding made <c>for pin in (a, b): pin.direction = 1</c> write a copy
    /// that CleanCtState then discarded, leaving the original pins at 0 (inherited-property).
    /// Follow aliases: a hoisted <c>(a, b)</c> files <c>__ctseqN__k</c> as an alias of
    /// <c>a</c>, and the loop variable has to name <c>a</c>, not the hoist slot.
    /// </summary>
    private void BindLoopVarToInstance(string src, string dst)
    {
        string origin = src;
        var seen = new HashSet<string> { origin };
        while (variableAliases.TryGetValue(origin, out var next)
               && !string.IsNullOrEmpty(next) && seen.Add(next))
            origin = next;
        variableAliases[dst] = origin;
        if (instanceClasses.TryGetValue(origin, out var cls) && cls != null)
            instanceClasses[dst] = cls;
        else if (instanceClasses.TryGetValue(src, out cls) && cls != null)
            instanceClasses[dst] = cls;
    }

    /// <summary>
    /// Bind <paramref name="dst"/> to the instance at <paramref name="src"/> for one unrolled
    /// iteration: carry the compile-time state across, then COPY the fields that live in a
    /// run-time variable. Those have nothing in the compile-time maps to carry, so without the
    /// copies the loop variable named fields that nothing had ever written and every read came
    /// back zero.
    /// </summary>
    private void BindInstanceForIteration(string src, string dst)
    {
        PropagateCtState(src, dst);

        if (!instanceClasses.TryGetValue(src, out var cls) || cls == null) return;
        if (!classFieldLayout.TryGetValue(cls, out var layout)) return;

        foreach (var (field, type, _) in layout)
        {
            string from = src + "_" + field, to = dst + "_" + field;
            if (constantVariables.ContainsKey(to) || variableAliases.ContainsKey(to)) continue;
            if (!classFieldLayout.ContainsKey(type)) // a nested instance is carried, not copied
            {
                DataType dt = DataTypeExtensions.StringToDataType(type);
                variableTypes[to] = dt;
                Emit(new Copy(new Variable(from, dt), new Variable(to, dt)));
            }
        }
    }

    private void CleanCtState(string dst)
    {
        instanceClasses.Remove(dst);

        void RemoveDescendants<T>(Dictionary<string, T> map, string sep)
        {
            string dp = dst + sep;
            foreach (var k in map.Keys.Where(k => UnderDst(k, dp)).ToList())
                map.Remove(k);
        }

        void RemoveDescendantsSet(HashSet<string> set, string sep)
        {
            string dp = dst + sep;
            foreach (var k in set.Where(k => UnderDst(k, dp)).ToList())
                set.Remove(k);
        }

        static bool UnderDst(string k, string dp)
        {
            if (!k.StartsWith(dp, StringComparison.Ordinal)) return false;
            // `__ctseqN` is minted from a counter no later expansion
            // re-uses, so its elements can never be stale state: they
            // are either escaped storage (a field aliases them) or
            // unreferenced. Removing them broke `self.segments = [Pin..
            // for ..]` the moment a second __init__ shared the prefix.
            // Two shapes must both be recognized: `scope.__ctseqN` (the
            // text right after dp, or after the last '.') and the
            // underscore-joined super() scheme `inline4___init_____ctseqN`.
            // A dst ending in '_' (the discard name `_` qualifies to it)
            // makes dp end in "__", which ate `__ctseqN__k` one underscore
            // deep under the remainder-only check.
            if (k.AsSpan(dp.Length).StartsWith("__ctseq", StringComparison.Ordinal)
                || IsCtSeqKey(k)) return false;
            return true;
        }

        static bool IsCtSeqKey(string k)
        {
            // `scope.__ctseqN` under the dotted scheme; under the underscore scheme the
            // element is `scope___ctseqN` -- a "__ctseq" segment split out on '_'
            // boundaries would split inside the name itself, so look for the marker
            // right after a '.' or '_' separator instead.
            for (int i = 0; i <= k.Length - 7; ++i)
            {
                if (k[i] != '_' && k[i] != '.') continue;
                if (k.AsSpan(i + 1).StartsWith("__ctseq", StringComparison.Ordinal)) return true;
            }
            return k.StartsWith("__ctseq", StringComparison.Ordinal);
        }

        foreach (var sep in new[] { ".", "_" })
        {
            RemoveDescendants(constantVariables, sep);
            RemoveDescendants(strConstantVariables, sep);
            RemoveDescendants(floatConstantVariables, sep);
            RemoveDescendants(variableAliases, sep);
            RemoveDescendants(constantAddressVariables, sep);
            RemoveDescendants(instanceClasses, sep);
            // Heap-list element types are callee-local state too: a `pulses =
            // list(pulses)` rebind in one expansion registers the param name
            // here, and the next expansion's ResolveListVarQualified then
            // resolves the param to the stale slot -- before its own alias
            // binds -- and reads whatever the dead expansion left in it.
            RemoveDescendants(listVarElemTypes, sep);
            RemoveDescendants(listInnerElemTypes, sep);
            RemoveDescendantsSet(tupleBoundNames, sep);
            RemoveDescendantsSet(promotableEmptyLists, sep);
            RemoveDescendantsSet(promotedEmptyLists, sep);
            RemoveDescendantsSet(noneValuedNames, sep);
            // The "different texts on different paths" mark is callee-local state
            // too: an earlier expansion at this depth filed it under this same
            // prefix, and a str parameter freshly bound here must not refuse a
            // run-time argument on texts the dead scope saw (draw_char(char)
            // marked inline2.draw_char.char once per unrolled iteration, then a
            // later expansion handing a run-time char code refused ord(char)).
            RemoveDescendants(multiStrVariables, sep);
        }
    }

    // Carry the chip file's declared geometry into the IR, which is the only channel
    // a backend has to it. DeviceConfig spells "not declared" as 0 for historical
    // reasons; DeviceGeometry spells it as null, so a backend that needs a number it
    // was never given fails the build instead of compiling for a chip with no flash.
    private static DeviceGeometry GeometryOf(DeviceConfig config) => new()
    {
        Chip       = !string.IsNullOrEmpty(config.Chip) ? config.Chip : config.TargetChip,
        RamSize    = config.RamSize    > 0 ? config.RamSize    : null,
        FlashSize  = config.FlashSize  > 0 ? config.FlashSize  : null,
        EepromSize = config.EepromSize > 0 ? config.EepromSize : null,
    };

    /// Names the file an error was raised IN when the error did not name one itself.
    ///
    /// `UserError` attaches `File`; the typed classes (`ValueError`, `TypeError`, ...) are
    /// constructed directly and none of them does, so their line and column came from the AST
    /// node, which belongs to whichever module the code is in, while the renderer fell back to
    /// the entry file. Two halves from two files, which is the pair #227 fixed for the
    /// `UserError` path and this one never had. Issue #230.
    ///
    /// Here rather than at each of the 27 sites: `currentSourcePath` is a field, so unwinding
    /// does not restore it, and at this catch it still holds the value it had at the throw. One
    /// place covers every direct throw inside IR generation, including one added later.
    ///
    /// `LocationIsFinal` is the opt-out, and it has to be a property rather than the exception
    /// class: the deliberate site in `VisitRaise` throws `ArchitectureError`, which is also one
    /// of the compiler-generated classes, so nothing about the type distinguishes them.
    ///
    /// Not covered, and not reachable from here: the three in `Optimizer.cs` and the two in
    /// `CanFailAnalyzer.cs`, which run in later phases.
    public ProgramIR Generate(
        ProgramNode mainAst,
        Dictionary<string, ProgramNode> importedModules,
        DeviceConfig config,
        List<string>? sourceLines = null,
        Dictionary<string, List<string>>? moduleSourceLines = null,
        HashSet<string>? projectModules = null,
        Dictionary<string, string>? modulePaths = null)
    {
        try
        {
            return GenerateCore(mainAst, importedModules, config, sourceLines,
                                moduleSourceLines, projectModules, modulePaths);
        }
        catch (PyMCU.Common.CompilerError e)
            when (!e.LocationIsFinal && e.File == null && !string.IsNullOrEmpty(currentSourcePath))
        {
            throw new PyMCU.Common.CompilerError(
                e.TypeName, e.Message, e.Line, e.Column, e.Length) { File = currentSourcePath };
        }
    }

    /// <summary>
    /// The keys of <paramref name="importedModules"/>, ordered so every module comes after
    /// every OTHER module it directly imports (that is itself in the map). Insertion order in
    /// the map is BFS-by-discovery, not dependency order, and scanning a module before a base
    /// class it inherits from is scanned leaves that base's field layout and methods
    /// unregistered -- see the call site.
    ///
    /// A depth-first post-order walk: a module is appended only after every import it names
    /// has been visited, so each of its own dependencies is already in the output by the time
    /// it is. An import cycle cannot make this loop (`visiting` catches it and the edge is
    /// just not waited on), and a module absent from the map (the standard library, a package
    /// not part of THIS scan) is not a node here, so its imports are silently skipped rather
    /// than resolved.
    /// </summary>
    private static List<string> TopologicallySortModules(Dictionary<string, ProgramNode> importedModules)
    {
        var order = new List<string>(importedModules.Count);
        var visited = new HashSet<string>();
        var visiting = new HashSet<string>();

        void Visit(string modName)
        {
            if (visited.Contains(modName) || !importedModules.TryGetValue(modName, out var ast)) return;
            if (!visiting.Add(modName)) return; // cycle: this module is already on the stack
            foreach (var imp in ast.Imports)
                Visit(imp.ModuleName);
            visiting.Remove(modName);
            visited.Add(modName);
            order.Add(modName);
        }

        // Original map order as the outer loop, so two modules with no dependency relation
        // to each other keep their prior relative order (stable, not just topological).
        foreach (var modName in importedModules.Keys)
            Visit(modName);

        return order;
    }

    /// Runs a pre-scan transform on one imported module with that module's file attached.
    ///
    /// AsyncTransform and NamedtupleTransform walk a bare <see cref="ProgramNode"/>: AST
    /// nodes carry a line and column but no file, so an error one of them raises keeps the
    /// module's position and falls back to the entry file's name -- `yield` inside a method
    /// of adafruit_irremote.py:226 was reported as main.py:226:5, a line that file does not
    /// have. The module's recorded path is the same answer <c>RecordSourcePaths</c> gives a
    /// function that survives the transform, so stamp it here for every error the transform
    /// did not already locate.
    private void TransformModule(string modName, ProgramNode ast, Action<ProgramNode> transform)
    {
        try
        {
            transform(ast);
        }
        catch (PyMCU.Common.CompilerError e)
            when (!e.LocationIsFinal && e.File == null
                  && PathOfModule(modName) is { Length: > 0 } modPath)
        {
            throw new PyMCU.Common.CompilerError(
                e.TypeName, e.Message, e.Line, e.Column, e.Length) { File = modPath };
        }
    }

    private ProgramIR GenerateCore(
        ProgramNode mainAst,
        Dictionary<string, ProgramNode> importedModules,
        DeviceConfig config,
        List<string>? sourceLines = null,
        Dictionary<string, List<string>>? moduleSourceLines = null,
        HashSet<string>? projectModules = null,
        Dictionary<string, string>? modulePaths = null)
    {
        // Assign the PARAMETER, not only the field: inside this method the parameter shadows
        // the field, so every later use of the bare name saw the caller's null.
        projectModules ??= new HashSet<string>();
        this.projectModules = projectModules;
        this.importedModuleAsts = importedModules;
        this.deviceConfig = config;
        this.sourceLines = sourceLines ?? new List<string>();
        this.moduleSourceLines = moduleSourceLines ?? new Dictionary<string, List<string>>();
        this.modulePaths = modulePaths ?? new Dictionary<string, string>();
        loopVarReadAfter.Clear();
        ScanLoopVarReadsAfter(mainAst, importedModules.Values);
        ScanNameWrites(mainAst, importedModules.Values);

        // Join the two maps the caller already provides, so a debug listing can be looked up
        // by the path a compiled function carries rather than by a module name reconstructed
        // from its mangled prefix. The reconstruction could never match a dotted name, and a
        // miss was silent: it fell back to the entry file and printed that file's text against
        // the module's line numbers (issue #179).
        this.sourceLinesByPath = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var kv in this.moduleSourceLines)
            if (this.modulePaths.TryGetValue(kv.Key, out var modPath) && modPath.Length > 0)
                this.sourceLinesByPath[modPath] = kv.Value;
        this.lastLine = -1;
        this.currentSourceFile = "";
        this.currentSourcePath = "";

        var irProgram = new ProgramIR { Device = GeometryOf(config) };

        // The names an absent optional import would have bound, from every module (#366). The
        // frontend recorded them as it folded each `try` to its handler, which is the last
        // moment anything knows: after that the import statement is gone and `I2CDeviceDriver`
        // is indistinguishable from a misspelling. One set for the whole program, because an
        // annotation in one module names what another module's try imported as often as not.
        typingOnlyNames.Clear();
        foreach (var n in mainAst.TypingOnlyNames) typingOnlyNames.Add(n);
        foreach (var modAst0 in importedModules.Values)
            foreach (var n in modAst0.TypingOnlyNames) typingOnlyNames.Add(n);
        typingOnlyValues.Clear();

        typeAliases.Clear();
        foreach (var (alias, text) in mainAst.TypeAliases) typeAliases[alias] = text;
        foreach (var modAst1 in importedModules.Values)
            foreach (var (alias, text) in modAst1.TypeAliases) typeAliases[alias] = text;

        globals.Clear();
        mutableGlobals.Clear();
        functionReturnTypes.Clear();
        functionParams.Clear();
        inlineFunctions.Clear();
        modules.Clear();
        functionsToCompile.Clear();
        intrinsicNames.Clear();
        pendingIsrRegistrations.Clear();
        pendingIsrOrigins.Clear();
        pendingZcaIsrBindings.Clear();
        zcaHandlerAstNodes.Clear();
        pendingZcaSynthFunctions.Clear();
        externFunctionMap.Clear();
        pendingFlashData.Clear();
        classAttrInits.Clear();
        writtenClassAttributes.Clear();
        enumClassNames.Clear();

        foreach (var t in new[] { "uint8", "uint16", "uint32", "int8", "int16", "int32", "int" })
            intrinsicNames.Add(t);
        intrinsicNames.Add("print");
        intrinsicNames.Add("input");
        intrinsicNames.Add("len");
        intrinsicNames.Add("sum");
        intrinsicNames.Add("any");
        intrinsicNames.Add("all");
        intrinsicNames.Add("hex");
        intrinsicNames.Add("bin");
        intrinsicNames.Add("str");
        intrinsicNames.Add("pow");
        intrinsicNames.Add("zip");
        intrinsicNames.Add("reversed");
        intrinsicNames.Add("divmod");
        intrinsicNames.Add("bitcast");
        intrinsicNames.Add("gc_alloc");

        if (config.Frequency > 0)
        {
            constantVariables["__FREQ__"] = (int)config.Frequency;
            constantVariables["__FREQUENCY__"] = (int)config.Frequency;
        }
        // Always bound, so a HAL can read it without the time base in the program.
        constantVariables["__TIMEBASE__"] = config.Timebase ? 1 : 0;

        // Does ANY handler in the program bind a name with `as`? A raise records the address
        // of its message only when one does, so a program without the form compiles to the
        // bytes it always did. The answer is whole-program and has to be settled before the
        // first raise is lowered, which is why it is taken here and not at each raise (#369).
        programBindsExceptionObject = ProgramBindsExceptionObject(mainAst, importedModules.Values);

        // Desugar `async def` coroutines into ZCA state-machine classes before any
        // scanning, so the rest of the pipeline sees ordinary classes. Imported modules
        // transform first into a SHARED catalog of machine classes: a generator method
        // defined in a library (`Decoder.read` -> class `Decoder_read`) is consumed in
        // the entry file's `for v in d.read()`, which rewrites to `Decoder_read(d)` only
        // if the method-to-machine map and the class names reach the entry transform.
        var genUse = new PyMCU.Frontend.AsyncTransform.GenUse(
            new HashSet<string>(), new Dictionary<string, string>(),
            new HashSet<string>(), new HashSet<string>());
        foreach (var m in importedModules)
        {
            string modPrefix = m.Key.Replace('.', '_') + "_";
            TransformModule(m.Key, m.Value,
                ast => PyMCU.Frontend.AsyncTransform.TransformProgram(ast, genUse, modPrefix));
        }
        PyMCU.Frontend.AsyncTransform.TransformProgram(mainAst, genUse);

        // `Name = namedtuple("Name", ("a", "b"))` is a ZCA class, not a heap type. Rewrite
        // every module before TypeInference / scan see the assignment as a call.
        PyMCU.Frontend.NamedtupleTransform.TransformProgram(mainAst);
        foreach (var m in importedModules)
            TransformModule(m.Key, m.Value, PyMCU.Frontend.NamedtupleTransform.TransformProgram);

        // Fill unannotated params/returns of outlined functions from call-site evidence
        // (safe integer-widening join) BEFORE scanning, so an unannotated helper no longer
        // silently defaults to uint8 and truncates wider arguments.
        PyMCU.Frontend.TypeInference.InferProgram(mainAst, importedModules.Values);

        // Shared with the import check, which has to know these resolve with or without an
        // import naming them (PyMCU.Common.BuiltinExceptionNames).
        foreach (var exn in PyMCU.Common.BuiltinExceptionNames.Codes)
            constantVariables[exn.Key] = exn.Value;

        foreach (var imp in mainAst.Imports)
        {
            if (imp.ModuleName == "pymcu.types")
            {
                intrinsicNames.Add("ptr");
                intrinsicNames.Add("const");
                intrinsicNames.Add("device_info");
                intrinsicNames.Add("inline");
                intrinsicNames.Add("naked");
                intrinsicNames.Add("interrupt");
                intrinsicNames.Add("asm");
                intrinsicNames.Add("compile_isr");
                intrinsicNames.Add("claim");
                intrinsicNames.Add("_set_irq_zca_arg");
                intrinsicNames.Add("funcref");
            }

            if (imp.WasStarImport)
            {
                CheckIntrospectionBinding("", imp, StarImportExpander.Star);
                starImports.Add((imp.ModuleName, new List<string>(imp.Symbols)));
            }

            if (imp.Symbols.Count == 0)
            {
                string modKey = string.IsNullOrEmpty(imp.ModuleAlias) ? imp.ModuleName : imp.ModuleAlias;
                modules[modKey] = new ModuleScope();
                // Map the used name (alias or real) to the real module so member/method
                // resolution mangles `t.sleep_ms` (import time as t) to time_sleep_ms.
                importedAliases[modKey] = imp.ModuleName;
                RegisterModuleAlias("", modKey, imp.ModuleName, null);

                // `import alarm.time` binds `alarm`, not `alarm.time` -- CPython binds the
                // top-level package name in the importing namespace, so `alarm.time.X`
                // reads `alarm` first. Only the unaliased form does this: `import a.b as c`
                // binds `c` to the submodule itself.
                int topDot = imp.ModuleName.IndexOf('.');
                if (topDot > 0 && string.IsNullOrEmpty(imp.ModuleAlias))
                {
                    string top = imp.ModuleName.Substring(0, topDot);
                    if (!modules.ContainsKey(top))
                        modules[top] = new ModuleScope();
                    if (!importedAliases.ContainsKey(top))
                        importedAliases[top] = top;
                    RegisterModuleAlias("", top, top, null);
                }
            }

            foreach (var sym in imp.Symbols)
            {
                CheckIntrospectionBinding("", imp, sym);
                string key = imp.Aliases.ContainsKey(sym) ? imp.Aliases[sym] : sym;
                // Re-export chase: `from pymcu.hal import Pin` where hal/__init__ itself
                // does `from pymcu.hal.gpio import Pin` must bind Pin to the DEFINING
                // module -- mangling against the facade produced an undefined
                // pymcu_hal_Pin. A module that defines the symbol itself ends the chase.
                importedAliases[key] = ResolveReExport(importedModules, imp.ModuleName, sym);
                RegisterModuleAlias("", key, importedAliases[key],
                                    imp.Aliases.ContainsKey(sym) ? sym : null);
                if (imp.Aliases.ContainsKey(sym))
                    aliasToOriginal[key] = sym;
            }
        }

        foreach (var kvp in importedModules)
        {
            var modName = kvp.Key;
            var modAst = kvp.Value;
            string ownPrefix = modName.Replace('.', '_') + "_";
            foreach (var imp in modAst.Imports)
            {
                if (imp.ModuleName == "pymcu.types")
                {
                    intrinsicNames.Add("ptr");
                    intrinsicNames.Add("const");
                    intrinsicNames.Add("device_info");
                    intrinsicNames.Add("inline");
                    intrinsicNames.Add("naked");
                    intrinsicNames.Add("interrupt");
                    intrinsicNames.Add("asm");
                    intrinsicNames.Add("compile_isr");
                intrinsicNames.Add("claim");
                    intrinsicNames.Add("_set_irq_zca_arg");
                }

                if (imp.WasStarImport)
                    CheckIntrospectionBinding(modName, imp, StarImportExpander.Star);

                foreach (var sym in imp.Symbols)
                {
                    CheckIntrospectionBinding(modName, imp, sym);
                    string key = imp.Aliases.ContainsKey(sym) ? imp.Aliases[sym] : sym;
                    // A sub-module's `from X import S` can name a symbol X only re-exports;
                    // chase it to the defining module the same way the entry file's imports
                    // are chased, or `pulse_delay_us` inside helper.py binds to the facade
                    // `pymcu.hal.pulse` and mangles to a function that was never compiled.
                    string resolvedMod = ResolveReExport(importedModules, imp.ModuleName, sym);
                    // This module's OWN binding, which no other module can take from it.
                    RegisterModuleAlias(ownPrefix, key, resolvedMod,
                                        imp.Aliases.ContainsKey(sym) ? sym : null);
                    // Don't overwrite aliases established by the main file — sub-module
                    // imports use the same flat dictionary and would otherwise shadow the
                    // user's own `from machine import Pin` with a stdlib-internal
                    // `from pymcu.hal.gpio import Pin` that lives in e.g. hal/__init__.py.
                    if (!importedAliases.ContainsKey(key))
                    {
                        importedAliases[key] = resolvedMod;
                        if (imp.Aliases.ContainsKey(sym))
                            aliasToOriginal[key] = sym;
                    }
                }

                // `import x as y` inside an imported module (no symbols). The entry loop
                // above registers these only for the entry file; a module imported
                // transitively (main imports B, B does `import A as a`) also needs its
                // alias mapped so `a.func()` mangles to A_func, not a_func.
                if (imp.Symbols.Count == 0)
                {
                    string modKey = string.IsNullOrEmpty(imp.ModuleAlias) ? imp.ModuleName : imp.ModuleAlias;
                    RegisterModuleAlias(ownPrefix, modKey, imp.ModuleName, null);
                    if (!importedAliases.ContainsKey(modKey))
                        importedAliases[modKey] = imp.ModuleName;
                    if (!modules.ContainsKey(modKey))
                        modules[modKey] = modules.TryGetValue(imp.ModuleName, out var realScope)
                            ? realScope : new ModuleScope();

                    // Same top-name binding as the entry branch above: `import a.b` inside
                    // a module binds `a`, so a later `a.b.X` read inside that module finds it.
                    int topDot = imp.ModuleName.IndexOf('.');
                    if (topDot > 0 && string.IsNullOrEmpty(imp.ModuleAlias))
                    {
                        string top = imp.ModuleName.Substring(0, topDot);
                        RegisterModuleAlias(ownPrefix, top, top, null);
                        if (!importedAliases.ContainsKey(top))
                            importedAliases[top] = top;
                        if (!modules.ContainsKey(top))
                            modules[top] = modules.TryGetValue(top, out var topScope)
                                ? topScope : new ModuleScope();
                    }
                }
            }
        }

        // Which `Cls.ATTR` the program writes, gathered across EVERY module before any of them
        // is scanned. ScanGlobals decides there and then whether an ALL-CAPS class attribute
        // folds, and it runs on the imported modules first, so a write in the entry file was
        // not yet visible when the class that owns the attribute was scanned (#272).
        foreach (var st in mainAst.GlobalStatements) CollectWrittenClassAttributes(st);
        foreach (var fn in mainAst.Functions) CollectWrittenClassAttributes(fn.Body);
        foreach (var modAstForWrites in importedModules.Values)
        {
            foreach (var st in modAstForWrites.GlobalStatements) CollectWrittenClassAttributes(st);
            foreach (var fn in modAstForWrites.Functions) CollectWrittenClassAttributes(fn.Body);
        }

        // Track which AST objects have already been scanned so that the same
        // physical module file loaded under two different qualified names
        // (e.g. "time" via `import time` AND "pymcu.time" via
        // `from pymcu.time import …`) is only scanned once.  For the alias
        // name we still need all inline functions to be accessible under the
        // alias prefix (e.g. pymcu_time_delay_ms) so we copy them after the
        // canonical scan rather than running ScanFunctions a second time.
        var astToCanonicalPrefix = new Dictionary<ProgramNode, string>(ReferenceEqualityComparer.Instance);

        // Scan every module's OWN imports before the module itself, so a class's base --
        // when the base lives in a module this one imports, rather than the module the base
        // is used FROM -- is already fully scanned (its field layout and its methods
        // registered) by the time the subclass's class body is processed. Insertion order in
        // `importedModules` is BFS-by-discovery, not dependency order: an entry file that
        // imports `sub_mod`, whose own body then imports `base_mod`, adds `sub_mod` to the
        // map first and `base_mod` second -- and without this, `sub_mod` scanned first,
        // found nothing yet registered under `base_mod`'s prefix for `class Sub(Base): pass`
        // to inherit, and treated `Sub` as a class with an empty field layout, exactly as if
        // it truly took none of Base's constructor arguments (#391 again, from a different
        // cause: `MCP3008(spi, cs)`, whose class is defined in a different file from `MCP3xxx`,
        // the base it declares no `__init__` of its own and inherits from).
        var moduleScanOrder = TopologicallySortModules(importedModules);

        foreach (var modKey in moduleScanOrder)
        {
            var kvp = new KeyValuePair<string, ProgramNode>(modKey, importedModules[modKey]);
            var modName = kvp.Key;
            var modAst = kvp.Value;
            string modPrefix = modName.Replace('.', '_') + "_";

            if (astToCanonicalPrefix.TryGetValue(modAst, out var canonicalPrefix))
            {
                // Same AST already fully scanned under canonicalPrefix.
                // Share the same scope object so symbol-propagation loops
                // that look up modules[modName] still find the right entries.
                if (modules.TryGetValue(canonicalPrefix.Substring(0, canonicalPrefix.Length - 1), out var sharedScope))
                    modules[modName] = sharedScope;
                else
                    modules[modName] = new ModuleScope();

                // Propagate inline functions from canonical prefix to alias prefix
                // so callee resolution via importedAliases works for both names.
                var inlineAdds = new List<KeyValuePair<string, FunctionDef?>>();
                foreach (var fn in inlineFunctions)
                {
                    if (!fn.Key.StartsWith(canonicalPrefix)) continue;
                    string aliasKey = modPrefix + fn.Key.Substring(canonicalPrefix.Length);
                    if (!inlineFunctions.ContainsKey(aliasKey))
                        inlineAdds.Add(new KeyValuePair<string, FunctionDef?>(aliasKey, fn.Value));
                }
                foreach (var add in inlineAdds)
                {
                    inlineFunctions[add.Key] = add.Value;
                    string srcKey = canonicalPrefix + add.Key.Substring(modPrefix.Length);
                    if (functionParams.TryGetValue(srcKey, out var p)) functionParams.TryAdd(add.Key, p);
                    if (functionReturnTypes.TryGetValue(srcKey, out var rt)) functionReturnTypes.TryAdd(add.Key, rt);
                    if (functionParamTypes.TryGetValue(srcKey, out var pt)) functionParamTypes.TryAdd(add.Key, pt);
                    if (methodInstanceTypes.TryGetValue(srcKey, out var it)) methodInstanceTypes.TryAdd(add.Key, it);
                    // Keep the DEFINING module, the way the re-export copy below does:
                    // expanding `pymcu.time.delay_us` under the alias prefix made its
                    // internal `case "avr": _delay_us_avr(us)` look for a name only the
                    // canonical `time` scan registered -- a non-inline sibling never
                    // gets an alias entry, so the call resolved to nothing.
                    if (functionModulePrefix.TryGetValue(srcKey, out var mp)) functionModulePrefix.TryAdd(add.Key, mp);
                }
                // Propagate globals under the alias prefix too.
                var globAdds = new List<KeyValuePair<string, SymbolInfo>>();
                foreach (var g in globals)
                {
                    if (!g.Key.StartsWith(canonicalPrefix)) continue;
                    string aliasKey = modPrefix + g.Key.Substring(canonicalPrefix.Length);
                    if (!globals.ContainsKey(aliasKey))
                        globAdds.Add(new KeyValuePair<string, SymbolInfo>(aliasKey, g.Value));
                }
                foreach (var add in globAdds)
                    globals[add.Key] = add.Value;

                continue;
            }

            astToCanonicalPrefix[modAst] = modPrefix;
            modules[modName] = new ModuleScope();
            currentModulePrefix = modPrefix;
            int dotPos = modName.LastIndexOf('.');
            currentSourceFile = (dotPos != -1 ? modName.Substring(dotPos + 1) : modName) + ".py";
            currentSourcePath = PathOfModule(modName);
            ScanGlobals(modAst, modules[modName]);
            ScanFunctions(modAst, modules[modName]);
            RefuseCodegenDecoratorsOnExpandedFunctions(modAst);
        }

        foreach (var kvp in importedModules)
        {
            var modName = kvp.Key;
            var modAst = kvp.Value;
            if (!modules.TryGetValue(modName, out var scope))
            {
                // Module was not scanned in the first pass - this can happen if
                // importedModules was modified after the first foreach.
                // Create the scope now to avoid KeyNotFoundException.
                scope = new ModuleScope();
                modules[modName] = scope;
                currentModulePrefix = modName.Replace('.', '_') + "_";
                int dotPos2 = modName.LastIndexOf('.');
                currentSourceFile = (dotPos2 != -1 ? modName.Substring(dotPos2 + 1) : modName) + ".py";
                currentSourcePath = PathOfModule(modName);
                ScanGlobals(modAst, scope);
                ScanFunctions(modAst, scope);
                RefuseCodegenDecoratorsOnExpandedFunctions(modAst);
            }
            foreach (var imp in modAst.Imports)
            {
                if (modules.TryGetValue(imp.ModuleName, out var srcScope))
                {
                    foreach (var sym in imp.Symbols)
                    {
                        if (srcScope.Globals.TryGetValue(sym, out var globalSym))
                        {
                            scope.Globals[sym] = globalSym;
                        }
                        else if (srcScope.MutableGlobals.TryGetValue(sym, out var mutGlobalType))
                        {
                            scope.MutableGlobals[sym] = mutGlobalType;
                        }
                    }
                }
            }
        }

        // Propagate instanceClasses for `from X import Y` imports so that
        // GetValClass can find the ZCA class when user code uses imported
        // singletons via subscript (e.g. `from machine import mem8; mem8[addr]`).
        foreach (var imp in mainAst.Imports)
        {
            string modPrefix = imp.ModuleName.Replace('.', '_') + "_";
            foreach (var sym in imp.Symbols)
            {
                string key = imp.Aliases.ContainsKey(sym) ? imp.Aliases[sym] : sym;
                string importedKey = modPrefix + sym;
                if (instanceClasses.TryGetValue(importedKey, out var importedClass))
                    instanceClasses[key] = importedClass;
            }
        }

        currentModulePrefix = "";
        currentSourceFile = "main.py";
        // The entry file is what diagnostics are reported against by default, so it carries no
        // path of its own: an empty path means "the file the compiler was invoked on".
        currentSourcePath = "";

        // Record entry-file module-level `name = Ctor(...)` targets: their construction
        // is injected into main as module init, but later references resolve them as
        // module globals — the instance tracking must use the module key (SlotInstanceKey).
        foreach (var s in mainAst.GlobalStatements)
            if (s is AssignStmt { Target: VariableExpr tlTv, Value: CallExpr })
                topLevelInstanceTargets.Add(tlTv.Name);

        ScanGlobals(mainAst);
        ScanFunctions(mainAst);
        RefuseCodegenDecoratorsOnExpandedFunctions(mainAst);

        // The embedded runtime helpers are registered like scanned functions, so
        // they must wait until every module has been scanned -- a helper call
        // from any module's function resolves the same way.
        RegisterRuntimeHelpers();

        // AFTER the entry file's scan, which is the last one: a base class may be defined below
        // its subclass, or in a module scanned later, so this cannot run inside ScanFunctions (#279).
        CheckBaseClassNames();

        // Also after every module is scanned, for the same reason: whether an outlined
        // method's self.<sibling>() call can be forwarded statically depends on the sibling's
        // final outline-safety and on every override in the class tree, neither of which is
        // settled while the containing method is itself being scanned (#373).
        DemoteUnsafeOutlinedSelfCalls();

        // Synthesize a `main` function from top-level executable statements when the
        // user has not written an explicit `def main():`.  This allows MicroPython-
        // and CircuitPython-style scripts that have no entry-point wrapper.
        bool hasExplicitMain = mainAst.Functions.Any(f => f.Name == "main");
        if (!hasExplicitMain)
        {
            var executableStmts = mainAst.GlobalStatements
                .Where(s => !IsTopLevelPureDeclaration(s))
                .ToList();

            // A class body runs where it is written, so its attribute initializers go ahead of
            // the module's own statements, and ahead of anything that reads one (#270).
            if (classAttrInits.TryGetValue(mainAst, out var synthClassInit))
                executableStmts.InsertRange(0, synthClassInit);

            if (executableStmts.Count > 0)
            {
                var syntheticBlock = new Block();
                foreach (var s in executableStmts)
                    syntheticBlock.Statements.Add(s);

                var syntheticMain = new FunctionDef("main", new List<Param>(), "None", syntheticBlock);
                functionsToCompile.Insert(0,
                    new FunctionEntry { Prefix = "", Func = syntheticMain, SourceFile = "main.py", SourcePath = "" });
                functionReturnTypes["main"] = "None";
                functionParams["main"] = new List<string>();
                functionParamTypes["main"] = new List<DataType>();

                // A top-level script is an entry point too. Running an imported module's own
                // module level was wired to the explicit `def main():` branch only, so the
                // MicroPython and CircuitPython shape -- the one with no entry-point wrapper,
                // and the shape #117 was reported in -- still read every module-level value of
                // its imported modules as zero, with nothing to say so.
                EmitImportedModuleInit(syntheticMain, importedModules, astToCanonicalPrefix);
            }
        }
        else
        {
            // Explicit `def main()`. Module-level executable statements run at STARTUP,
            // before main()'s body -- mirroring Python, where the module body executes
            // before the entry point. (Previously they were rejected or silently dropped,
            // so a Pin/UART/sensor constructed at module scope never configured its
            // hardware -- only one constructed *inside* main did.)
            var mainFuncDef = mainAst.Functions.FirstOrDefault(f => f.Name == "main");
            if (mainFuncDef != null)
            {
                // Collect module-level statements with a runtime effect, in source order.
                // Pure declarations (imports, class defs, const/already-folded globals) are
                // skipped. A VarDecl initializer for a mutable global becomes an AnnAssign so
                // the global is actually written -- zero inits included: the AVR backend may
                // give a mutable global a register home, which BSS zeroing never touches, and
                // AVR registers power up undefined (the emulator zeroes them, real silicon
                // does not). Everything else -- AnnAssign SRAM arrays, plain constructions like
                // `led = Pin(...)`, bare calls, control flow -- runs as written.
                var moduleInit = new List<Statement>();

                // A class body runs where it is written, so its attribute initializers go
                // ahead of the module's own statements, and ahead of anything that reads
                // one (#270).
                if (classAttrInits.TryGetValue(mainAst, out var entryClassInit))
                    moduleInit.AddRange(entryClassInit);

                // Module-level statements written AFTER the explicit `main()` call. The call
                // marks where main's body runs, so everything below it runs after that body,
                // exactly as Python orders it -- see the self-call comment below (#301).
                var moduleTail = new List<Statement>();
                CallExpr? selfCall = null;
                int selfCallLine = 0;

                foreach (var s in mainAst.GlobalStatements)
                {
                    if (IsTopLevelPureDeclaration(s)) continue;

                    // `main()` at module level -- written by hand, or left by
                    // `if __name__ == "__main__": main()` once the guard folded away (the entry
                    // file IS __main__). It names the entry point PyMCU calls itself, so it is
                    // not lowered as a call: inserting one into main's own body made the cycle
                    // detector report `main -> main`, a recursion the user never wrote, for the
                    // most universal idiom in Python.
                    //
                    // It is not dropped either. It says WHERE main's body runs, and the
                    // statements below it run after that body. Dropping it ran them first
                    // instead, so `main(); print("END")` printed END before main's own output
                    // and nothing said so (#301).
                    if (IsEntryPointSelfCall(s, out var thisCall))
                    {
                        // Nothing has been lowered yet, so `currentStmtLine` is still 0 and a
                        // node with no column of its own would send the caret to line 1. The
                        // two refusals here are about a statement that is in hand: put its
                        // line where UserError looks for one.
                        int line = thisCall!.Line > 0 ? thisCall.Line : s.Line;
                        if (selfCall != null)
                        {
                            currentStmtLine = line;
                            throw UserError(
                                "main() is the entry point and runs once, so it cannot be "
                                + "called twice at module level. Move the second call's work "
                                + "into main, or rename the function and call it as often as "
                                + "you like.",
                                thisCall);
                        }
                        selfCall = thisCall;
                        selfCallLine = line;
                        continue;
                    }

                    if (s is VarDecl d)
                    {
                        if (d.Init != null
                            && mutableGlobals.ContainsKey(d.Name)
                            && !globals.ContainsKey(d.Name))
                            (selfCall == null ? moduleInit : moduleTail)
                                .Add(new AnnAssign(d.Name, d.VarType, d.Init));
                        continue;
                    }
                    (selfCall == null ? moduleInit : moduleTail).Add(s);
                }

                // main's body is spliced in where the call is, not called, so a `return` in it
                // ends the program and the statements below the call never run. Python runs
                // them. There is no lowering of this shape that is both a splice and correct,
                // so it is refused where the reader can see both halves (#301).
                if (moduleTail.Count > 0 && BodyCanReturnEarly(mainFuncDef.Body))
                {
                    currentStmtLine = selfCallLine;
                    throw UserError(
                        "main() returns, and there is module-level code after the call to it. "
                        + "PyMCU runs main's body where the call is written, so a `return` "
                        + "would skip that code. Move it into main, or drop the `return`.",
                        selfCall);
                }

                // Insert AFTER the build's auto-injected `_pymcu_*` preamble (clock_init,
                // millis_init, stdout) so module-level peripheral setup sees the final clocks
                // and stdout, but BEFORE the user's own main body.
                var body = mainFuncDef.Body.Statements;
                int at = 0;
                while (at < body.Count && IsInjectedPreamble(body[at])) at++;
                for (int i = moduleInit.Count - 1; i >= 0; i--)
                    body.Insert(at, moduleInit[i]);
                // A trailing `return` ahead of the spliced tail is Python's main() handing
                // control back to the module level: the tail still runs. Emitted as a Return
                // it would sit mid-body and cut the tail off at run time; as a terminated
                // sequence it would keep the tail from lowering at all. BodyCanReturnEarly
                // above already guaranteed this is the only `return` the spliced body holds.
                if (moduleTail.Count > 0 && body.Count > 0 && body[^1] is ReturnStmt trailing)
                {
                    body.RemoveAt(body.Count - 1);
                    if (trailing.Value != null)
                        body.Add(new ExprStmt(trailing.Value));
                }
                body.AddRange(moduleTail);

                // An import runs before the file that imports it, so this goes in AFTER the
                // entry module's own init and therefore ends up ahead of it.
                EmitImportedModuleInit(mainFuncDef, importedModules, astToCanonicalPrefix);
            }
        }

        foreach (var imp in mainAst.Imports)
        {
            if (modules.TryGetValue(imp.ModuleName, out var srcScope))
            {
                foreach (var sym in imp.Symbols)
                {
                    // A symbol that is already a known compile-time constant (e.g. the
                    // builtin exception codes ValueError=1, …, predefined regardless of
                    // import) must NOT be re-imported as a data global — doing so shadows
                    // the constant with an undefined symbol and breaks `raise ValueError`.
                    if (constantVariables.ContainsKey(sym)) continue;
                    if (srcScope.Globals.TryGetValue(sym, out var globalSym))
                    {
                        globals[sym] = globalSym;
                    }
                    else if (srcScope.MutableGlobals.ContainsKey(sym))
                    {
                        // Deliberately NOT `mutableGlobals[sym] = type`. The name already has
                        // storage, under the defining module's own key, and giving it a second
                        // one here split the variable in two: the module initializer wrote the
                        // declared value into `<mod>_<sym>` while the module's own functions and
                        // this file both wrote and read the bare name, so the declared value was
                        // in the firmware and nothing could reach it. ResolveBinding resolves
                        // this name through importedAliases to the one slot that exists.
                    }
                }
            }
        }

        foreach (var kvp in importedModules)
        {
            var modName = kvp.Key;
            var modAst = kvp.Value;
            string dstPrefix = modName.Replace('.', '_') + "_";

            foreach (var imp in modAst.Imports)
            {
                if (!modules.ContainsKey(imp.ModuleName)) continue;

                string srcPrefix = imp.ModuleName.Replace('.', '_') + "_";

                foreach (var sym in imp.Symbols)
                {
                    if (imp.Aliases.ContainsKey(sym)) continue;

                    // Re-export a plain @inline function under the facade name. The
                    // class-method loop below only matches "prefix_sym_<method>" keys, so
                    // the exact function key "prefix_sym" (e.g. millis) would otherwise be
                    // left unmapped and its call site would emit an unresolved CALL.
                    string srcExact = srcPrefix + sym;
                    string dstExact = dstPrefix + sym;
                    if (inlineFunctions.TryGetValue(srcExact, out var exactFn)
                        && !inlineFunctions.ContainsKey(dstExact))
                    {
                        inlineFunctions[dstExact] = exactFn;
                        if (functionParams.TryGetValue(srcExact, out var ep)) functionParams[dstExact] = ep;
                        if (functionReturnTypes.TryGetValue(srcExact, out var ert)) functionReturnTypes[dstExact] = ert;
                        if (functionParamTypes.TryGetValue(srcExact, out var ept)) functionParamTypes[dstExact] = ept;
                        if (methodInstanceTypes.TryGetValue(srcExact, out var emit)) methodInstanceTypes[dstExact] = emit;
                        // Keep the DEFINING module so inlining the re-exported function
                        // still resolves its internal helper calls in the original module.
                        if (functionModulePrefix.TryGetValue(srcExact, out var emp)) functionModulePrefix[dstExact] = emp;
                    }

                    string srcClassPrefix = srcPrefix + sym + "_";
                    string dstClassPrefix = dstPrefix + sym + "_";

                    var inlineAdds = new List<KeyValuePair<string, FunctionDef>>();
                    foreach (var funcKvp in inlineFunctions)
                    {
                        if (funcKvp.Key.StartsWith(srcClassPrefix))
                        {
                            string suffix = funcKvp.Key.Substring(srcClassPrefix.Length);
                            inlineAdds.Add(
                                new KeyValuePair<string, FunctionDef>(dstClassPrefix + suffix, funcKvp.Value));
                        }
                    }

                    foreach (var add in inlineAdds)
                    {
                        string newKey = add.Key;
                        string srcKey = srcClassPrefix + newKey.Substring(dstClassPrefix.Length);
                        inlineFunctions[newKey] = add.Value;

                        if (functionParams.TryGetValue(srcKey, out var p)) functionParams[newKey] = p;
                        if (functionReturnTypes.TryGetValue(srcKey, out var rt)) functionReturnTypes[newKey] = rt;
                        if (functionParamTypes.TryGetValue(srcKey, out var pt)) functionParamTypes[newKey] = pt;
                        if (methodInstanceTypes.TryGetValue(srcKey, out var mit)) methodInstanceTypes[newKey] = mit;
                    }

                    // Carry the OVERLOAD REGISTRY across the re-export as well. Once a name is
                    // overloaded its bare key is deliberately vacated in inlineFunctions so that
                    // suffix resolution can work, so copying inlineFunctions alone gives the
                    // facade the suffixed keys and nothing that records the name as overloaded.
                    // Constructor resolution asks exactly that question, so an overloaded
                    // __init__ reached through a facade was found under neither the bare key nor
                    // the overload set, and the call site reported the class as not exported --
                    // naming, as the near miss, the name it had just refused.
                    var ovlAdds = new List<string>();
                    foreach (var ovl in overloadedFunctions)
                    {
                        if (ovl == srcExact)
                            ovlAdds.Add(dstExact);
                        else if (ovl.StartsWith(srcClassPrefix))
                            ovlAdds.Add(dstClassPrefix + ovl.Substring(srcClassPrefix.Length));
                    }
                    foreach (var add in ovlAdds)
                        overloadedFunctions.Add(add);

                    foreach (var globKvp in Enumerable.ToList<KeyValuePair<string, SymbolInfo>>(globals))
                    {
                        if (globKvp.Key.StartsWith(srcClassPrefix))
                        {
                            string suffix = globKvp.Key.Substring(srcClassPrefix.Length);
                            globals[dstClassPrefix + suffix] = globKvp.Value;
                        }
                    }
                }
            }
        }

        // Runs here, not during the scan: it needs the class layouts and the instance-to-class
        // map, and both are only complete once every module has been scanned.
        currentModulePrefix = "";
        CheckSignatureAnnotations(mainAst);
        CheckGlobalAnnotations(mainAst);
        RegisterInstanceFieldArrays(mainAst);
        MarkModuleInstanceFields(mainAst);

        // Every imported module too, under its own prefix. The stdlib is deliberately out of
        // scope here, for the same reason EmitImportedModuleInit leaves it out: its modules are
        // written knowing that only the entry file's top level runs.
        foreach (var modKvp in importedModules)
        {
            if (!astToCanonicalPrefix.TryGetValue(modKvp.Value, out var markPrefix)) continue;
            if (!projectModules.Contains(modKvp.Key)) continue;
            currentModulePrefix = markPrefix;
            // The file the module's own declarations are written in, so a refusal below names
            // it rather than the entry program (#347, and #348 for the globals).
            string savedGlobPath = currentSourcePath, savedGlobFile = currentSourceFile;
            int gDot = modKvp.Key.LastIndexOf('.');
            currentSourceFile = (gDot != -1 ? modKvp.Key[(gDot + 1)..] : modKvp.Key) + ".py";
            currentSourcePath = PathOfModule(modKvp.Key);
            try
            {
                CheckSignatureAnnotations(modKvp.Value);
                CheckGlobalAnnotations(modKvp.Value);
            }
            finally
            {
                currentSourcePath = savedGlobPath;
                currentSourceFile = savedGlobFile;
            }
            RegisterInstanceFieldArrays(modKvp.Value);
            MarkModuleInstanceFields(modKvp.Value);
        }
        currentModulePrefix = "";

        ForceInlineClassReturningFactories();
        ForceInlineTupleReturningFunctions();
        ForceInlineBufferReturningFunctions();
        ForceInlineClassPlainFunctionsThatReadParamMembers();

        // RFC 0009 decision 2: decide which declared/inferred Optional returns can
        // actually produce a run-time None. Only those get `ReturnMembers` on the IR
        // function and the tag byte on the wire; a function whose None is provable at
        // compile time keeps the exact code it had before. Runs after every scan so a
        // call site can ask it while its own function is still being generated.
        ResolveOptionalReturns();

        // RFC 0009 section 10: the same question turned around for parameters -- a
        // union-annotated parameter of a real subroutine carries a tag byte only
        // when the call sites can hand it more than one member. Must run after
        // ResolveOptionalReturns so an argument that is itself a call can ask what
        // its callee returns.
        ResolveOptionalParams(mainAst, importedModules, astToCanonicalPrefix);

        // Whether raises record their message for a later read. Two readers: a handler that
        // binds `except X as e` (#369), and the unhandled-exception report, which prints
        // `E:<Type>: <msg>` through __pymcu_exn_tail. The second needs a raise that actually
        // carries a message AND the UART string writer the printer calls already in the
        // image -- which is why this is computed after the module scan filled
        // functionParams/functionReturnTypes, but before the first raise is lowered.
        // A program that raises messages without any writer linked keeps the raise and
        // drops the report, exactly as before.
        bool programRaisesWithMessage = ProgramRaisesWithMessage(mainAst, importedModules.Values);
        programReportsRaiseMessage = programRaisesWithMessage
            && ResolveRuntimeWriteStrFn() != "uart_write_str";
        programRecordsRaiseMessages = programBindsExceptionObject || programReportsRaiseMessage;
        programHasDynamicRaiseMessage = programRecordsRaiseMessages
            && ProgramHasDynamicRaiseMessage(mainAst, importedModules.Values);

        // A synthesized `__module_init` is LOWERED before the rest, then put back where it was.
        //
        // Lowering the module level is what BINDS a module-level instance's fields: a Pin's
        // port, direction register and bit are compile-time constants established by the
        // construction. EmitImportedModuleInit appends the init to the end of the list, so a
        // function of that same module was lowered first and read the fields as run-time
        // values instead -- `led = Pin("PB5", Pin.OUT)` at a module's top level still failed
        // from a function of that module, now on the field read rather than on the missing
        // construction.
        //
        // Conditional because lowering order is observable: it advances the shared label,
        // temporary and string-literal counters, so hoisting unconditionally renumbered every
        // program in the corpus (and shifted interned string ids) for a binding only a module
        // with an init needs. A program without one lowers exactly as it always did.
        //
        // Only the ORDER OF LOWERING changes. Emission order is restored below.
        var lowered = new Function?[functionsToCompile.Count];
        var lowerOrder = new List<int>(functionsToCompile.Count);
        var initFirst = new List<int>();

        // The ENTRY file has no `__module_init` of its own: its module level is injected into
        // main's body, so lowering MAIN is what binds an object built there. Any other function
        // of the entry file was lowered first and read the instance's fields as run-time values,
        // which the backend reported as a bit index through a runtime pointer, at a line the
        // file does not have. Same binding, same remedy, same scope: only a file that builds an
        // instance at its module level moves, so every other program lowers as it always did.
        var mainFirst = new List<int>();
        bool hoistEntryMain = EntryModuleLevelBuildsInstance(mainAst);
        if (hoistEntryMain)
            Logger.Verbose("IRGen", "entry file builds an instance at module level; lowering main first");

        for (int i = 0; i < functionsToCompile.Count; i++)
        {
            if (functionsToCompile[i].Func.Name == "__module_init") initFirst.Add(i);
            else if (hoistEntryMain && string.IsNullOrEmpty(functionsToCompile[i].Prefix)
                     && functionsToCompile[i].Func.Name == "main") mainFirst.Add(i);
            else lowerOrder.Add(i);
        }
        lowerOrder.InsertRange(0, mainFirst);
        lowerOrder.InsertRange(0, initFirst);

        foreach (int i in lowerOrder)
        {
            var entry = functionsToCompile[i];
            currentModulePrefix = entry.Prefix;
            currentSourceFile = entry.SourceFile;
            currentSourcePath = entry.SourcePath;
            if (!entry.Func.IsInline)
            {
                lowered[i] = VisitFunction(entry.Func);
            }
        }

        foreach (var fn in lowered)
            if (fn != null)
                irProgram.Functions.Add(fn);

        // The report flags are AST-level: a raise with a message inside an imported but
        // never-called function sets them even though nothing reachable can record one.
        // In that case the messageless-raise clear stores lowered under the flag are
        // dead, and so is the report tail -- strip the clears and skip the synthesis so
        // the image stays byte-identical to one built without the machinery.
        if (!sawRaiseMessageStore)
        {
            foreach (var fn in irProgram.Functions)
                fn.Body.RemoveAll(i => i is Copy c
                    && c.Dst is Variable { Name: ExceptionMessageVar }
                    && c.Src is Constant { Value: 0 });
            if (raiseMessageSites.Count == 0)
                foreach (var fn in irProgram.Functions)
                    fn.Body.RemoveAll(i => i is Copy c
                        && c.Dst is Variable { Name: ExceptionSiteVar }
                        && c.Src is Constant { Value: 0 });
        }

        if (programHasDynamicRaiseMessage || (programReportsRaiseMessage && sawRaiseMessageStore))
            irProgram.Functions.Add(SynthesizeExceptionMessagePrinter());
        // The unhandled path in the backend calls __pymcu_exn_tail from raw asm after
        // printing `E:<Type>` -- it appends ": <msg>" + CRLF when a message was recorded
        // and just the CRLF when it was not. Only emitted when a raise can carry a
        // message and the string writer is in the image to print it.
        if (programReportsRaiseMessage && sawRaiseMessageStore)
            irProgram.Functions.Add(SynthesizeExceptionMessageTail());
        LowerCalledRuntimeHelpers(irProgram);

        // Inject FlashData instructions (global const[uint8[N]] arrays) into the
        // main function body so the backend emits .byte tables in flash.
        if (pendingFlashData.Count > 0)
        {
            var mainFunc = irProgram.Functions.FirstOrDefault(f => f.Name == "main");
            if (mainFunc != null)
            {
                mainFunc.Body.InsertRange(0, pendingFlashData);
            }
        }

        foreach (var sf in pendingZcaSynthFunctions)
            irProgram.Functions.Add(sf);
        pendingZcaSynthFunctions.Clear();

        foreach (var kvp in pendingIsrRegistrations)
        {
            string bareName = kvp.Key;
            int vec = kvp.Value;
            bool found = false;
            foreach (var fn in irProgram.Functions)
            {
                if (fn.Name == bareName ||
                    (fn.Name.Length > bareName.Length &&
                     fn.Name[fn.Name.Length - bareName.Length - 1] == '_' &&
                     fn.Name.EndsWith(bareName)))
                {
                    fn.IsInterrupt = true;
                    fn.InterruptVector = vec;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                pendingIsrOrigins.TryGetValue(bareName, out var origin);
                bool fromModule = !string.IsNullOrEmpty(origin.Module);
                string where = string.IsNullOrEmpty(origin.Function)
                    ? ""
                    : $" inside '{origin.Function}'";
                string at = fromModule && origin.Line > 0
                    ? $" The call is at line {origin.Line} of the module that defines it, not of " +
                      "the file being compiled."
                    : "";
                throw new PyMCU.Common.CompilerError("CompileError",
                    $"compile_isr(){where} could not resolve '{bareName}' to a function. The " +
                    "handler must be a compile-time function reference: either a top-level " +
                    "function in this translation unit, or a parameter that folds to one. A " +
                    "function that calls compile_isr() with a handler parameter must be " +
                    $"@inline -- without it the parameter stays a run-time value and no " +
                    $"function can be resolved.{at}",
                    fromModule || origin.Line <= 0 ? 1 : origin.Line, 1);
            }
        }

        pendingIsrRegistrations.Clear();
        pendingIsrOrigins.Clear();

        irProgram.UsesRefPayloads = usesRefPayloads;

        foreach (var kvp in mutableGlobals)
        {
            irProgram.Globals.Add(new Variable(kvp.Key, kvp.Value));
        }

        // Module-level SRAM arrays must be allocated as globals so the overlay
        // algorithm never aliases them with function-local arrays across sibling calls.
        foreach (var name in moduleSramArrays)
        {
            int count = arraySizes.TryGetValue(name, out int c) ? c : 1;
            DataType elemType = arrayElemTypes.TryGetValue(name, out DataType dt) ? dt : DataType.UINT8;
            irProgram.GlobalArrays[name] = count * elemType.SizeOf();
        }

        var seenExtern = new HashSet<string>();
        foreach (var kvp in externFunctionMap)
        {
            if (seenExtern.Add(kvp.Value))
            {
                irProgram.ExternSymbols.Add(kvp.Value);
                // Carry the DECLARED widths across to the backend: an @extern function has no
                // body, so it never reaches irProgram.Functions, and without this the call site
                // sizes each argument by the width of the value instead of the parameter --
                // f(5) to a uint16_t loaded one byte and left the high one undefined.
                irProgram.ExternSignatures.Add(new ExternSignature
                {
                    Symbol = kvp.Value,
                    ParamTypes = functionParamTypes.TryGetValue(kvp.Key, out var extParamTypes)
                        ? new List<DataType>(extParamTypes)
                        : new List<DataType>(),
                    ReturnType = functionReturnTypes.TryGetValue(kvp.Key, out var extRet)
                        ? DataTypeExtensions.StringToDataType(extRet ?? "void")
                        : DataType.VOID,
                });
            }
        }

        foreach (var sym in exnExterns)
            if (seenExtern.Add(sym))
                irProgram.ExternSymbols.Add(sym);

        // If any try/raise was emitted, allocate the global 2-byte active-jmpbuf pointer.
        if (exnExterns.Count > 0)
        {
            irProgram.Globals.Add(new Variable("__pymcu_active_jmpbuf", DataType.UINT16));
        }

        loopStack.Clear();
        externFunctionMap.Clear();
        exnExterns.Clear();

        if (_pendingFlashData.Count > 0)
        {
            var mainFunc = irProgram.Functions.FirstOrDefault(f => f.Name == "main");
            if (mainFunc != null) mainFunc.Body.InsertRange(0, _pendingFlashData);
            _pendingFlashData.Clear();
        }

        // Two functions compiled under the same name (e.g. `def f()` twice without an
        // overload-distinguishing signature) would otherwise crash a downstream
        // ToDictionary(f => f.Name) with a raw "An item with the same key has already been
        // added" reported as an InternalCompilerError. Report it as a clean diagnostic.
        var dupFn = irProgram.Functions
            .GroupBy(f => f.Name)
            .FirstOrDefault(g => g.Count() > 1);
        if (dupFn != null)
            throw new PyMCU.Common.CompilerError("CompileError",
                $"duplicate function definition: '{dupFn.Key}' is defined more than once " +
                "(give the overloads different parameter types, or rename one)", 1, 1);

        // Propagate class hierarchy so the Optimizer devirt pass and AvrCodeGen
        // can work without access to IRGenerator-internal state.
        irProgram.ClassChildren = new Dictionary<string, HashSet<string>>(
            classChildren.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value)));
        irProgram.ClassDirectMethods = new Dictionary<string, HashSet<string>>(
            classDirectMethods.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value)));

        // An outlined body reachable from two contexts that can interrupt each other is not
        // reentrant, and its statics are shared between them. Checked on the finished IR so an
        // @inline body, already expanded into its callers, cannot be flagged.
        CheckReentrancy(irProgram);

        // Names emitted for values that exist only at compile time -- object
        // instances, bound functions, comprehension items, literal containers.
        // Each leaves a placeholder Variable where a read needs a Val and no
        // instruction ever writes the byte; the verifier exempts exactly these
        // from read-never-written, the one check that cannot tell them from a
        // genuinely unwritten slot.
        irProgram.CompileTimeNames = CompileTimeOnlyNames(irProgram);

        // Between-passes verifier (PYMCU_VERIFY_IR): the raw generator output is the
        // stage every later pass trusts, so it is the first thing worth checking.
        Verifier.Check(irProgram, "generate");

        return irProgram;
    }

    /// <summary>
    /// Every Variable/Temporary name in the finished IR that stands for a
    /// compile-time value rather than a run-time slot. A name counts when a
    /// positive compile-time binding map knows it (instanceClasses and
    /// friends -- an object's placeholder byte still acquires a scalar entry
    /// from the synthesized assign that built it, so those maps win over the
    /// scalar test), or when it owns no scalar slot of its own but is the
    /// parent under which real slots were flattened, or it names a function
    /// being used as a value. A name that fails both sides -- no slot, no
    /// compile-time binding -- stays out, so the verifier still treats it as
    /// a genuine unwritten read.
    /// </summary>
    private List<string> CompileTimeOnlyNames(ProgramIR irProgram)
    {
        // Strict prefixes of every emitted name: an object whose fields
        // flatten to "obj_field" leaves the parent "obj" naming nothing but
        // the instance itself. Every emitted name contributes, not only the
        // scalar maps' -- a field slot the maps never registered still makes
        // its parent a compound object. A real scalar that shares a prefix
        // with a compound child is still kept out by HasScalarSlot below.
        var scalarParents = new HashSet<string>(StringComparer.Ordinal);
        void AddParents(string key)
        {
            for (int i = 1; i < key.Length; i++)
                if (key[i] == '_' || key[i] == '.')
                    scalarParents.Add(key[..i]);
        }
        foreach (var fn0 in irProgram.Functions)
            foreach (var ins0 in fn0.Body)
                foreach (var n0 in Verifier.ScalarNames(ins0))
                    AddParents(n0);

        // The str-var maps stay out: a string variable's scalar byte is a
        // vestigial handle -- the data lives in the str machinery's own
        // buffers -- so the name joins the compile-time side below instead.
        // runtimePtrVars stays scalar: a pointer variable's slot really is
        // the storage.
        bool HasScalarSlot(string n) =>
            variableTypes.ContainsKey(n)
            || mutableGlobals.ContainsKey(n)
            || globals.ContainsKey(n)
            || runtimePtrVars.ContainsKey(n)
            || arrayElemTypes.ContainsKey(n)
            || arraySizes.ContainsKey(n)
            || moduleSramArrays.Contains(n)
            || arraysWithVariableIndex.Contains(n)
            || bytearrayParams.Contains(n)
            || tempRanges.ContainsKey(n);

        // A function or bound method passed as a value leaves a name whose
        // byte is the funcref placeholder. The IR spells the module-level
        // ones "main.f" -- the registration tables know them bare.
        bool FunctionObject(string n)
        {
            if (functionParams.ContainsKey(n) || functionReturnTypes.ContainsKey(n)
                || inlineFunctions.ContainsKey(n) || methodAstByName.ContainsKey(n)
                || externFunctionMap.ContainsKey(n) || loopFunctionAliases.ContainsKey(n)
                || lambdaVariableNames.ContainsKey(n))
                return true;
            int dot = n.IndexOf('.');
            return dot > 0
                && (functionParams.ContainsKey(n[(dot + 1)..])
                    || functionReturnTypes.ContainsKey(n[(dot + 1)..])
                    || inlineFunctions.ContainsKey(n[(dot + 1)..])
                    || methodAstByName.ContainsKey(n[(dot + 1)..]));
        }

        // Positive compile-time bindings only; scalarParents and boundNames
        // stay out because they also hold real scalars (a name that shares a
        // prefix with another, and every assigned name respectively) -- they
        // join the test below only behind the no-scalar-slot gate. The
        // constant tables count as compile-time bindings -- a Variable read
        // of a name whose value the generator already knows is a placeholder
        // read, never a demand for runtime storage.
        bool IsCompileTimeOnly(string n) =>
            instanceClasses.ContainsKey(n)
            || slotInstances.ContainsKey(n)
            || constSequenceBindings.ContainsKey(n)
            || dictLiteralBindings.ContainsKey(n)
            || setLiteralBindings.ContainsKey(n)
            || listLiteralParams.ContainsKey(n)
            || arrayLiteralElements.ContainsKey(n)
            || namedTupleElements.ContainsKey(n)
            || instanceArrayClass.ContainsKey(n)
            || noneValuedNames.Contains(n)
            || constantVariables.ContainsKey(n)
            || constantAddressVariables.ContainsKey(n)
            || floatConstantVariables.ContainsKey(n)
            || localConstantValues.ContainsKey(n)
            || strConstantVariables.ContainsKey(n)
            || runtimeStrVars.ContainsKey(n)
            || bufferLogicalLen.ContainsKey(n)
            || multiStrVariables.ContainsKey(n)
            || multiStrCandidates.ContainsKey(n)
            || FunctionObject(n);

        // A name spelled "main.x" is the module global "x" -- the twin rule
        // one-storage-key already encodes. Bindings file under either spelling
        // (module-level `with m as v:` binds the bare name while reads spell
        // the qualified one), so both sides of the test ask both spellings.
        bool AnyKey(Func<string, bool> test, string n)
        {
            if (test(n)) return true;
            if (n.StartsWith("main.")) return test(n[5..]);
            int dot = n.IndexOf('.');
            if (dot > 0 && n[..dot].EndsWith("___module_init"))
                return test(n[..dot][..^"___module_init".Length] + "_"
                            + n[(dot + 1)..]);
            return false;
        }

        // A flattened field of a compile-time object: "main.s__offset" is the
        // field alias of the slot instance "main.s". The module head itself
        // ("main", "mod___module_init") is not an object, so the walk skips
        // the first separator.
        bool ChildOfCtParent(string n)
        {
            bool first = true;
            for (int i = 1; i < n.Length; i++)
            {
                if (n[i] != '_' && n[i] != '.') continue;
                if (first) { first = false; continue; }
                if (IsCompileTimeOnly(n[..i])) return true;
            }
            return false;
        }

        // Names the compiler mints itself -- anonymous constructor targets
        // ("__c" + id), const-sequence elements ("__ctseq"), comprehension
        // temporaries ("__ctcomp"), const tables ("__cttab"). They never name
        // user storage; IsNameKnownSomewhere applies the same "__c" test for
        // the undefined-name check.
        bool MintedName(string n)
        {
            int dot = n.LastIndexOf('.');
            string seg = dot >= 0 ? n[(dot + 1)..] : n;
            return seg.StartsWith("__c");
        }

        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var fn in irProgram.Functions)
        {
            var paramSet = new HashSet<string>(fn.Params);
            foreach (var ins in fn.Body)
                foreach (var n in Verifier.ScalarNames(ins))
                {
                    if (Temporary.IsScratchName(n) || paramSet.Contains(n)) continue;
                    bool scalar = AnyKey(HasScalarSlot, n);
                    // A positive compile-time map wins over the scalar gate:
                    // an object's placeholder byte still acquires a
                    // variableTypes entry from the synthesized assign that
                    // built it. The compound/child/bound fallbacks only apply
                    // to a name that owns NO scalar slot -- a real scalar can
                    // share a prefix with an unrelated flattened child, and a
                    // real field of an instance is ordinary storage. For a
                    // slotless name, boundNames means bound-to-a-compile-time
                    // value: a run-time binding would have emitted the write
                    // the verifier looks for first.
                    if (AnyKey(IsCompileTimeOnly, n) || MintedName(n)
                        || (!scalar && (scalarParents.Contains(n)
                                        || ChildOfCtParent(n)
                                        || boundNames.Contains(n))))
                        names.Add(n);
                }
        }
        return new List<string>(names);
    }

    /// <summary>
    /// The tail of the "not defined" message when a star import is in scope. "never imported"
    /// reads as a contradiction of the import the reader can see, so name the star and what it
    /// did bring in. A leading underscore is the usual reason a name is missing: it is private
    /// by convention and a star never binds one, in CPython either.
    /// </summary>
    private string StarImportHint(string name)
    {
        if (starImports.Count == 0) return "";

        var parts = new List<string>();
        foreach (var (module, names) in starImports)
        {
            string brought = names.Count == 0
                ? "nothing: it exports no public top-level name"
                : string.Join(", ", names.Take(8)) + (names.Count > 8 ? ", ..." : "");
            parts.Add($"'from {module} import *' brings in {brought}");
        }

        string why = name.StartsWith('_')
            ? $". A name starting with '_' is private and no star import binds it, here or in "
              + $"CPython; write `from <its module> import {name}` if that is what you meant"
            : "";

        return $". {string.Join("; ", parts)}{why}";
    }

    private bool InlineScopeShadows(string name)
    {
        if (string.IsNullOrEmpty(currentInlinePrefix)) return false;
        string k = currentInlinePrefix + name;
        return constantVariables.ContainsKey(k) || strConstantVariables.ContainsKey(k)
            || floatConstantVariables.ContainsKey(k) || variableAliases.ContainsKey(k)
            || constantAddressVariables.ContainsKey(k) || variableTypes.ContainsKey(k);
    }

    /// <summary>
    /// Whether <paramref name="name"/> is bound in the scope being lowered right now -- the
    /// @inline expansion's prefix, or the current function's `name.` key -- in ANY binding
    /// table. A local binding shadows a module-level global of the same name for every
    /// name-keyed lookup: without this a `data = bytearray(2)` at module level answered the
    /// "is this a sequence" question asked about `data` inside `uart_rx_read`, whose own
    /// `data` is a uint8 local (#458).
    ///
    /// `global`/`nonlocal` declarations deliberately do not count: they record the bare name
    /// in <c>currentFunctionGlobals</c>, which is the opposite of a local binding.
    /// </summary>
    private bool LocalScopeBinds(string name)
    {
        foreach (var k in new[]
        {
            string.IsNullOrEmpty(currentInlinePrefix) ? null : currentInlinePrefix + name,
            string.IsNullOrEmpty(currentFunction) ? null : currentFunction + "." + name,
        })
        {
            if (k == null) continue;
            if (variableTypes.ContainsKey(k) || constantVariables.ContainsKey(k)
                || constantAddressVariables.ContainsKey(k) || strConstantVariables.ContainsKey(k)
                || floatConstantVariables.ContainsKey(k) || variableAliases.ContainsKey(k)
                || listLiteralParams.ContainsKey(k) || dictLiteralBindings.ContainsKey(k)
                || setLiteralBindings.ContainsKey(k) || runtimeStrVars.ContainsKey(k)
                || funcrefReturnTypes.ContainsKey(k) || loopFunctionAliases.ContainsKey(k)
                || noneValuedNames.Contains(k) || bytearrayParams.Contains(k)
                || boundNames.Contains(k))
                return true;
        }
        return false;
    }

    /// <summary>
    /// A module-level `main()` with no arguments: the call the runtime already makes. Written
    /// by hand or left by the `if __name__ == "__main__":` guard, it means the same thing.
    /// </summary>
    private static bool IsEntryPointSelfCall(Statement s, out CallExpr? call)
    {
        if (s is ExprStmt { Expr: CallExpr { Callee: VariableExpr { Name: "main" } } c }
            && c.Args.Count == 0)
        {
            call = c;
            return true;
        }
        call = null;
        return false;
    }

    /// <summary>
    /// True when lowering this body can reach a `return` before its last statement runs.
    ///
    /// Only asked of the entry point's body, and only to decide whether splicing it in place of
    /// an explicit `main()` call would swallow the module-level statements written below that
    /// call. A `return` as the very last statement of the body is not one of those: nothing in
    /// the body follows it, so the splice puts the module tail exactly where Python does.
    /// </summary>
    private static bool BodyCanReturnEarly(Block body)
    {
        for (int i = 0; i < body.Statements.Count; i++)
        {
            var s = body.Statements[i];
            if (s is ReturnStmt) return i != body.Statements.Count - 1;
            if (ContainsReturn(s)) return true;
        }
        return false;
    }

    /// A `return` anywhere inside a statement's nested blocks, which is always an early one:
    /// control reaches it only under a condition, so the statements after it can still run.
    private static bool ContainsReturn(Statement? s) => s switch
    {
        null => false,
        ReturnStmt => true,
        Block b => b.Statements.Any(ContainsReturn),
        IfStmt ifs => ContainsReturn(ifs.ThenBranch)
                      || ContainsReturn(ifs.ElseBranch)
                      || ifs.ElifBranches.Any(e => ContainsReturn(e.Body)),
        WhileStmt w => ContainsReturn(w.Body),
        ForStmt f => ContainsReturn(f.Body),
        TryStmt t => t.Body.Any(ContainsReturn)
                     || t.Handlers.Any(h => h.Handler.Any(ContainsReturn))
                     || (t.ElseBody?.Any(ContainsReturn) ?? false)
                     || (t.Finally?.Any(ContainsReturn) ?? false),
        MatchStmt m => m.Branches.Any(c => ContainsReturn(c.Body)),
        WithStmt wi => ContainsReturn(wi.Body),
        _ => false
    };

    /// <summary>
    /// The diagnostic for indexing an unrolled array with a run-time value. The subscript is
    /// not the problem -- the same subscript on a declared array compiles -- so a message about
    /// the subscript sends the reader off trying to make the INDEX constant, which defeats the
    /// buffer. Name the array and the annotation that makes it indexable.
    /// </summary>
    /// <param name="at">
    /// The ARRAY the message names, not the whole subscript and not the index. `xs[i]` has
    /// three candidates and the message is about `xs`: it asks the reader to declare a type
    /// for the array, so a caret under `i` would send them to make the index constant, which
    /// is the reading the message was written to prevent.
    /// </param>
    private Exception UnrolledArrayIndexError(string qualified, ASTNode? at = null)
    {
        int dot = qualified.LastIndexOf('.');
        string shown = dot >= 0 ? qualified[(dot + 1)..] : qualified;
        string elem = arrayElemTypes.TryGetValue(qualified, out var et) && et != DataType.UNKNOWN
            ? et.ToString().ToLowerInvariant()
            : "uint8";
        int size = arraySizes.TryGetValue(qualified, out var sz) ? sz : 0;
        string example = size > 0 ? $"{shown}: {elem}[{size}] = [...]" : $"{shown}: {elem}[N] = [...]";
        // The old text said the array "has no declared array type". That is true for a bare
        // `b = [1, 2, 3]` and FALSE for the other way in: an array declared `uint8[64]` and
        // passed to an @inline whose call the scan could not follow arrives here with its
        // declaration intact and its addressability lost (#246). Telling that reader to declare
        // a type sends them to add an annotation they already wrote.
        //
        // So the message names the PROPERTY that is missing -- addressable storage -- and then
        // gives both ways to get it, rather than asserting a cause that is right half the time.
        return UserError(
            $"'{shown}' is not addressable at run time here: it lives as separate variables, so "
            + "it can only be indexed with a constant. An array gets real storage when it is "
            + "declared with a type and the compiler can see it indexed at run time. Either "
            + $"declare it, e.g. `{example}`, or -- if it is already declared and is being "
            + "passed into an @inline -- hold it as a field of the class instead, which always "
            + "has storage.", at);
    }

    /// <param name="at">
    /// The expression node the name was read from, when the caller has it. Only used to locate
    /// the "not defined" diagnostic: a name is a token, so it has a column, and the caret
    /// belongs under the name rather than under the first character of the statement. Callers
    /// that hold no node pass none and get a statement-level location with no caret.
    /// </param>
    private Val ResolveBinding(string name, PyMCU.Frontend.ASTNode? at = null)
    {
        // THE FIRST READ of a value whose annotation names something with no representation
        // here (#367). The annotation was accepted on the parameter, because a parameter the
        // body never reads costs nothing and refusing it stopped eight Adafruit libraries on
        // the three arguments of a context manager's `__exit__`. Reading it is the other half:
        // the value has no width, so it is refused at the line that asks for it, and #278's
        // guarantee holds where it matters -- nothing the program touches has an unknown width.
        //
        // Not in ProbeBinding, which asks rather than lowers.
        foreach (string key in TypingOnlyCandidateKeys(name))
            if (typingOnlyValues.TryGetValue(key, out var ann))
                throw UserError(
                    $"'{name}' is annotated '{ann}', which names nothing this target has a "
                    + "representation for, so it has no width to be read at. The annotation is "
                    + "accepted on a value the program does not touch; this line touches it. "
                    + "Give it a concrete type, or do not read it.", at);

        return ResolveBindingCore(name, at, probe: false)!;
    }

    /// The spellings a parameter may have been recorded under, for the typing-only check.
    private IEnumerable<string> TypingOnlyCandidateKeys(string name)
    {
        if (typingOnlyValues.Count == 0) yield break;
        if (!string.IsNullOrEmpty(currentInlinePrefix)) yield return currentInlinePrefix + name;
        if (!string.IsNullOrEmpty(currentFunction)) yield return currentFunction + "." + name;
    }

    /// Resolves <paramref name="name"/> for a caller that is ASKING rather than lowering, and
    /// answers null instead of throwing when the name is simply not defined.
    ///
    /// The distinction that matters is between "I do not know this name" and "this name is
    /// refused". A module-level `raise CompileError(...)` guard that folded away -- the HAL
    /// saying this part has no hardware UART, say -- is a definite answer with a written reason,
    /// and it keeps throwing even here: swallowing it would replace one good sentence with the
    /// generic "call to undefined function 'uart_init'". Only the plain never-defined case is
    /// downgraded to null, because that is the one where the asker has a better message of its
    /// own to reach.
    private Val? ProbeBinding(string name) => ResolveBindingCore(name, null, probe: true);

    private Val? ResolveBindingCore(string name, PyMCU.Frontend.ASTNode? at, bool probe)
    {
        if (globals.TryGetValue(name, out var symInfo))
        {
            if (symInfo.IsMemoryAddress)
                return new MemoryAddress(symInfo.Value, symInfo.Type);
            return new Constant(symInfo.Value);
        }

        // Loop variable bound to a function reference (zip() over a function list).
        if (loopFunctionAliases.TryGetValue(name, out string? fnAliasName))
            return new FunctionRef(fnAliasName);

        // Inside an @inline expansion a bound parameter or local shadows any module
        // global of the same name: `uart.write('hello')` inlines write(data=...) and a
        // user-level `data = 5` global must not hijack the body's reads of `data`
        // (the const[str] binding then went unseen and the call hard-errored).
        if (mutableGlobals.ContainsKey(name) && !InlineScopeShadows(name))
        {
            if (!string.IsNullOrEmpty(currentFunction))
            {
                if (currentFunctionGlobals.Contains(name))
                {
                    return new Variable(name, mutableGlobals[name]);
                }

                string localName = currentFunction + "." + name;
                if (constantVariables.TryGetValue(localName, out int localVal))
                {
                    return new Constant(localVal);
                }

                // A PARAMETER of a plain (non-@inline) function shadows a module global
                // of the same name -- Python scoping. Without this, a user-level
                // `start_low_ms = 250` hijacked every read of dht_read's start_low_ms
                // parameter and the driver held its start pulse for 250 ms. Only
                // parameters can collide here: assigning a global-named local inside a
                // non-main function is already a NameError, and `main` IS the module's
                // top level, where the qualified name and the global are one binding.
                if (currentFunction != "main"
                    && variableTypes.TryGetValue(localName, out var localDt))
                {
                    return new Variable(localName, localDt);
                }
            }

            string moduleGlobal = currentModulePrefix + name;
            if (mutableGlobals.TryGetValue(moduleGlobal, out var modType))
            {
                return new Variable(moduleGlobal, modType);
            }

            if (constantVariables.TryGetValue(moduleGlobal, out int modVal))
            {
                return new Constant(modVal);
            }

            if (mutableGlobals.TryGetValue(name, out var bareType))
            {
                return new Variable(name, bareType);
            }

            if (constantVariables.TryGetValue(name, out int bareVal))
            {
                return new Constant(bareVal);
            }
        }

        if (!string.IsNullOrEmpty(currentInlinePrefix))
        {
            string inlineName = currentInlinePrefix + name;
            if (constantVariables.TryGetValue(inlineName, out int inlineVal))
            {
                // Same note as the read below: a name in both maps (a compile-time
                // string bound through an inline parameter) must carry its text, or
                // the value arrives downstream as a bare interned id.
                return new Constant(inlineVal, ResolveStrConstant(inlineName));
            }

            if (constantAddressVariables.TryGetValue(inlineName, out int inlineAddr))
            {
                DataType inlineDt = DataType.UINT8;
                if (variableTypes.TryGetValue(inlineName, out var inlineType))
                    inlineDt = inlineType;
                else if (variableTypes.TryGetValue(name, out var globalType))
                    inlineDt = globalType;
                return new MemoryAddress(inlineAddr, inlineDt);
            }
        }

        if (!string.IsNullOrEmpty(currentFunction) && string.IsNullOrEmpty(currentInlinePrefix))
        {
            string localName = currentFunction + "." + name;
            if (constantVariables.TryGetValue(localName, out int localVal))
            {
                // Same note as the inline-prefix and finalLocalName reads: a name in both
                // maps (a compile-time string bound as a function-scope constant, e.g. a
                // loop variable over a literal) must carry its text, or the value arrives
                // downstream as a bare character code / interned id and `ch.lower()`
                // answers "an integer" (adafruit_ht16k33's _put, reached through _push).
                return new Constant(localVal, ResolveStrConstant(localName));
            }
        }

        string mg = currentModulePrefix + name;
        if (constantVariables.TryGetValue(mg, out int mgVal))
            return new Constant(mgVal);
        if (constantVariables.TryGetValue(name, out int nameVal))
            return new Constant(nameVal);

        // Resolve bare names that refer to mutable globals in the current module
        // (e.g. `_num_tasks` inside rtos.py functions, where the IR key is `rtos__num_tasks`).
        if (!string.IsNullOrEmpty(currentModulePrefix))
        {
            string modKey2 = currentModulePrefix + name;
            string localFnKey = string.IsNullOrEmpty(currentFunction) ? "" : currentFunction + "." + name;
            bool hasLocalDecl = !string.IsNullOrEmpty(localFnKey) &&
                                (variableTypes.ContainsKey(localFnKey) || constantVariables.ContainsKey(localFnKey));
            if (!hasLocalDecl && mutableGlobals.TryGetValue(modKey2, out var modType2))
                return new Variable(modKey2, modType2);
        }

        // `from module import sym` where sym is a mutable global (e.g. `from machine import mem8`)
        if (TryImportedAlias(name, out var importedAliasMod) && importedAliasMod != null)
        {
            var origName = AliasOriginal(name);
            string importedPrefix = importedAliasMod.Replace('.', '_') + "_";
            string importedKey = importedPrefix + origName;
            if (mutableGlobals.TryGetValue(importedKey, out var importedAliasType))
                return new Variable(importedKey, importedAliasType);
        }

        foreach (var mod in modules)
        {
            // `mod_member` mangling collides with ordinary user names: a program that
            // imports `time` and declares `time_alarm` makes `alarm` resolve to that
            // variable's slot here, exactly as `time.alarm` would if the module had
            // one. Membership has to come from the module's own scope -- the flat
            // tables cannot tell a module member from a user global of the same
            // spelling.
            if (!mod.Value.Globals.ContainsKey(name) && !mod.Value.MutableGlobals.ContainsKey(name))
                continue;
            string mangledMod = mod.Key.Replace('.', '_');
            string modKey = mangledMod + "_" + name;
            if (globals.TryGetValue(modKey, out var modSym))
            {
                if (modSym.IsMemoryAddress)
                    return new MemoryAddress(modSym.Value, modSym.Type);
                return new Constant(modSym.Value);
            }

            if (mutableGlobals.TryGetValue(modKey, out var modMutType))
            {
                return new Variable(modKey, modMutType);
            }
        }

        string finalLocalName = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + name
            : currentFunction + "." + name;

        // A name bound to a compile-time FLOAT answers with the float, the way a name bound to
        // a compile-time integer answers with the integer one line below (#374). It did not, so
        // the read fell through to a run-time slot nothing writes and `self._timeout = timeout`
        // stored 0.0 for a parameter whose default says 0.1. Asked BEFORE the integer map,
        // which never holds a float.
        if (floatConstantVariables.TryGetValue(finalLocalName, out double finFloat))
            return new FloatConstant(finFloat);

        if (constantVariables.TryGetValue(finalLocalName, out int finVal))
        {
            // A name bound to a one-character string is in BOTH maps: the numeric one holds its
            // character code and the string one holds its text. The numeric lookup wins here, so
            // without carrying the text the read arrives downstream as a bare number.
            return new Constant(finVal, ResolveStrConstant(finalLocalName));
        }

        // WHAT THE LOCAL HOLDS (#331) -- REVERTED, and the reason is written down so the next
        // attempt starts from it rather than from the measurement alone.
        //
        // Answering reads from `localConstantValues` is worth 6 894 bytes over the corpus (84
        // fixtures smaller, none bigger) and it is not sound as the map stands. The map records
        // what a name held along the LINE that was just lowered; a read is only entitled to it
        // when that value holds on EVERY PATH THAT REACHES THE READ, and three shapes break
        // that and were measured breaking it:
        //
        //   * a FLOAT local, whose value is not an integer at all: the map is integer-only, so
        //     `x: float = 1.0` answered a read with the integer 1 and the float probes read
        //     OCR0A as 0x00 where 1.0's MSB is 0x3F;
        //   * a MIXED SIGNED/UNSIGNED comparison, which came out {1, 0} where the program
        //     computes {1, 1};
        //   * fixtures/compat-cp-keypad, which stops reaching its second BREAK. Its region
        //     between the two is straight-line with no call and no backward jump, so the
        //     escape is not in the code the fold emitted and is not yet understood.
        //
        // Two OTHER shapes it broke are fixed and stay fixed, because they were defects of
        // their own that nothing had reached: the run-time list and string loops never
        // invalidated what their body writes, an unrolled body with `continue`/`break` has
        // paths the linear lowering does not walk, and a dict miss the program catches must
        // raise rather than refuse.
        //
        // PyMCU#331 and #370 carry this. The precondition to implement is the one stated
        // above, and it needs the map to know the paths, which today it does not.

        string? strVal = ResolveStrConstant(finalLocalName);
        if (strVal != null)
        {
            // The text rides along with the id. It matters most for a ONE-character string,
            // whose id here is an interned number while the same literal in expression position
            // is its character code -- two encodings of one string, which a comparison by value
            // reads as unequal.
            if (stringLiteralIds.TryGetValue(strVal, out int strId))
            {
                return new Constant(strId, strVal);
            }

            int newId = nextStringId++;
            stringLiteralIds[strVal] = newId;
            stringIdToStr[newId] = strVal;
            return new Constant(newId, strVal);
        }

        DataType type = DataType.UINT8;
        if (variableTypes.TryGetValue(finalLocalName, out var dt))
            type = dt;

        int dotCount = finalLocalName.Count(c => c == '.');
        if (dotCount >= 2)
        {
            // A name that already carries a caller's scope (`rainbow_cycle.iret_1_0`,
            // a tuple-result slot passed on as a sequence element) is not a local of
            // this expansion -- the qualified spelling IS the binding. Source text
            // cannot spell a dotted name, so only compiler-built references reach this.
            if (name.Contains('.') && variableTypes.TryGetValue(name, out var fqSlotDt))
                return new Variable(name, fqSlotDt);

            string resolved = finalLocalName;
            string lastNonTemp = finalLocalName;
            for (int depth = 0; depth < 20; depth++)
            {
                if (!variableAliases.TryGetValue(resolved, out string next)) break;
                if (Temporary.IsScratchName(next))
                {
                    if (constantVariables.TryGetValue(next, out int tmpVal)) return new Constant(tmpVal);
                    if (constantAddressVariables.TryGetValue(next, out int tmpAddr))
                        return new MemoryAddress(tmpAddr,
                            variableTypes.TryGetValue(next, out var tmpDt) ? tmpDt : DataType.UINT16);
                    break;
                }

                resolved = next;
                lastNonTemp = resolved;
            }

            if (lastNonTemp != finalLocalName)
            {
                if (constantVariables.TryGetValue(lastNonTemp, out int lstVal)) return new Constant(lstVal);
                if (constantAddressVariables.TryGetValue(lastNonTemp, out int lstAddr))
                    return new MemoryAddress(lstAddr,
                        variableTypes.TryGetValue(lastNonTemp, out var lstDt) ? lstDt : DataType.UINT16);
                DataType resolvedType = DataType.UINT8;
                if (variableTypes.TryGetValue(lastNonTemp, out var lastDt)) resolvedType = lastDt;
                else if (mutableGlobals.TryGetValue(lastNonTemp, out var lastGlobalDt)) resolvedType = lastGlobalDt;
                return new Variable(lastNonTemp, resolvedType);
            }
        }

        // Closure capture: a nested @inline function may read a variable from the ENCLOSING
        // function (e.g. `return x + base`, where base is a local of the caller). Such a free
        // variable is not bound under this inline prefix, and the earlier enclosing-scope lookup
        // only runs when no inline prefix is active — so without this it fell through to an
        // unbound (zero) local, silently dropping the capture. Resolve it to the enclosing
        // function's binding when the inline-qualified name is genuinely unknown.
        if (!string.IsNullOrEmpty(currentInlinePrefix) && !variableTypes.ContainsKey(finalLocalName))
        {
            // Candidate enclosing scopes, innermost first: each enclosing inline expansion's
            // prefix (so a capture from an enclosing @inline like `outer` resolves), then the
            // enclosing plain function. The topmost stack entry is THIS expansion — skip it.
            for (int si = inlineStack.Count - 2; si >= 0; --si)
            {
                string p = inlineStack[si].Prefix;
                if (string.IsNullOrEmpty(p)) continue;
                string enc = p + name;
                if (enc == finalLocalName) continue;
                if (constantVariables.TryGetValue(enc, out int ec)) return new Constant(ec);
                if (constantAddressVariables.TryGetValue(enc, out int ea2))
                    return new MemoryAddress(ea2, variableTypes.TryGetValue(enc, out var ead) ? ead : DataType.UINT16);
                if (variableTypes.TryGetValue(enc, out var et)) return new Variable(enc, et);
            }

            if (!string.IsNullOrEmpty(currentFunction))
            {
                string enclosing = currentFunction + "." + name;
                if (enclosing != finalLocalName)
                {
                    if (constantVariables.TryGetValue(enclosing, out int encConst)) return new Constant(encConst);
                    if (constantAddressVariables.TryGetValue(enclosing, out int encAddr))
                        return new MemoryAddress(encAddr,
                            variableTypes.TryGetValue(enclosing, out var encAddrDt) ? encAddrDt : DataType.UINT16);
                    if (variableTypes.TryGetValue(enclosing, out var encType)) return new Variable(enclosing, encType);
                }
            }
        }

        // Nothing above resolved the name, and two very different situations reach this line.
        // One is a binding this generator holds under some other key: an inline handler's
        // parameter, a type-annotated instance, a tuple result slot, a runtime-bounded slice.
        // The other is a name the program never defines -- a typo, which used to become a read
        // of a slot nobody ever wrote, so the firmware shipped with whatever the RAM held and
        // no diagnostic said a word. Invent the local only for the first.
        // A plain FUNCTION named as a VALUE -- an interrupt handler handed to an @inline
        // helper that lives in another module. Functions are registered under the prefix of
        // the module that DEFINES them, and only the bare name was looked for, so the handler
        // was reported undefined in the very file that defines it, forty lines below the
        // definition (#321). Any HAL module that owns an interrupt has to put it on a pin, and
        // the pin table lives in the GPIO module, so this is the shape that reaches it.
        if (!string.IsNullOrEmpty(currentModulePrefix)
            && !IsNameKnownSomewhere(finalLocalName, name)
            && (functionParams.ContainsKey(currentModulePrefix + name)
                || inlineFunctions.ContainsKey(currentModulePrefix + name)))
            return new Variable(currentModulePrefix + name, DataType.UINT16);

        if (!IsNameKnownSomewhere(finalLocalName, name))
        {
            // The name may be missing because its defining module REFUSED this target: a
            // module-level `raise CompileError(...)` guard whose enclosing if/match folded
            // away, so none of the module's symbols exist. The call path has reported that
            // properly for a while (see EmitRegularFunctionCall); a plain name read did not,
            // and answered "name 'uart_init' is not defined" for an internal helper the user
            // never wrote, on a chip whose HAL had said in one sentence why it cannot be built.
            foreach (var g in moduleGuardErrors.OrderByDescending(kv => kv.Key.Length))
                if (finalLocalName.StartsWith(g.Key, StringComparison.Ordinal)
                    || currentModulePrefix.StartsWith(g.Key, StringComparison.Ordinal))
                    // THE CARET GOES ON THE GUARD, and the sentence names where it was reached
                    // from. That reverses what this site did until #241, because the old
                    // arrangement was right about user code and wrong about the library.
                    //
                    // It reported at the READ that failed, on the reasoning that the caret
                    // belongs in the program in front of the reader. That holds when the read is
                    // in their file. It fails here: for `AnalogPin("A0")` on an ATtiny 4313 the
                    // failing read is inside the HAL's own class, so the reader was sent to
                    // adc/__init__.py:36, a CORRECT line, in a file they had never opened, for a
                    // decision taken fourteen lines earlier.
                    //
                    // The guard is the one line in these modules a user is meant to read, and
                    // its text IS the explanation. That the old message had to name it in prose
                    // was the tell: a diagnostic spelling out a location in its sentence is one
                    // that had the position and did not use it.
                    throw ModuleGuardError(g.Value, at);

            // A probing caller gets null and reaches its own, more specific diagnostic. Note
            // this sits AFTER the module-guard check above, which throws for everyone.
            if (probe) return null;

            // A name bound by `except ... as` IS defined, by the handler it is read inside, so
            // the sentence below would be false about it and would send the reader looking for
            // a missing assignment. What is missing is not the binding, it is a meaning for
            // this use of it: the object is bounded, and naming the four things it supports is
            // what ends the search (#369).
            if (TryGetExceptionBinding(name, out var exnBinding))
            {
                // `except (A, B) as e` and a bare `except: as`-less clause leave no single type
                // to name, so the example is written with the one the handler declared only
                // when there is exactly one.
                string oneType = exnBinding.ExnType.Contains(',') || exnBinding.ExnType.Length == 0
                    ? "" : $", and isinstance({name}, {exnBinding.ExnType})";
                throw UserError(
                    $"'{name}' is the exception this handler caught, and the only things it "
                    + $"carries are its message and its type: print({name}), str({name}), "
                    + $"{name}.args[0]{oneType}. There is no exception object to store, "
                    + "pass on, or keep past the handler.", at);
            }

            throw UserError(
                $"name '{name}' is not defined -- it is read here but never assigned, " +
                "imported, or received as a parameter" + StarImportHint(name), at);
        }

        return new Variable(finalLocalName, type);
    }

    /// <summary>
    /// True when some table in this generator knows <paramref name="qualified"/> or the bare
    /// <paramref name="name"/>, under any of the prefixes a binding can be filed by. Used at the
    /// end of <see cref="ResolveBinding"/> to tell "bound through another path" apart from
    /// "never defined": the question is deliberately asked of every table rather than of
    /// variableTypes alone, because the paths that legitimately reach the fallback file their
    /// bindings elsewhere (aliases, instance classes, arrays, literal params).
    /// </summary>
    private bool IsNameKnownSomewhere(string qualified, string name)
    {
        // Compiler-generated names (temporaries, anonymous constructor targets, inline result
        // slots) are never user-written, so they can never be a typo.
        if (Temporary.IsScratchName(name) || name.StartsWith("__c") || name.StartsWith("__slc")
            || name.StartsWith("__unpack") || name.StartsWith("_irq_synth_"))
            return true;

        // `self` is bound by the expansion machinery, not by any statement in the source.
        if (name == "self") return true;

        // A Python builtin: whatever is wrong with the call, the diagnostic that names the
        // builtin (VisitCall, via UnsupportedBuiltins) is better than "not defined" -- a builtin
        // is in scope everywhere, so it was never going to be assigned or imported. The full
        // builtins namespace is used, not a hand-kept subset, so `sorted` and `oct` reach the
        // same diagnostic `round` and `isinstance` do.
        if (PythonBuiltins.Contains(name)) return true;

        var keys = new List<string> { qualified, name };
        if (!string.IsNullOrEmpty(currentFunction)) keys.Add(currentFunction + "." + name);
        if (!string.IsNullOrEmpty(currentModulePrefix)) keys.Add(currentModulePrefix + name);
        if (!string.IsNullOrEmpty(currentInlinePrefix)) keys.Add(currentInlinePrefix + name);
        foreach (var frame in inlineStack)
            if (!string.IsNullOrEmpty(frame.Prefix)) keys.Add(frame.Prefix + name);

        foreach (var key in keys)
        {
            if (variableTypes.ContainsKey(key)) return true;
            if (constantVariables.ContainsKey(key)) return true;
            if (constantAddressVariables.ContainsKey(key)) return true;
            if (strConstantVariables.ContainsKey(key)) return true;
            // A name bound ONLY as a float constant is bound (#374). Every other binding table
            // is asked here and this one was not, so a parameter whose float default the call
            // omitted was reported as never received -- inside the function that declares it.
            if (floatConstantVariables.ContainsKey(key)) return true;
            if (mutableGlobals.ContainsKey(key)) return true;
            if (globals.ContainsKey(key)) return true;
            if (variableAliases.ContainsKey(key)) return true;
            if (instanceClasses.ContainsKey(key)) return true;
            if (listLiteralParams.ContainsKey(key)) return true;
            if (dictLiteralBindings.ContainsKey(key)) return true;
            if (setLiteralBindings.ContainsKey(key)) return true;
            if (runtimeStrVars.ContainsKey(key)) return true;
            if (funcrefReturnTypes.ContainsKey(key)) return true;
            if (loopFunctionAliases.ContainsKey(key)) return true;
            if (noneValuedNames.Contains(key)) return true;
            if (declaredConstants.Contains(key)) return true;
            if (bytearrayParams.Contains(key)) return true;
            if (arraysWithVariableIndex.Contains(key)) return true;
            if (moduleSramArrays.Contains(key)) return true;
            if (boundNames.Contains(key)) return true;
        }

        // A name that denotes something other than a variable: a class, a function, an import.
        // Imported classes are filed under the mangled key (adafruit_ina219_INA219) in
        // classDirectMethods / classFieldLayout, while classNames holds the bare spelling.
        // A synthesized `type(inst)` that still carries the key must count as defined.
        if (classNames.Contains(name) || IsImportedAlias(name)
            || aliasToOriginal.ContainsKey(name) || inlineFunctions.ContainsKey(name)
            || functionParams.ContainsKey(name) || functionReturnTypes.ContainsKey(name)
            || externFunctionMap.ContainsKey(name)
            || classDirectMethods.ContainsKey(name) || classFieldLayout.ContainsKey(name)
            || classDirectMethods.ContainsKey(qualified) || classFieldLayout.ContainsKey(qualified))
            return true;

        // Filed under a module prefix by whichever module declared it.
        foreach (var mod in modules)
        {
            string modKey = mod.Key.Replace('.', '_') + "_" + name;
            if (globals.ContainsKey(modKey) || mutableGlobals.ContainsKey(modKey)
                || variableTypes.ContainsKey(modKey) || constantVariables.ContainsKey(modKey))
                return true;
        }

        return false;
    }

    // Resolve a variable name used as an asm() constraint operand.
    // Unlike ResolveBinding, this always returns a Variable (never a Constant)
    // so that the backend can load and then store back the modified value.
    private Val ResolveAsmOperand(string name)
    {
        // The assembly may write it, and nothing here can say whether it does.
        ForgetLocalConstant(name);

        string localName = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + name
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + name : name);

        DataType type = DataType.UINT8;
        if (variableTypes.TryGetValue(localName, out var dt))
            type = dt;
        else if (variableTypes.TryGetValue(name, out var dt2))
            type = dt2;

        return new Variable(localName, type);
    }

    private string? ResolveStrConstant(string name)
    {
        string bare = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;

        // A parameter or local of the scope being compiled shadows a same-named
        // module-level string, exactly as Python scoping works (#438). A bare probe has
        // to ask this BEFORE the walk below: `b` would otherwise hit the global's own
        // entry on the first step, so `def f(b: uint8)` read next to a module-level
        // `b = "world"` answered with the text. A scoped binding that IS a string
        // answers with its own text -- the local wins over the global there too.
        if (name == bare && ScopedStrBinding(bare, out string? scopedText))
            return scopedText;

        var key = name;
        for (var depth = 0; depth < 20; depth++)
        {
            if (key != null && strConstantVariables.TryGetValue(key, out var val)) return val;
            if (key != null && variableAliases.TryGetValue(key, out var alias)) key = alias;
            else break;
        }

        // The same shadow one level out: the alias walk settled on (or the probe itself
        // was) a name bound to something that is not a compile-time string -- a typed
        // variable, a buffer, an instance. A parameter inlined to "f.b" through
        // variableAliases lands here. The fallbacks below must not read a same-named
        // global's text for it (#438).
        //
        // One exception: a module-level string carries TWO bindings that disagree about
        // whether it is "non-string". The bare name is the run-time slot (the interned
        // id, a uint8 in mutableGlobals), while the text is filed under the entry
        // function's scope spelling `main.S` -- module-level statements lower inside
        // `main`, and Assign files the text as `currentFunction + "." + name`. A probe
        // that settles on that bare name -- written bare at module level, or reached
        // through an inline parameter's alias (`pixel_order` -> `ORDER`) -- IS the
        // global itself, not a scope shadowing it, so the `main.S` probe runs before
        // BindsNonString can rule the name non-string on the strength of its own
        // storage slot. An owning module's same-named global keeps precedence: inside
        // `helper`'s code a bare `S` means `helper_S`, which the ordinary fallbacks
        // below already find.
        if (key != null && !key.Contains('.') && !Temporary.IsScratchName(key)
            && (mutableGlobals.ContainsKey(key) || globals.ContainsKey(key))
            && !OwningModulePrefixes().Any(mp =>
                mutableGlobals.ContainsKey(mp + key) || globals.ContainsKey(mp + key)
                || strConstantVariables.ContainsKey(mp + key))
            && strConstantVariables.TryGetValue("main." + key, out var ownText))
            return ownText;

        if (key != null && BindsNonString(key)) return null;

        // Fall back to the module-global / bare-name forms, mirroring how integer globals
        // resolve (ResolveBinding): a qualified `localName` like "main.S" should still find a
        // module-level string `S` registered by ScanGlobals as `currentModulePrefix + "S"`.
        if (strConstantVariables.TryGetValue(currentModulePrefix + bare, out var mv)) return mv;
        if (strConstantVariables.TryGetValue(bare, out var bv)) return bv;

        // `from m import banner`: the text is filed under the DEFINING module's key, which is
        // the only storage the name has (see the import loop in Generate). Reading it under
        // the bare name alone answered with no text at all, and print wrote an empty line.
        if (ImportedGlobalKey(bare) is { } importedKey
            && strConstantVariables.TryGetValue(importedKey, out var iv)) return iv;

        return null;
    }

    /// <summary>
    /// The compile-time string the scope being compiled binds <paramref name="bare"/> to
    /// (#438). True with <paramref name="text"/> set when a scoped binding is a string; true
    /// with <paramref name="text"/> null when a scoped binding is anything else -- a
    /// parameter, a typed local, a buffer -- because the local shadows any same-named global,
    /// exactly as Python scoping works. False when no scope binds the name at all.
    /// </summary>
    private bool ScopedStrBinding(string bare, out string? text)
    {
        text = null;
        foreach (var scoped in LocalScopeKeys(bare))
        {
            var k = scoped;
            var aliased = false;
            for (var depth = 0; depth < 20; depth++)
            {
                if (strConstantVariables.TryGetValue(k, out var s)) { text = s; return true; }
                if (variableAliases.TryGetValue(k, out var a) && a != null) { k = a; aliased = true; continue; }
                break;
            }
            // An alias that ends nowhere still IS the scope's binding of the name.
            if (aliased || BindsNonString(k)) return true;
        }
        return false;
    }

    /// <summary>
    /// Every qualified key <paramref name="bare"/> could be bound under in the scope being
    /// compiled, most local first -- the same order <see cref="MultiStrKeys"/> enumerates for
    /// the same question.
    /// </summary>
    private IEnumerable<string> LocalScopeKeys(string bare)
    {
        if (!string.IsNullOrEmpty(currentInlinePrefix)) yield return currentInlinePrefix + bare;
        // `main` is the module's top level: `main.b` and `b` spell one binding, not a
        // scope over it, so the function key would only shadow the module's own table.
        if (!string.IsNullOrEmpty(currentFunction) && currentFunction != "main")
            yield return currentFunction + "." + bare;
        if (!string.IsNullOrEmpty(currentModulePrefix)) yield return currentModulePrefix + bare;
    }

    /// <summary>
    /// Whether <paramref name="key"/> is bound at all to something that is not a compile-time
    /// string: a typed variable, a constant, a buffer, an instance, a runtime or multi string.
    /// Callers ask this only after the string tables have already missed, so a hit means the
    /// name stands for a value a bare-name fallback must never read a global's text for
    /// (#438). Mirrors the binding tables <see cref="IsDefined"/> consults, minus the string
    /// ones.
    /// </summary>
    private bool BindsNonString(string key) =>
        variableTypes.ContainsKey(key)
        || constantVariables.ContainsKey(key)
        || floatConstantVariables.ContainsKey(key)
        || constantAddressVariables.ContainsKey(key)
        || mutableGlobals.ContainsKey(key)
        || globals.ContainsKey(key)
        || boundNames.Contains(key)
        || noneValuedNames.Contains(key)
        || declaredConstants.Contains(key)
        || instanceClasses.ContainsKey(key)
        || bytearrayParams.Contains(key)
        || listLiteralParams.ContainsKey(key)
        || dictLiteralBindings.ContainsKey(key)
        || setLiteralBindings.ContainsKey(key)
        || runtimeStrVars.ContainsKey(key)
        || multiStrVariables.ContainsKey(key)
        || funcrefReturnTypes.ContainsKey(key)
        || loopFunctionAliases.ContainsKey(key)
        || arraysWithVariableIndex.Contains(key)
        || moduleSramArrays.Contains(key);

    /// <summary>
    /// The defining module's key for a name this file imported with `from m import name`, or
    /// null when the name was not imported that way. The name has no storage of its own: it
    /// stands for the variable that lives in `m`.
    /// </summary>
    private string? ImportedGlobalKey(string name)
    {
        if (!TryImportedAlias(name, out var mod) || mod == null) return null;
        string original = AliasOriginal(name);
        return mod.Replace('.', '_') + "_" + original;
    }

    // --- Import aliases, scoped to the module that wrote them (#320, #324) ---------------
    //
    // `from x import C as _C` binds _C in ONE file. The flat importedAliases table is shared
    // by every module, so a second file writing `from y import D as _C` either lost its own
    // binding or stole the first one's, and the call that named _C was lowered against the
    // wrong class: a constructor that appeared to call itself, or a keyword argument whose
    // callee no longer had that parameter, so it silently took the default.
    //
    // The per-module tables hold each file's own imports. A name the file imports itself
    // resolves there; anything else falls back to the flat table, which is what the rest of
    // the pipeline (star imports, re-export chases, inline-body imports) still populates.

    private string _owningPrefixCacheKey = "";
    private string _owningPrefixCacheValue = "";

    /// <summary>
    /// The mangled prefix of the module whose import table governs the code being lowered.
    /// `currentModulePrefix` can carry a class segment inside a method, so the longest
    /// registered module prefix it starts with is the owner; the entry file is "".
    /// </summary>
    private string OwningModulePrefix()
    {
        if (_owningPrefixCacheKey == currentModulePrefix) return _owningPrefixCacheValue;
        string best = "";
        foreach (var p in perModuleImportedAliases.Keys)
            if (p.Length > best.Length && currentModulePrefix.StartsWith(p, StringComparison.Ordinal))
                best = p;
        _owningPrefixCacheKey = currentModulePrefix;
        _owningPrefixCacheValue = best;
        return best;
    }

    /// <summary>The module an imported name refers to, preferring the current module's own import.</summary>
    private bool TryImportedAlias(string name, out string? mod)
    {
        if (perModuleImportedAliases.TryGetValue(OwningModulePrefix(), out var own)
            && own.TryGetValue(name, out mod))
            return true;
        return importedAliases.TryGetValue(name, out mod);
    }

    /// <summary>Whether any module in the program imported this name.</summary>
    private bool IsImportedAlias(string name) => TryImportedAlias(name, out _) || importedAliases.ContainsKey(name);

    /// <summary>
    /// The name as the defining module spells it. When the current module imports the name
    /// WITHOUT renaming it, another module's `as` mapping must not rename it here.
    /// </summary>
    private string AliasOriginal(string name)
    {
        if (perModuleImportedAliases.TryGetValue(OwningModulePrefix(), out var own)
            && own.ContainsKey(name))
        {
            if (perModuleAliasToOriginal.TryGetValue(OwningModulePrefix(), out var ownOrig)
                && ownOrig.TryGetValue(name, out var scoped) && scoped != null)
                return scoped;
            return name;
        }
        return aliasToOriginal.TryGetValue(name, out var orig) && orig != null ? orig : name;
    }

    /// <summary>Whether the current module (or, failing that, any module) renamed this name.</summary>
    private bool HasAliasOriginal(string name)
    {
        if (perModuleImportedAliases.TryGetValue(OwningModulePrefix(), out var own)
            && own.ContainsKey(name))
            return perModuleAliasToOriginal.TryGetValue(OwningModulePrefix(), out var ownOrig)
                   && ownOrig.ContainsKey(name);
        return aliasToOriginal.ContainsKey(name);
    }

    /// <summary>
    /// `from sys import platform` (and the star form that brings it in) can only bind the
    /// compat shim's module global -- the placeholder every chip shares ("rp2" on a chip
    /// that answers "atmega328p"). The compile-time value is substituted at the
    /// `sys.platform` member access, a spelling this import never makes, so the bound name
    /// would answer the wrong platform for the whole program. Refuse it; `import sys`
    /// then `sys.platform` substitutes the real value. `from sys import implementation` is
    /// allowed: the object the shim binds carries only `name`, which is the honest answer.
    ///
    /// The layer's own `usys`/`uos` files are exempt: upstream defines them literally as
    /// `from sys import *` / `from os import *`, so their re-export of `platform` is the
    /// mechanism the member folds sit in front of, not a leak.
    /// </summary>
    private void CheckIntrospectionBinding(string importingModule, ImportStmt imp, string sym)
    {
        string tail = importingModule.Contains('.')
            ? importingModule[(importingModule.LastIndexOf('.') + 1)..]
            : importingModule;
        if (tail is "usys" or "uos" or "sys" or "os") return;
        if (imp.ModuleName is not ("sys" or "usys")) return;
        if (!IntrospectionTable.IsKnownStdlib(deviceConfig.Stdlib)) return;
        if (sym is not ("platform" or "*")) return;
        throw UserError(
            $"'from {imp.ModuleName} import {(sym == "*" ? "*" : sym)}' would bind the compat " +
            "shim's placeholder -- a bound name can only carry the module global, which is " +
            $"'rp2' for every chip. Write 'import {imp.ModuleName}' and read " +
            $"{imp.ModuleName}.platform -- the compiler substitutes the real value there.", imp);
    }

    /// <summary>Record `name -> module` (and its original spelling) in one module's own table.</summary>
    private void RegisterModuleAlias(string modulePrefix, string name, string? module, string? original)
    {
        if (!perModuleImportedAliases.TryGetValue(modulePrefix, out var tbl))
            perModuleImportedAliases[modulePrefix] = tbl = new Dictionary<string, string?>();
        tbl[name] = module;
        _owningPrefixCacheKey = "";
        if (original == null) return;
        if (!perModuleAliasToOriginal.TryGetValue(modulePrefix, out var otbl))
            perModuleAliasToOriginal[modulePrefix] = otbl = new Dictionary<string, string?>();
        otbl[name] = original;
    }

    // --- Strings whose value is decided at run time (issue #145) -------------------------
    //
    // A str is a compile-time value in PyMCU: there is no string type, only an interned id
    // that the writers turn back into flash bytes. When two paths bind the SAME name to
    // different texts, no single text is right at a later read -- and folding one of them is
    // how `s = "idle"; if x: s = "running"; print(s)` printed "idle" on every path.
    //
    // What lives at run time is the id, in a 16-bit variable. It is stored at each binding
    // site (only for the names CollectMultiStrNames flagged: one binding still folds), and a
    // read that needs the TEXT dispatches over the ids the name can hold.

    /// <summary>The storage key a str binding of <paramref name="name"/> is filed under.</summary>
    private string StrBindingKey(string name)
    {
        if (!string.IsNullOrEmpty(currentInlinePrefix)) return currentInlinePrefix + name;
        if (!string.IsNullOrEmpty(currentFunction))
        {
            if (currentFunctionGlobals.Contains(name) && mutableGlobals.ContainsKey(currentModulePrefix + name))
                return currentModulePrefix + name;
            if (multiStrCandidates.ContainsKey(currentFunction + "." + name))
                return currentFunction + "." + name;
            if (mutableGlobals.ContainsKey(currentModulePrefix + name)) return currentModulePrefix + name;
            return currentFunction + "." + name;
        }
        return currentModulePrefix + name;
    }

    /// <summary>
    /// The candidate texts of a name whose string value is decided at run time, if it is one.
    /// <paramref name="materialized"/> says whether the id is actually stored at every binding
    /// site: only then can a read dispatch on it -- otherwise the slot holds whatever the RAM
    /// held, and the read has to be refused instead.
    /// </summary>
    private bool TryGetMultiStr(string name, out string key, out List<string> values, out bool materialized)
    {
        foreach (var k in MultiStrKeys(name))
        {
            // An unconditional rebind (`s = "third"` outside any branch) gives the name a
            // single value again, and that value is the right one to fold from there on.
            if (strConstantVariables.ContainsKey(k)) break;

            if (multiStrVariables.TryGetValue(k, out var vals))
            {
                key = k;
                values = vals;
                materialized = multiStrCandidates.ContainsKey(k);
                return true;
            }

            // A parameter or local bound to a non-string shadows any same-named global the
            // remaining keys would find, exactly as Python scoping works (#438) -- the same
            // guard ResolveStrConstant's bare-name fallback asks.
            if (BindsNonString(k)) break;
        }

        key = "";
        values = new List<string>();
        materialized = false;
        return false;
    }

    /// <summary>Every key a str binding of <paramref name="name"/> could be filed under.</summary>
    private IEnumerable<string> MultiStrKeys(string name)
    {
        if (!string.IsNullOrEmpty(currentInlinePrefix)) yield return currentInlinePrefix + name;
        if (!string.IsNullOrEmpty(currentFunction)) yield return currentFunction + "." + name;
        if (!string.IsNullOrEmpty(currentModulePrefix)) yield return currentModulePrefix + name;
        yield return name;
        // `from m import state`: the slot belongs to m, and so does what is recorded about it.
        if (ImportedGlobalKey(name) is { } importedKey) yield return importedKey;
    }

    /// <summary>
    /// Records that <paramref name="key"/> holds one of <paramref name="values"/> at run time,
    /// and stops it being a compile-time string. Marking is one-way: once two paths disagree,
    /// no later single value makes the earlier read right again.
    /// </summary>
    private void MarkMultiStr(string key, IEnumerable<string?> values)
    {
        if (!multiStrVariables.TryGetValue(key, out var known))
            multiStrVariables[key] = known = new List<string>();
        foreach (var v in values)
            if (v != null && !known.Contains(v)) known.Add(v);
        strConstantVariables.Remove(key);
        if (known.Count > 0) variableTypes[key] = DataType.UINT16;
        if (mutableGlobals.ContainsKey(key)) mutableGlobals[key] = DataType.UINT16;
    }

    /// <summary>
    /// The 16-bit slot a str binding must be stored into, or null when the name is bound to
    /// one text only (then the fold is always right and nothing is stored). Emitting the store
    /// does not commit to reading it: while the name still has a single compile-time value
    /// every read folds and the store is dead, which the optimizer removes.
    /// </summary>
    private Val? MultiStrStoreTarget(string name)
    {
        string key = StrBindingKey(name);
        if (!multiStrCandidates.ContainsKey(key)) return null;
        variableTypes[key] = DataType.UINT16;
        if (mutableGlobals.ContainsKey(key)) mutableGlobals[key] = DataType.UINT16;
        return new Variable(key, DataType.UINT16);
    }

    /// <summary>
    /// The refusal for a use of a run-time-decided string that PyMCU cannot lower. It names the
    /// texts the name can hold, because the whole difficulty is that the reader of the source
    /// sees several and the compiler used to pick one of them in silence.
    /// </summary>
    /// <param name="at">
    /// The READ the compiler could not lower, which is the position the reader has to change.
    /// The assignments that gave the name its several texts are elsewhere and are already
    /// named in the message by their texts.
    /// </param>
    private Exception MultiStrUseError(string name, List<string> values, ASTNode? at = null)
    {
        string shown = values.Count switch
        {
            0 => "",
            1 => $"\"{values[0]}\"",
            _ => string.Join(" or ", values.Select(v => $"\"{v}\"")),
        };
        return UserError(
            $"'{name}' has no single compile-time value here: it is {shown} depending on the "
            + "path taken. A PyMCU string is a compile-time value, so a name that holds "
            + "different texts on different paths can only be printed (print / write_str / "
            + "println) or compared with a literal (== / !=).", at);
    }

    /// <summary>The interned id a string literal is lowered to (see VisitExpression).</summary>

    /// Whether any `except ... as` appears anywhere in the program, the entry module and every
    /// import alike. A handler in a library the program never calls still counts: the decision
    /// is taken before dead code is eliminated, so it errs towards emitting the store rather
    /// than towards a read of a word nothing ever wrote.
    private static bool ProgramBindsExceptionObject(
        PyMCU.Frontend.ProgramNode main,
        IEnumerable<PyMCU.Frontend.ProgramNode> imported)
    {
        bool found = false;

        void Walk(PyMCU.Frontend.Statement? s)
        {
            // The shared walk does not descend into defs/classes; the old recursion
            // did, and it did not descend into `with` bodies at all.
            foreach (var st in TypeInference.WalkStatements(s))
            {
                if (found) return;
                switch (st)
                {
                    case PyMCU.Frontend.TryStmt t:
                        if (t.HandlerNames.Any(n => n != null)) { found = true; return; }
                        break;
                    case PyMCU.Frontend.FunctionDef fd: Walk(fd.Body); break;
                    case PyMCU.Frontend.ClassDef cd: Walk(cd.Body); break;
                }
            }
        }

        void WalkProgram(PyMCU.Frontend.ProgramNode p)
        {
            foreach (var st in p.GlobalStatements) Walk(st);
            foreach (var fn in p.Functions) Walk(fn.Body);
        }

        WalkProgram(main);
        foreach (var m in imported) WalkProgram(m);
        return found;
    }

    /// Whether any raise carries a non-literal message. The printer has to exist before
    /// the first <c>print(e)</c> is lowered, which may be in a function compiled before
    /// the raise itself (#435).
    private static bool ProgramHasDynamicRaiseMessage(
        PyMCU.Frontend.ProgramNode main,
        IEnumerable<PyMCU.Frontend.ProgramNode> imported)
    {
        bool found = false;

        void Walk(PyMCU.Frontend.Statement? s)
        {
            foreach (var st in TypeInference.WalkStatements(s))
            {
                if (found) return;
                switch (st)
                {
                    case PyMCU.Frontend.RaiseStmt r:
                        if (r.MessageExpr != null || !string.IsNullOrEmpty(r.MessageName))
                        { found = true; return; }
                        break;
                    case PyMCU.Frontend.FunctionDef fd: Walk(fd.Body); break;
                    case PyMCU.Frontend.ClassDef cd: Walk(cd.Body); break;
                }
            }
        }

        void WalkProgram(PyMCU.Frontend.ProgramNode p)
        {
            foreach (var st in p.GlobalStatements) Walk(st);
            foreach (var fn in p.Functions) Walk(fn.Body);
        }

        WalkProgram(main);
        foreach (var m in imported) WalkProgram(m);
        return found;
    }

    /// Whether any raise in the program carries a message at all -- a literal
    /// (`raise ValueError("x")`), a named constant, or a deferred-print expression.
    /// The unhandled-exception report only pays for the tail when one exists.
    private static bool ProgramRaisesWithMessage(
        PyMCU.Frontend.ProgramNode main,
        IEnumerable<PyMCU.Frontend.ProgramNode> imported)
    {
        bool found = false;

        void Walk(PyMCU.Frontend.Statement? s)
        {
            foreach (var st in TypeInference.WalkStatements(s))
            {
                if (found) return;
                switch (st)
                {
                    case PyMCU.Frontend.RaiseStmt r:
                        if (!string.IsNullOrEmpty(r.Message) || r.MessageExpr != null
                            || !string.IsNullOrEmpty(r.MessageName))
                        { found = true; return; }
                        break;
                    case PyMCU.Frontend.FunctionDef fd: Walk(fd.Body); break;
                    case PyMCU.Frontend.ClassDef cd: Walk(cd.Body); break;
                }
            }
        }

        void WalkProgram(PyMCU.Frontend.ProgramNode p)
        {
            foreach (var st in p.GlobalStatements) Walk(st);
            foreach (var fn in p.Functions) Walk(fn.Body);
        }

        WalkProgram(main);
        foreach (var m in imported) WalkProgram(m);
        return found;
    }

    private int StringIdOf(string text)
    {
        if (text.Length == 1) return text[0];
        if (stringLiteralIds.TryGetValue(text, out int id)) return id;
        id = nextStringId++;
        stringLiteralIds[text] = id;
        stringIdToStr[id] = text;
        return id;
    }

    // Interns a compile-time string value as a null-terminated FlashData entry,
    // returning the flash array name.  Reuses an existing entry if the same string
    // was interned before.  Registers the entry in flashArrays / arraySizes /
    // arrayElemTypes so that ArrayLoadFlash works for runtime-indexed access.
    private int _flashStrCounter;
    private readonly Dictionary<string, string> _flashStrCache = new();
    private readonly List<FlashData> _pendingFlashData = new();

    private string InternStringAsFlash(string value)
    {
        if (_flashStrCache.TryGetValue(value, out var existing))
            return existing;

        var name = $"__cstr_{_flashStrCounter++}";
        var bytes = System.Text.Encoding.ASCII.GetBytes(value)
            .Select(b => (int)b)
            .Append(0) // null terminator
            .ToList();

        _pendingFlashData.Add(new FlashData(name, bytes));
        flashArrays.Add(name);
        arraySizes[name] = bytes.Count;
        bufferLogicalLen[name] = bytes.Count;
        arrayElemTypes[name] = DataType.UINT8;

        _flashStrCache[value] = name;
        return name;
    }

    // A field's recorded class may be a HAL dispatch facade key (e.g. "pymcu_hal_gpio_Pin")
    // that re-exports the concrete chip class ("pymcu_hal_<chip>_gpio_Pin"). Return the concrete
    // classFieldLayout key: the facade itself if it has a layout, else the UNIQUE layout key that
    // matches the facade with a chip segment inserted (same "pymcu_hal_" prefix + same tail).
    // Null when unknown or ambiguous, so the caller leaves resolution untouched.
    private string? ResolveConcreteClass(string cls)
    {
        if (string.IsNullOrEmpty(cls)) return null;
        if (classFieldLayout.ContainsKey(cls)) return cls;
        const string halPfx = "pymcu_hal_";
        if (cls.StartsWith(halPfx))
        {
            // HAL facade "..._gpio_Pin" -> the unique concrete "..._<chip>_gpio_Pin".
            string tail = "_" + cls.Substring(halPfx.Length);
            string? f = UniqueClassEndingWith(cls, tail, halPfx);
            if (f != null) return f;
        }
        // Generic facade re-export (e.g. `from facade import Foo` where facade re-exported
        // it from concrete): the class isn't itself defined, so resolve to the UNIQUE
        // concrete class sharing its final symbol (the part after the last '_').
        int us = cls.LastIndexOf('_');
        if (us <= 0) return null;
        return UniqueClassEndingWith(cls, cls.Substring(us), null);
    }

    private string? UniqueClassEndingWith(string exclude, string suffix, string? requirePrefix)
    {
        string? found = null;
        foreach (var k in classFieldLayout.Keys)
            if (k != exclude && k.EndsWith(suffix) && (requirePrefix == null || k.StartsWith(requirePrefix)))
            {
                if (found != null) return null;   // ambiguous -> give up
                found = k;
            }
        return found;
    }

    /// <summary>
    /// True when the entry file builds a CLASS INSTANCE at its module level (`led = Pin(...)`,
    /// annotated or not). Only those files need main lowered before their other functions, and
    /// asking the question this narrowly is what keeps every other program on its exact current
    /// path: lowering order advances the shared label, temporary and string-literal counters,
    /// so a hoist nobody needs renumbers a program for nothing.
    /// </summary>
    private bool EntryModuleLevelBuildsInstance(ProgramNode mainAst)
    {
        foreach (var stmt in mainAst.GlobalStatements)
        {
            Expression? value = stmt switch
            {
                AssignStmt { Target: VariableExpr } a => a.Value,
                VarDecl vd => vd.Init,
                AnnAssign an => an.Value,
                _ => null,
            };

            if (value is not CallExpr call) continue;
            string cls = call.Callee switch
            {
                VariableExpr cv => ResolveCallee(cv.Name),
                MemberAccessExpr { Object: VariableExpr mo } ma when modules.ContainsKey(mo.Name)
                    => (TryImportedAlias(mo.Name, out var real) && real != null ? real : mo.Name)
                       .Replace('.', '_') + "_" + ma.Member,
                _ => "",
            };

            if (string.IsNullOrEmpty(cls)) continue;
            if (inlineFunctions.ContainsKey(cls + "___init__")
                || overloadedFunctions.Contains(cls + "___init__")) return true;
        }

        return false;
    }

    private string ResolveCallee(string name)
    {
        int dotPos = name.IndexOf('.');
        if (dotPos != -1)
        {
            string mod = name.Substring(0, dotPos);
            string func = name.Substring(dotPos + 1);
            // `import pymcu.hal.console as c` then `c.print(1)`: print is a builtin this
            // compiler lowers itself, and the module qualifier only says where the name came
            // from. Without this the call went looking for `c_print`, a symbol nothing emits.
            int lastDot = name.LastIndexOf('.');
            string qualifier = name[..lastDot];
            string member = name[(lastDot + 1)..];
            if (intrinsicNames.Contains(member)
                && (IsImportedAlias(qualifier) || modules.ContainsKey(qualifier)))
                return member;
            // `import adafruit_framebuf as framebuf` then `class C(framebuf.FrameBuffer)`:
            // the qualifier is the import alias, not the defining module. Mangling
            // `framebuf_FrameBuffer` finds no methods, so super().__init__ fell through
            // to the builtin-super refusal (adafruit_ssd1306).
            if (TryImportedAlias(qualifier, out var realMod) && realMod != null)
                return realMod.Replace('.', '_') + "_" + member;
            return mod + "_" + func;
        }

        if (intrinsicNames.Contains(name)) return name;

        // A class defined in the module being lowered shadows an import of the same
        // name that belongs to a different module. TryImportedAlias falls back to the
        // entry file's table, so `from digitalio import DigitalInOut` in main made
        // `return DigitalInOut(pin, self)` inside adafruit_74hc595.get_pin the
        // 1-argument constructor. Functions stay alias-first: a facade that re-exports
        // `i2c_write_bytes` still has to resolve `_twi_wait` through the defining
        // module's import table, and walking prefixes first returned a class key
        // that is not a function.
        if (ResolvePrefixedClass(name) is { } localClass)
            return localClass;

        // A name the CURRENT module imported resolves through ITS table first: inside
        // `I2C.write_to` (defined in the package __init__) `i2c_write_to` is that module's
        // own `from ...avr import i2c_write_to`, and the prefix walk would otherwise land
        // on the same function re-registered under the package prefix -- whose
        // functionModulePrefix is the package, so helpers in the defining module
        // (`_twi_wait` in avr.py) no longer resolve.
        if (perModuleImportedAliases.TryGetValue(OwningModulePrefix(), out var ownImports)
            && ownImports.TryGetValue(name, out var ownMod))
        {
            var mangledOwn = ownMod?.Replace('.', '_');
            var ownOriginal = AliasOriginal(name);
            if (intrinsicNames.Contains(ownOriginal)) return ownOriginal;
            return mangledOwn + "_" + ownOriginal;
        }

        // A name the enclosing module DEFINES (a class or function filed under its prefix)
        // shadows the flat import-alias table: that table is shared by every module, so a
        // `from digitalio import DigitalInOut` written in one file leaked into another that
        // only defines its own `class DigitalInOut` -- the call resolved to the imported
        // constructor instead of the local one (adafruit_74hc595).
        var prefixTry = currentModulePrefix;
        while (!string.IsNullOrEmpty(prefixTry))
        {
            var candidate = prefixTry + name;
            // Classes are looked up here too, not just functions: a class defined in an
            // imported module is registered under that module's prefix, and without this
            // `C(5)` resolved to the bare `C`, found no `C___init__`, and was reported as a
            // class with no __init__ on a file that defines one -- from the importing file AND
            // from a plain function inside the module that defines the class.
            //
            // A class prefix is not a scope for bare names, though: `decode_bits(...)`
            // inside GenericDecode_decode_bits names the MODULE function, never the
            // sibling method (Python spells sibling methods `self.decode_bits`). Without
            // this, a compat wrapper calling the module function it wraps was reported
            // as recursive.
            bool prefixIsClass = prefixTry.Length > 1
                && (classFieldLayout.ContainsKey(prefixTry[..^1])
                    || classDirectMethods.ContainsKey(prefixTry[..^1]));
            if (!prefixIsClass
                && (inlineFunctions.ContainsKey(candidate) || functionParams.ContainsKey(candidate)
                    || classFieldLayout.ContainsKey(candidate) || classDirectMethods.ContainsKey(candidate)))
            {
                return candidate;
            }

            if (prefixTry.Length < 2) break;
            int lastSep = prefixTry.LastIndexOf('_', prefixTry.Length - 2);
            if (lastSep == -1) break;
            prefixTry = prefixTry.Substring(0, lastSep + 1);
        }

        // The walk above only tries prefixed spellings, so a name the ENTRY file
        // defines (module prefix "") is invisible to it: `class Pin`/`def Pin` in
        // main.py registers under the bare key, and a `Pin(8)` inside another
        // class's method fell through to the flat import-alias table, where a
        // stdlib package __init__'s `from pymcu.hal.gpio import Pin` had already
        // claimed the name. An entry-file definition shadows an import of the
        // same spelling.
        if (inlineFunctions.ContainsKey(name) || functionParams.ContainsKey(name)
            || classFieldLayout.ContainsKey(name) || classDirectMethods.ContainsKey(name))
        {
            return name;
        }

        if (TryImportedAlias(name, out var modName))
        {
            var mangledMod = modName?.Replace('.', '_');
            var original = AliasOriginal(name);
            // `from pymcu.hal.console import print as p`: the alias renames a builtin, so the
            // call must reach the builtin. Mangling it to `pymcu_hal_console_print` named a
            // function that is never emitted, and the error blamed the module rather than
            // saying the alias had been dropped.
            if (intrinsicNames.Contains(original)) return original;
            return mangledMod + "_" + original;
        }

        return name;
    }

    private string? ResolvePrefixedClass(string name)
    {
        var prefixTry = currentModulePrefix;
        while (!string.IsNullOrEmpty(prefixTry))
        {
            var candidate = prefixTry + name;
            if (classFieldLayout.ContainsKey(candidate) || classDirectMethods.ContainsKey(candidate))
                return candidate;
            if (prefixTry.Length < 2) break;
            int lastSep = prefixTry.LastIndexOf('_', prefixTry.Length - 2);
            if (lastSep == -1) break;
            prefixTry = prefixTry.Substring(0, lastSep + 1);
        }
        // Bare spelling last: same entry-file hole as the callee walk above.
        if (classFieldLayout.ContainsKey(name) || classDirectMethods.ContainsKey(name))
            return name;
        return null;
    }

    // Returns true for top-level statements that are purely declarative and have no
    // runtime effect when visited inside a function body.  Such statements are either
    // already handled at scan time (globals, constants, class / function definitions)
    // or are no-ops at the IR level (imports, class definitions).
    //
    // Used by the synthesized-main logic to decide which GlobalStatements to include
    // in the generated `main` body.
    // Follows a re-export chain to the module that actually DEFINES `symbol`. A module
    // defines it when it contains a class, function or module-level assignment of that
    // name; otherwise, if the module re-imports the symbol, the chase continues there.
    // Bounded to keep import cycles from looping.
    private static string ResolveReExport(Dictionary<string, ProgramNode> importedModules,
        string moduleName, string symbol, int depth = 0)
    {
        if (depth > 8 || !importedModules.TryGetValue(moduleName, out var mAst))
            return moduleName;

        bool definedHere =
            mAst.Functions.Any(f => f.Name == symbol)
            || mAst.GlobalStatements.Any(s =>
                s is ClassDef cd && cd.Name == symbol
                || s is AssignStmt { Target: VariableExpr tv } && tv.Name == symbol
                || s is AnnAssign aa && aa.Target == symbol
                || s is VarDecl vd && vd.Name == symbol);
        if (definedHere) return moduleName;

        // `from Y import S` re-exports S; `from Y import S as T` re-exports T, not S.
        foreach (var mi in mAst.Imports)
            if (mi.Symbols.Contains(symbol) && !mi.Aliases.ContainsKey(symbol))
                return ResolveReExport(importedModules, mi.ModuleName, symbol, depth + 1);

        return moduleName;
    }

    private bool IsTopLevelPureDeclaration(Statement s)
    {
        // Imports and class definitions are scanned before IR generation and have no
        // runtime representation in a function body.
        if (s is ImportStmt || s is ClassDef) return true;

        // device_info() is the chip file's declaration of the target, and the prescan
        // consumes it before IR generation starts. A user chip file under the project
        // root is a project module, so its module level becomes a __module_init like
        // any other -- and without this the annotation compiled as a call to a function
        // that does not exist ('device_info' is not exported by pymcu.types).
        if (s is ExprStmt { Expr: CallExpr { Callee: VariableExpr { Name: "device_info" } } }) return true;

        // Dict/set literal bindings are compile-time lookup tables (registered during
        // the scan) -- no runtime initialization exists.
        if (s is AssignStmt { Value: DictExpr or SetExpr, Target: VariableExpr }) return true;
        if (s is VarDecl { Init: DictExpr or SetExpr }) return true;

        if (s is AnnAssign ann)
        {
            // const[T[N]] flash arrays are already injected via pendingFlashData —
            // including them again would emit a duplicate FlashData instruction.
            if (ann.Annotation.StartsWith("const[") && ann.Annotation.EndsWith("]"))
            {
                string inner = ann.Annotation.Substring(6, ann.Annotation.Length - 7);
                if (inner.Contains('[')) return true; // const[uint8[N]] — flash array
            }

            // If ScanGlobals resolved this name as a compile-time constant (e.g.
            // `MY_VAL: const[uint8] = 42` or an all-caps assignment), there is no
            // runtime initializer to emit.
            if (globals.ContainsKey(ann.Target)) return true;

            return false;
        }

        return false;
    }

    // Returns true for the build's auto-injected startup preamble statements -- the
    // `_pymcu_*` calls (clock_init / millis_init / stdout) and the print_str `pass`.
    // Module-level init is inserted AFTER this run so peripheral setup at module scope
    // sees the final clocks and stdout, but before the user's own main body.
    private static bool IsInjectedPreamble(Statement s) =>
        s is PassStmt
        || (s is ExprStmt es && es.Expr is CallExpr ce
            && ce.Callee is VariableExpr ve && ve.Name.StartsWith("_pymcu_"));
    /// <summary>
    /// Run the module-level statements of every IMPORTED module before main's own body.
    ///
    /// Only the entry file's module level was executed, so an imported module's state started
    /// at zero however it was written: `n: uint16 = 5` in cfg.py, or `c = C(5)` at module
    /// level, both arrived as zero. The storage and the writes were real -- a counter in an
    /// imported module counted 0, 1, 2 instead of 5, 6, 7 -- only the initial value was lost,
    /// which is why it compiles, runs, and is wrong by a constant.
    ///
    /// Each module's statements become a synthesized `__module_init` compiled under that
    /// module's own prefix, so its names resolve exactly as the rest of that module does, and
    /// main calls them in import order before anything else the user wrote.
    /// </summary>
    private void EmitImportedModuleInit(FunctionDef? mainFuncDef,
                                        Dictionary<string, ProgramNode> importedModules,
                                        Dictionary<ProgramNode, string> astToCanonicalPrefix)
    {
        if (mainFuncDef == null || importedModules.Count == 0) return;

        var calls = new List<Statement>();
        foreach (var kvp in importedModules)
        {
            var modAst = kvp.Value;
            if (!astToCanonicalPrefix.TryGetValue(modAst, out var prefix)) continue;

            // Only the USER's own modules. An installed distribution (the pymcu stdlib, and
            // the compat layers that provide `machine`, `board` and `busio`) is written knowing
            // that only the entry file's module level runs: several guard their top level on
            // the target chip, and running that as a function reaches code the import machinery
            // never intended to compile. Measured, not assumed: doing it for all of them turns
            // 129 tests red, `machine`'s own `mem8 = _Mem8()` first. Extending it to the
            // installed layers is a separate question with its own measurements.
            if (!projectModules.Contains(kvp.Key)) continue;

            var body = new Block();

            // Same as the entry module: a class body's attribute initializers run first. This
            // goes in ahead of the emptiness check below, so a module that is nothing but a
            // class still gets one.
            if (classAttrInits.TryGetValue(modAst, out var modClassInit))
                foreach (var st in modClassInit) body.Statements.Add(st);

            foreach (var st in modAst.GlobalStatements)
            {
                if (IsTopLevelPureDeclaration(st)) continue;
                if (st is VarDecl d)
                {
                    // A declaration with an initializer is the whole point here: without the
                    // rewrite the value never reaches the global it declares.
                    if (d.Init != null && mutableGlobals.ContainsKey(prefix + d.Name)
                        && !globals.ContainsKey(prefix + d.Name))
                        body.Statements.Add(new AnnAssign(d.Name, d.VarType, d.Init));
                    continue;
                }
                body.Statements.Add(st);
            }

            if (body.Statements.Count == 0) continue;

            // The synthesized function IS the module level, so every name it assigns is a
            // module global by definition. Without saying so it hits the ordinary rule and
            // reports "'c' is a module-level global; to assign it inside
            // 'counter___module_init' add a 'global c'" -- naming a function nobody wrote.
            var globalNames = new List<string>();
            // Top-level-only was wrong: `if cond: x = 1` at module level still assigns
            // the module global, and without the declaration the synthesized function
            // demanded a `global x` in a function nobody wrote.
            foreach (var st in TypeInference.WalkStatements(body.Statements))
            {
                var targets = st switch
                {
                    AnnAssign aa => new[] { aa.Target },
                    AssignStmt { Target: VariableExpr tv } => new[] { tv.Name },
                    AugAssignStmt { Target: VariableExpr av } => new[] { av.Name },
                    ForStmt f => new[] { f.VarName, f.Var2Name },
                    TupleUnpackStmt tu => tu.Targets.ToArray(),
                    _ => Array.Empty<string?>(),
                };
                foreach (var target in targets)
                    if (!string.IsNullOrEmpty(target) && !target.Contains('.')
                        && !globalNames.Contains(target))
                        globalNames.Add(target);
            }
            if (globalNames.Count > 0)
                body.Statements.Insert(0, new GlobalStmt(globalNames));

            string initName = prefix + "__module_init";
            var initFn = new FunctionDef("__module_init", new List<Param>(), "None", body);
            functionsToCompile.Add(new FunctionEntry
                { Prefix = prefix, Func = initFn, SourceFile = kvp.Key + ".py", SourcePath = PathOfModule(kvp.Key) });
            functionReturnTypes[initName] = "None";
            calls.Add(new ExprStmt(new CallExpr(new VariableExpr(initName), new List<Expression>())));
        }

        if (calls.Count == 0) return;

        // After the build's auto-injected preamble, before the entry module's own init and
        // before the user's body: an import runs before the file that imports it.
        var mainBody = mainFuncDef.Body.Statements;
        int at = 0;
        while (at < mainBody.Count && IsInjectedPreamble(mainBody[at])) at++;
        for (int i = calls.Count - 1; i >= 0; i--) mainBody.Insert(at, calls[i]);
    }


    /// <summary>
    /// A function whose declared return type is a CLASS with no representable handle has no
    /// standalone form, so it must be expanded at its call sites.
    ///
    /// A single-field class travels back in a register (RFC 0001 Model B) and a slot class
    /// travels as a pointer to its SRAM slot. Everything else, which is every multi-field HAL
    /// class, has neither: the call returned a scalar, the name it was assigned to never
    /// learned its class, and the next method call on it became a call to a symbol nobody
    /// defines. `def create_out(i) -> Pin` followed by `led.value(1)` reported
    /// "call to undefined function 'led_value'", naming a function the program never wrote.
    ///
    /// The declared type is not the only place the returned class shows up. An unannotated
    /// factory spells it only in the body's `return` -- `def I2C(): return
    /// _board_i2c(SCL, SDA)`, the CircuitPython board layer -- and a shared subroutine
    /// lowers the construction's field stores against the callee's frame, where they are
    /// dropped: the hardware init inside `__init__` still ran (the bus got its TWBR), but
    /// the caller's `i2c._bus._mode` read a name nothing ever wrote and every method's
    /// `self._mode == "c"` gate answered false, so the program emitted not one I2C
    /// transaction. A ZCA instance is compile-time per binding, so the only correct form
    /// is the same @inline expansion a declared-but-unrepresentable class gets.
    ///
    /// Registered in inlineFunctions and REMOVED from functionsToCompile, the same shape
    /// force-inlining already takes for a single-field mutator that also returns a value.
    ///
    /// This runs after every module has been scanned rather than at registration time, because
    /// whether a class is a slot class or a factory class is decided while scanning classes,
    /// in the same pass that registers functions: at registration the answer is not known yet.
    /// </summary>
    private void ForceInlineClassReturningFactories()
    {
        var moved = new List<FunctionEntry>();
        foreach (var entry in functionsToCompile)
        {
            string fullName = (entry.Prefix ?? "") + entry.Func.Name;
            // An outlined method's entry carries the SYNTHESIZED FunctionDef (self
            // rewritten to field parameters, or a slot pointer). Moving it to
            // inlineFunctions would expand a frame whose `self` is not bound. The
            // un-rewritten AST is reached through methodAstByName in the pass below.
            if (outlinedMethods.Contains(fullName)) continue;

            var rt = entry.Func.ReturnType;
            string? classKey = null;
            if (!string.IsNullOrEmpty(rt) && rt != "None")
                classKey = ResolveClassKey(rt, entry.Prefix ?? "");

            bool needs = classKey != null
                && !slotClasses.Contains(classKey) && !zcaFactoryClasses.ContainsKey(classKey);

            // The body-scan half: the class shows up in a `return`, not the annotation.
            // main and __module_init are called by the runtime/injected calls, never
            // from a site this could expand at, and @naked/@interrupt must stay real
            // subroutines whatever their body returns.
            if (!needs && entry.Func.Name is not ("main" or "__module_init")
                && !entry.Func.IsNaked && !entry.Func.IsInterrupt)
            {
                string savedPrefix = currentModulePrefix;
                currentModulePrefix = entry.Prefix ?? "";
                try
                {
                    needs = BodyReturnsUnrepresentedZca(entry.Func.Body, rt ?? "");
                }
                finally
                {
                    currentModulePrefix = savedPrefix;
                }
            }

            if (!needs) continue;
            if (inlineFunctions.ContainsKey(fullName)) continue;
            inlineFunctions[fullName] = entry.Func;
            moved.Add(entry);
        }
        foreach (var m in moved) functionsToCompile.Remove(m);

        // The same shape through a method: `def get_pin(self): return Pin(5)` declares
        // no class and does not pass `self` on, so IsOutlineSafe's annotation check did
        // not see it and it was outlined -- its return dropped the fields the same way.
        // Move the ORIGINAL AST (methodAstByName), never the synth skipped above.
        var demoted = new List<string>();
        foreach (var name in outlinedMethods)
        {
            if (!methodAstByName.TryGetValue(name, out var mfunc)) continue;
            if (inlineFunctions.ContainsKey(name)) continue;
            string savedPrefix = currentModulePrefix;
            currentModulePrefix = functionModulePrefix.TryGetValue(name, out var mpfx) ? mpfx : "";
            try
            {
                if (!BodyReturnsUnrepresentedZca(mfunc.Body, mfunc.ReturnType ?? "")) continue;
            }
            finally
            {
                currentModulePrefix = savedPrefix;
            }
            inlineFunctions[name] = mfunc;
            demoted.Add(name);
        }
        foreach (var d in demoted)
        {
            outlinedMethods.Remove(d);
            functionsToCompile.RemoveAll(fe => (fe.Prefix ?? "") + fe.Func.Name == d);
        }
    }

    /// <summary>
    /// True when the body `return`s a ZCA construction the subroutine ABI cannot carry:
    /// `return C(...)` directly, a name the body bound to one (`x = C(...); return x`),
    /// or a call to a function that itself returns one. The one shape excepted is the
    /// one VisitReturn already lowers: `return C(...)` when the declared type, AS
    /// WRITTEN, is that class AND the class has a runtime return form -- the
    /// register-packed handle for a single-field class (always), the slot pointer for
    /// a slot class (only on that direct spelling). Every other spelling -- an
    /// unannotated `def`, a declared type that does not match, a bound name -- lowers
    /// the construction against the callee's frame and the fields never reach the
    /// caller.
    /// </summary>
    private bool BodyReturnsUnrepresentedZca(Block body, string rawRt)
    {
        var bound = new Dictionary<string, string>();
        return StmtReturnsUnrepresentedZca(body, bound, rawRt, new HashSet<string>());
    }

    private bool StmtReturnsUnrepresentedZca(Statement? st, Dictionary<string, string> bound,
        string rawRt, HashSet<string> visiting)
    {
        // The shared walk yields statements in source order, so `bound` is updated
        // in the same sequence the old recursion built it -- including across arms.
        foreach (var s in TypeInference.WalkStatements(st))
        {
            switch (s)
            {
                case AssignStmt { Target: VariableExpr tv } a:
                    if (ClassReturnedByExpr(a.Value, bound, visiting) is { } ac) bound[tv.Name] = ac;
                    break;
                case AnnAssign an:
                    if (ClassReturnedByExpr(an.Value, bound, visiting) is { } nc) bound[an.Target] = nc;
                    break;
                case VarDecl vd:
                    if (ClassReturnedByExpr(vd.Init, bound, visiting) is { } vc) bound[vd.Name] = vc;
                    break;
                case ReturnStmt r:
                    foreach (var (key, direct) in ReturnedZcaClasses(r.Value, bound, visiting))
                    {
                        if (key == rawRt
                            && (zcaFactoryClasses.ContainsKey(key)
                                || (direct && slotClasses.Contains(key))))
                            continue;
                        return true;
                    }
                    break;
            }
        }
        return false;
    }

    /// <summary>
    /// The (classKey, directCtor) pairs a returned expression hands back. `direct` is
    /// true only for the bare `return C(...)` spelling -- the one VisitReturn's
    /// register-handle and sret paths recognize; a construction nested in a ternary
    /// arm or a tuple element, or returned through a bound name, is not it.
    /// </summary>
    private IEnumerable<(string Key, bool Direct)> ReturnedZcaClasses(Expression? e,
        Dictionary<string, string> bound, HashSet<string> visiting)
    {
        switch (e)
        {
            case CallExpr c:
                if (ConstructedClassKey(c) is { } ck) yield return (ck, true);
                else if (FactoryReturnClass(c, visiting) is { } fk) yield return (fk, false);
                yield break;
            case VariableExpr v when bound.TryGetValue(v.Name, out var bk):
                yield return (bk, false);
                yield break;
            case TernaryExpr t:
                foreach (var h in ReturnedZcaClasses(t.TrueVal, bound, visiting)) yield return (h.Key, false);
                foreach (var h in ReturnedZcaClasses(t.FalseVal, bound, visiting)) yield return (h.Key, false);
                yield break;
            case TupleExpr tp:
                foreach (var el in tp.Elements)
                    foreach (var h in ReturnedZcaClasses(el, bound, visiting)) yield return (h.Key, false);
                yield break;
            case ListExpr l:
                foreach (var el in l.Elements)
                    foreach (var h in ReturnedZcaClasses(el, bound, visiting)) yield return (h.Key, false);
                yield break;
        }
    }

    /// <summary>
    /// The class a `= <expr>` binding hands the name, for the scan's purposes: a
    /// constructor call's class, or what a called factory returns. Null for any
    /// other expression.
    /// </summary>
    private string? ClassReturnedByExpr(Expression? e, Dictionary<string, string> bound,
        HashSet<string> visiting) => e switch
    {
        CallExpr c => (string?)ConstructedClassKey(c) ?? FactoryReturnClass(c, visiting),
        _ => null,
    };

    /// <summary>
    /// The class key a call constructs, resolving the callee under the module prefix
    /// the body was written in (already installed by the caller). Null when the call
    /// constructs no class this compiler tracks.
    /// </summary>
    private string? ConstructedClassKey(CallExpr call)
    {
        string? resolved = call.Callee switch
        {
            VariableExpr v => ResolveCallee(v.Name),
            MemberAccessExpr { Object: VariableExpr mv } ma when modules.ContainsKey(mv.Name)
                => (TryImportedAlias(mv.Name, out var rm) && rm != null ? rm : mv.Name)
                       .Replace('.', '_') + "_" + ma.Member,
            _ => null,
        };
        return resolved != null
            && (classFieldLayout.ContainsKey(resolved) || classDirectMethods.ContainsKey(resolved))
            ? resolved : null;
    }

    /// <summary>
    /// `return f(...)` / `x = f(...)` where f is not a constructor but a function
    /// whose own returns are constructions: the returned class is whatever f's body
    /// hands back -- by its declared return type when there is one, else by scanning
    /// the callee's body the same way (`def I2C(): return busI2C(...)` re-exported by
    /// `def board_i2c(): return I2C()`). Bounded by `visiting` so mutually
    /// re-exporting factories cannot loop.
    /// </summary>
    private string? FactoryReturnClass(CallExpr call, HashSet<string> visiting)
    {
        string? fn = call.Callee switch
        {
            VariableExpr v => ResolveCallee(v.Name),
            MemberAccessExpr { Object: VariableExpr mv } ma when modules.ContainsKey(mv.Name)
                => (TryImportedAlias(mv.Name, out var rm) && rm != null ? rm : mv.Name)
                       .Replace('.', '_') + "_" + ma.Member,
            _ => null,
        };
        if (fn == null || !visiting.Add(fn)) return null;

        if (functionReturnTypes.TryGetValue(fn, out var frt)
            && !string.IsNullOrEmpty(frt) && frt != "None" && frt != "void")
        {
            string fpfx = functionModulePrefix.TryGetValue(fn, out var fp) ? fp : "";
            if (ResolveClassKey(frt, fpfx) is { } declaredKey) return declaredKey;
        }

        FunctionDef? fd = null;
        if (inlineFunctions.TryGetValue(fn, out var f1)) fd = f1;
        else if (methodAstByName.TryGetValue(fn, out var f2)) fd = f2;
        else foreach (var fe in functionsToCompile)
            if ((fe.Prefix ?? "") + fe.Func.Name == fn) { fd = fe.Func; break; }
        if (fd == null) return null;

        string savedPrefix = currentModulePrefix;
        currentModulePrefix = functionModulePrefix.TryGetValue(fn, out var mp) ? mp : "";
        try
        {
            var bound = new Dictionary<string, string>();
            return FirstReturnedZcaClass(fd.Body, bound, visiting);
        }
        finally
        {
            currentModulePrefix = savedPrefix;
        }
    }

    /// The first class key any return in the body hands back, for transitive
    /// resolution -- directness does not matter at this depth: a construction that
    /// crosses TWO boundaries is never the register/sret spelling either way.
    private string? FirstReturnedZcaClass(Statement? st, Dictionary<string, string> bound,
        HashSet<string> visiting)
    {
        // Source order keeps `bound` updates in the same sequence the old recursion
        // built them, and the first ReturnStmt it yields is the first return it saw.
        foreach (var s in TypeInference.WalkStatements(st))
        {
            switch (s)
            {
                case AssignStmt { Target: VariableExpr tv } a:
                    if (ClassReturnedByExpr(a.Value, bound, visiting) is { } ac) bound[tv.Name] = ac;
                    break;
                case AnnAssign an:
                    if (ClassReturnedByExpr(an.Value, bound, visiting) is { } nc) bound[an.Target] = nc;
                    break;
                case VarDecl vd:
                    if (ClassReturnedByExpr(vd.Init, bound, visiting) is { } vc) bound[vd.Name] = vc;
                    break;
                case ReturnStmt r:
                    foreach (var (key, _) in ReturnedZcaClasses(r.Value, bound, visiting))
                        return key;
                    return null;
            }
        }
        return null;
    }

    /// <summary>
    /// A function that returns several values has no subroutine lowering: the AVR ABI hands
    /// back one register, so `return (a, b)` can only reach the caller through the @inline
    /// expansion path, where the unpack targets (or a `f()[k]` site's sentinel) become the
    /// result slots. Rather than refuse `def f(): ... return (a, b)` for want of the
    /// decorator, register it in inlineFunctions and let it expand wherever it is called --
    /// the same shape ForceInlineClassReturningFactories takes. This is what
    /// adafruit_tcs34725's `color_rgb_bytes` property needs: it is an ordinary method with
    /// `return (red, green, blue)`.
    /// </summary>
    private void ForceInlineTupleReturningFunctions()
    {
        var moved = new List<FunctionEntry>();
        foreach (var entry in functionsToCompile)
        {
            if (!TupleType.IsTupleType(entry.Func.ReturnType)
                && TupleReturnArity(entry.Func) == 0
                && !BodyReturnsStructUnpack(entry.Func)
                && !BodyReturnsConditionalTuple(entry.Func)) continue;

            string fullName = (entry.Prefix ?? "") + entry.Func.Name;
            if (inlineFunctions.ContainsKey(fullName)) continue;
            inlineFunctions[fullName] = entry.Func;
            moved.Add(entry);
        }
        foreach (var m in moved) functionsToCompile.Remove(m);
    }

    /// <summary>
    /// True when a body has `return struct.unpack(fmt, buf)` / `return struct.unpack_from(...)`.
    /// The element count lives in the format text -- possibly a field that only binds inside
    /// __init__ -- so TupleReturnArity cannot count it, but the return still delivers a tuple,
    /// which an outlined body cannot carry. Registering the function for expansion lets
    /// VisitReturn mint the result slots at the return, where the format resolves.
    /// AST-only like the arity scan: nothing is lowered, so a format the compiler would later
    /// refuse is still reported there and not here.
    /// </summary>
    private bool BodyReturnsStructUnpack(FunctionDef func)
    {
        bool found = false;
        foreach (var s in TypeInference.WalkStatements(func.Body))
        {
            if (s is not ReturnStmt { Value: { } rv }) continue;
            Expression inner = rv;
            if (inner is CallExpr { Callee: VariableExpr { Name: "list" or "tuple" } } wrap
                && wrap.Args.Count == 1)
                inner = wrap.Args[0];
            if (inner is CallExpr uc
                && (IsStructCall(uc, "unpack") || IsStructCall(uc, "unpack_from")))
                found = true;
        }
        return found;
    }

    /// <summary>
    /// True when a body has `return (a, b) if cond else (a, b, c)` -- a tuple that
    /// lives in a conditional expression's arm. TupleReturnArity cannot count it
    /// (the arms can differ, and which one runs is the condition's answer), but the
    /// return still delivers a tuple an outlined body cannot carry; VisitReturn
    /// folds the condition and mints the taken arm's slots. AST-only like the arity
    /// scan: nothing is lowered here.
    /// </summary>
    private static bool BodyReturnsConditionalTuple(FunctionDef func)
    {
        foreach (var s in TypeInference.WalkStatements(func.Body))
            if (s is ReturnStmt { Value: TernaryExpr t }
                && (t.TrueVal is TupleExpr || t.FalseVal is TupleExpr))
                return true;
        return false;
    }

    /// <summary>
    /// The same move for a function that returns a buffer it built locally:
    /// `return result` on a `bytearray(length)` the body just filled. There is no
    /// handle to hand back through the ABI, but the @inline expansion path already
    /// gives the caller an alias onto the callee's element storage -- which is how
    /// adafruit_bmp280's `_read_register` reaches `for b in self._read_register(n)`
    /// and `self._read_register(r, 1)[0]` unmodified (PyMCU#464).
    /// </summary>
    private void ForceInlineBufferReturningFunctions()
    {
        var moved = new List<FunctionEntry>();
        foreach (var entry in functionsToCompile)
        {
            if (entry.Func.Name is "main" or "__module_init") continue;
            if (!FunctionReturnsFixedBuffer(entry.Func)) continue;
            string fullName = (entry.Prefix ?? "") + entry.Func.Name;
            if (!inlineFunctions.ContainsKey(fullName))
                inlineFunctions[fullName] = entry.Func;
            outlinedMethods.Remove(fullName);
            moved.Add(entry);
        }
        foreach (var m in moved) functionsToCompile.Remove(m);

        foreach (var name in outlinedMethods.ToList())
        {
            if (!methodAstByName.TryGetValue(name, out var func)) continue;
            if (!FunctionReturnsFixedBuffer(func)) continue;
            inlineFunctions[name] = func;
            outlinedMethods.Remove(name);
        }
    }

    /// <summary>
    /// A class-body function with no <c>self</c> is compiled as an ordinary
    /// subroutine (#201). That body cannot see the argument's class: the
    /// call site binds it, the outlined form does not. <c>framebuf.stride</c>
    /// inside <c>MVLSBFormat.set_pixel</c> then refused the member as a
    /// numeric value (or mangled it as the import alias). Expand those that
    /// actually read a parameter field, or that pass the parameter to a
    /// sibling that does (<c>GS2HMSBFormat.rect</c> -> <c>set_pixel</c>),
    /// the same move as a tuple-returning function. A no-self method that
    /// only does arithmetic stays a subroutine, which is what <c>A.f(x)</c>
    /// in #201 pins.
    /// </summary>
    private void ForceInlineClassPlainFunctionsThatReadParamMembers()
    {
        var moved = new List<FunctionEntry>();
        foreach (var entry in functionsToCompile)
        {
            string fullName = (entry.Prefix ?? "") + entry.Func.Name;
            if (!classPlainFunctions.Contains(fullName)) continue;
            if (!FunctionReadsParamMember(entry.Func)) continue;
            if (inlineFunctions.ContainsKey(fullName)) continue;
            inlineFunctions[fullName] = entry.Func;
            moved.Add(entry);
        }
        foreach (var m in moved) functionsToCompile.Remove(m);
    }

    private static bool FunctionReadsParamMember(FunctionDef func)
    {
        if (func.Params.Count == 0) return false;
        var names = new HashSet<string>(func.Params.Select(p => p.Name));
        return StatementReadsParamMember(func.Body, names);
    }

    private static bool StatementReadsParamMember(Statement? st, HashSet<string> names)
    {
        // The shared walk yields the container itself and its children, so the
        // parent's own expressions (condition, iterable, target, patterns, guard,
        // context expression) are checked here and the children arrive separately.
        foreach (var s in TypeInference.WalkStatements(st))
        {
            switch (s)
            {
                case AssignStmt a:
                    if (ExprReadsParamMember(a.Target, names) || ExprReadsParamMember(a.Value, names))
                        return true;
                    break;
                case AugAssignStmt aug:
                    if (ExprReadsParamMember(aug.Target, names) || ExprReadsParamMember(aug.Value, names))
                        return true;
                    break;
                case AnnAssign an:
                    if (ExprReadsParamMember(an.Value, names)) return true;
                    break;
                case VarDecl vd:
                    if (ExprReadsParamMember(vd.Init, names)) return true;
                    break;
                case TupleUnpackStmt tu:
                    if (ExprReadsParamMember(tu.Value, names)) return true;
                    break;
                case ExprStmt es:
                    if (ExprReadsParamMember(es.Expr, names)) return true;
                    break;
                case ReturnStmt r:
                    if (ExprReadsParamMember(r.Value, names)) return true;
                    break;
                case ForStmt f:
                    if (ExprReadsParamMember(f.Iterable, names)) return true;
                    break;
                case WhileStmt w:
                    if (ExprReadsParamMember(w.Condition, names)) return true;
                    break;
                case IfStmt i:
                    if (ExprReadsParamMember(i.Condition, names)) return true;
                    foreach (var (cond, _) in i.ElifBranches)
                        if (ExprReadsParamMember(cond, names)) return true;
                    break;
                case WithStmt wi:
                    if (ExprReadsParamMember(wi.ContextExpr, names)) return true;
                    break;
                case MatchStmt m:
                    if (ExprReadsParamMember(m.Target, names)) return true;
                    foreach (var br in m.Branches)
                    {
                        if (ExprReadsParamMember(br.Pattern, names)) return true;
                        if (br.Guard != null && ExprReadsParamMember(br.Guard, names)) return true;
                    }
                    break;
            }
        }
        return false;
    }

    private static bool ExprReadsParamMember(Expression? e, HashSet<string> names)
    {
        switch (e)
        {
            case null: return false;
            case MemberAccessExpr { Object: VariableExpr ve }:
                return names.Contains(ve.Name);
            case MemberAccessExpr m: return ExprReadsParamMember(m.Object, names);
            case CallExpr c:
                if (ExprReadsParamMember(c.Callee, names)) return true;
                foreach (var a in c.Args)
                {
                    // A parameter handed to another call is that call's
                    // receiver: GS2HMSBFormat.rect does
                    // GS2HMSBFormat.set_pixel(framebuf, ...) and the callee
                    // reads framebuf.stride. Without this, rect stayed an
                    // outlined subroutine, was compiled unused, and
                    // set_pixel saw a numeric framebuf.
                    if (a is VariableExpr ve && names.Contains(ve.Name)) return true;
                    if (ExprReadsParamMember(a, names)) return true;
                }
                return false;
            case ListCompExpr lc:
                return ExprReadsParamMember(lc.Element, names)
                    || ExprReadsParamMember(lc.Iterable, names)
                    || ExprReadsParamMember(lc.Iterable2, names)
                    || ExprReadsParamMember(lc.Filter, names);
            case GeneratorExpr gx:
                return ExprReadsParamMember(gx.Element, names)
                    || ExprReadsParamMember(gx.Iterable, names)
                    || ExprReadsParamMember(gx.Iterable2, names)
                    || ExprReadsParamMember(gx.Filter, names);
            case BinaryExpr b:
                return ExprReadsParamMember(b.Left, names) || ExprReadsParamMember(b.Right, names);
            case UnaryExpr u: return ExprReadsParamMember(u.Operand, names);
            case IndexExpr ix:
                return ExprReadsParamMember(ix.Target, names) || ExprReadsParamMember(ix.Index, names);
            case TernaryExpr t:
                return ExprReadsParamMember(t.Condition, names)
                    || ExprReadsParamMember(t.TrueVal, names)
                    || ExprReadsParamMember(t.FalseVal, names);
            case ListExpr l:
                foreach (var x in l.Elements)
                    if (ExprReadsParamMember(x, names)) return true;
                return false;
            case TupleExpr tp:
                foreach (var x in tp.Elements)
                    if (ExprReadsParamMember(x, names)) return true;
                return false;
            case KeywordArgExpr k: return ExprReadsParamMember(k.Value, names);
            default: return false;
        }
    }

    private static bool IsBufferTypeName(string? rt) =>
        rt is "bytearray" or "bytes" or "memoryview" or "WriteableBuffer" or "ReadableBuffer";

    private static bool IsBufferCtor(Expression? e) =>
        e is CallExpr { Callee: VariableExpr { Name: "bytearray" or "bytes" } };

    private static bool FunctionReturnsFixedBuffer(FunctionDef func)
    {
        if (IsBufferTypeName(func.ReturnType)) return true;
        var bufLocals = new HashSet<string>();
        return StatementReturnsFixedBuffer(func.Body, bufLocals);
    }

    private static bool StatementReturnsFixedBuffer(Statement? stmt, HashSet<string> bufLocals)
    {
        // Source order keeps `bufLocals` updates in the same sequence the old
        // recursion built them.
        foreach (var s in TypeInference.WalkStatements(stmt))
        {
            switch (s)
            {
                case VarDecl v when IsBufferTypeName(v.VarType) || IsBufferCtor(v.Init):
                    bufLocals.Add(v.Name);
                    break;
                case AnnAssign a when IsBufferTypeName(a.Annotation) || IsBufferCtor(a.Value):
                    bufLocals.Add(a.Target);
                    break;
                case AssignStmt { Target: VariableExpr t } a when IsBufferCtor(a.Value):
                    bufLocals.Add(t.Name);
                    break;
                case ReturnStmt { Value: VariableExpr rv } when bufLocals.Contains(rv.Name):
                    return true;
                case ReturnStmt { Value: ListExpr }:
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The class a declared type name refers to, or null when it names no class. Tries the name
    /// as written, then under the defining module's prefix, then any known class whose key ends
    /// in it: a return type is spelled the way the user imported it, not the way it is mangled.
    /// </summary>
    private string? ResolveClassKey(string typeName, string prefix)
    {
        if (classFieldLayout.ContainsKey(typeName) || classDirectMethods.ContainsKey(typeName))
            return typeName;

        string prefixed = prefix + typeName;
        if (classFieldLayout.ContainsKey(prefixed) || classDirectMethods.ContainsKey(prefixed))
            return prefixed;

        string suffix = "_" + typeName;
        foreach (var k in classDirectMethods.Keys)
            if (k.EndsWith(suffix, StringComparison.Ordinal)) return k;
        foreach (var k in classFieldLayout.Keys)
            if (k.EndsWith(suffix, StringComparison.Ordinal)) return k;
        return null;
    }

}
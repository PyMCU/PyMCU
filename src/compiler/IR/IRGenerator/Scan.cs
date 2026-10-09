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

namespace PyMCU.IR.IRGenerator;

public partial class IRGenerator
{
    // Registers a module-level fixed buffer for `name = bytearray(N)` or
    // `bytearray([...])`. N may be an integer literal or any compile-time constant
    // expression already registered by this scan (e.g. `WINDOW = 8` earlier in the
    // module). Silently ignores anything that is not a bytearray(...) call — the
    // caller passes every candidate initializer through here.
    private void TryRegisterModuleBytearray(string name, Expression? initializer)
    {
        if (initializer is not CallExpr call || call.Callee is not VariableExpr callee
            || callee.Name != "bytearray" || call.Args.Count == 0) return;

        int count = call.Args[0] switch
        {
            ListExpr le => le.Elements.Count,
            var e when TryEvalElemConst(e, out int n) => n,
            _ => 0,
        };
        if (count <= 0) return;

        KeepGrownArraySize(name, count);
        arrayElemTypes[name] = DataType.UINT8;
        moduleSramArrays.Add(name);
    }

    /// <summary>
    /// A module-level `g = [[v]*W for _ in range(H)]` / `[bytearray(W) ...]` is a
    /// compile-time 2-D grid: one flat array of W*H elements under the bare name,
    /// exactly like `name = bytearray(N)`. Registering dims here lets a subscript
    /// resolve the grid before (or without) the declaration replay reaching
    /// EmitLocalGridInit.
    /// </summary>
    private void TryRegisterModuleGrid(string name, Expression? initializer)
    {
        if (initializer is not ListCompExpr comp || !IsGridComprehension(comp)) return;
        if (!TryFoldGridDims(comp, out var dims)) return;
        arraySizes[name] = dims.W * dims.H;
        arrayElemTypes[name] = GridElemTypeOf(comp);
        gridDims[name] = dims;
        moduleSramArrays.Add(name);
    }

    /// <summary>
    /// A buffer's size is the largest it is ever asked for (#362). Replaying
    /// <c>name = bytearray(n)</c> in module init must not undo a <c>.extend()</c>
    /// that already grew it. Class-body constructors run ahead of the module's own
    /// statements (#270), so adafruit_register's <c>_BUFFER = bytearray(1)</c> was
    /// rewriting size 3 back to 1 after <c>RWBits.__init__</c> called <c>_fit(2)</c>.
    /// </summary>
    private int KeepGrownArraySize(string key, int declared, bool isModuleReplay = false)
    {
        int size = arraySizes.TryGetValue(key, out int already) ? Math.Max(already, declared) : declared;
        arraySizes[key] = size;
        // The run-time branch context this declaration runs under. `buf += src`
        // (VisitAugAssign) reads it to refuse a growth whose executions are not
        // one-for-one with this declaration's.
        bufferDeclBranchTokens[key] = new List<int>(_runtimeBranchTokens);
        // ...and the LOGICAL length re-bases: this statement is a fresh buffer here
        // even where a sibling expansion's growth already sized the shared storage.
        // Except the module-init replay, which lowers the same statement a second
        // time -- a buffer grown between the two (#270's _fit) keeps its grown
        // length there, the same reason `size` above keeps the max.
        if (!isModuleReplay || !bufferLogicalLen.ContainsKey(key))
            bufferLogicalLen[key] = declared;
        return size;
    }

    /// <summary>
    /// The length len()/writeto/`+=` should answer for <paramref name="key"/> -- the
    /// declaration's count plus any appends on this path, when the buffer went
    /// through a declaration that records one; the storage size otherwise.
    /// </summary>
    private int LogicalArrayLen(string key, int storageSize) =>
        bufferLogicalLen.TryGetValue(key, out int logical) ? logical : storageSize;

    // Whether `name` is EVER an assignment target anywhere in `ast`: a bare top-level
    // statement, one nested in a loop/branch/try/with, or inside any function or method
    // body (a `global name` write included -- there is no DIFFERENT target spelling for
    // one). Unlike CollectModuleReassignedNames, a single write already answers true: that
    // function exists to tell a merely-constant initializer from a true mutable (and so
    // exempts a name written only once, by design, #372); this one exists to tell an
    // untouched import from one the importer rebinds at all, where even its ONE write is
    // what CPython's later read must see instead of the defining module's own value
    // (`from m import X` then `X = 42`, PyMCU#rebindstr).
    private static bool ProgramWritesName(ProgramNode ast, string name)
    {
        bool found = false;
        void Walk(Statement s)
        {
            switch (s)
            {
                case AssignStmt { Target: VariableExpr v } when v.Name == name: found = true; break;
                case AnnAssign aa when aa.Target == name: found = true; break;
                case VarDecl vd when vd.Name == name: found = true; break;
                case AugAssignStmt { Target: VariableExpr av } when av.Name == name: found = true; break;
            }
        }
        foreach (var s in TypeInference.WalkStatements(ast.GlobalStatements)) Walk(s);
        foreach (var fn in ast.Functions.Concat(TypeInference.ClassMethods(ast)))
            foreach (var s in TypeInference.WalkStatements(fn.Body)) Walk(s);
        return found;
    }

    // Module-level names that are WRITTEN beyond their initializer: a second top-level
    // assignment, any assignment nested in a loop/branch/try, an augmented assignment,
    // or a function that declares them `global`. Such a name is a mutable variable whose
    // initializer merely happens to be constant -- NOT a constant alias. Folding it as a
    // constant silently deleted every later write and folded every read (a state machine
    // with named states, `state: uint8 = IDLE`, never left state 0).
    /// <summary>
    /// Whether the module's own top level writes <paramref name="sym"/> on a line BEFORE
    /// <paramref name="importLine"/> -- a binding the import then replaced. The shared
    /// walk stays at module level: a function's `X = 42` is its local, not this name.
    /// </summary>
    private static bool ModuleBindsBefore(ProgramNode ast, string sym, int importLine)
    {
        foreach (var s in TypeInference.WalkStatements(ast.GlobalStatements))
        {
            switch (s)
            {
                case AssignStmt { Target: VariableExpr v } when v.Name == sym && s.Line < importLine:
                case VarDecl vd when vd.Name == sym && s.Line < importLine:
                case AnnAssign aa when aa.Target == sym && s.Line < importLine:
                    return true;
            }
        }
        return false;
    }

    private static HashSet<string> CollectModuleReassignedNames(ProgramNode ast)
    {
        var counts = new Dictionary<string, int>();
        void Bump(string n, int by = 1) =>
            counts[n] = (counts.TryGetValue(n, out var c) ? c : 0) + by;

        void Walk(Statement s)
        {
            switch (s)
            {
                case AssignStmt { Target: VariableExpr v }: Bump(v.Name); return;
                case AnnAssign aa: Bump(aa.Target); return;
                case VarDecl vd: Bump(vd.Name); return;
                // aug-assign presupposes an existing binding: always a REassignment.
                case AugAssignStmt { Target: VariableExpr av }: Bump(av.Name, 2); return;
            }
        }

        // The shared walk reaches the arm the old recursion skipped: a write
        // inside a module-level `with` reassigned its name unseen.
        foreach (var s in TypeInference.WalkStatements(ast.GlobalStatements)) Walk(s);
        var result = new HashSet<string>(
            counts.Where(kv => kv.Value > 1).Select(kv => kv.Key));

        // A name written ONLY by straight-line top-level statements is the LAST of them (#372).
        //
        // The module's top level runs once, in order, so nothing can observe the intermediate
        // value of a name written twice in a row. Two counts made such a name mutable, which is
        // the right answer for what this was written for -- a state machine whose `state` is
        // written from inside functions -- and the wrong one for the optional-import flag every
        // CircuitPython driver opens with:
        //
        //     _USE_PULSEIO = False
        //     try:
        //         from pulseio import PulseIn
        //         _USE_PULSEIO = True
        //     except ImportError:
        //         pass
        //
        // Once #351 folds that try, the two writes ARE straight-line top-level statements. With
        // the flag mutable, `if _USE_PULSEIO:` became a run-time branch, both sides of
        // adafruit_hcsr04's constructor were lowered, `self._echo` took two types and
        // `self._echo.clear()` was compiled against the one that has no `clear`.
        //
        // The name is only released when NO top-level statement between the first and the last
        // write READS it: a read before the last write sees a different value than a read
        // after it, and one constant cannot answer for both.
        foreach (var name in TopLevelOnlyLastWriteWins(ast, counts)) result.Remove(name);

        // `global x` inside a function marks x as mutated from function scope.
        // The shared walk reaches the arms the old recursion skipped: a `global`
        // inside with/match/try-else still mutates from function scope.
        void WalkGlobals(Statement s)
        {
            if (s is GlobalStmt g) foreach (var n in g.Names) result.Add(n);
        }
        // A `global` inside a METHOD declares the same module write: methods are ClassDef
        // bodies in GlobalStatements, not entries of prog.Functions, and skipping them left
        // the name a foldable constant whose method write went nowhere (the #220 defect
        // with a class in front of it).
        foreach (var fn in ast.Functions.Concat(TypeInference.ClassMethods(ast)))
            foreach (var s in TypeInference.WalkStatements(fn.Body)) WalkGlobals(s);

        return result;
    }

    /// <summary>Every name a `global` statement declares in a function or method.</summary>
    private static HashSet<string> CollectGlobalDeclaredNames(ProgramNode ast)
    {
        var result = new HashSet<string>();
        void WalkGlobals(Statement s)
        {
            if (s is GlobalStmt g) foreach (var n in g.Names) result.Add(n);
        }
        foreach (var fn in ast.Functions.Concat(TypeInference.ClassMethods(ast)))
            foreach (var s in TypeInference.WalkStatements(fn.Body)) WalkGlobals(s);
        return result;
    }

    /// <summary>
    /// The names whose every write is a straight-line top-level assignment, with no top-level
    /// read before the last of them (#372). Such a name is the constant of its last write.
    /// </summary>
    private static List<string> TopLevelOnlyLastWriteWins(
        ProgramNode ast, Dictionary<string, int> counts)
    {
        var topWrites = new Dictionary<string, List<int>>();
        var allWrites = new Dictionary<string, int>();
        var firstRead = new Dictionary<string, int>();

        void CountWritesAnywhere(Statement s)
        {
            switch (s)
            {
                case AssignStmt { Target: VariableExpr v }: allWrites[v.Name] = allWrites.GetValueOrDefault(v.Name) + 1; return;
                case AnnAssign aa: allWrites[aa.Target] = allWrites.GetValueOrDefault(aa.Target) + 1; return;
                case VarDecl vd: allWrites[vd.Name] = allWrites.GetValueOrDefault(vd.Name) + 1; return;
                case AugAssignStmt { Target: VariableExpr av }: allWrites[av.Name] = allWrites.GetValueOrDefault(av.Name) + 2; return;
            }
        }

        void Reads(Expression? e, int idx)
        {
            switch (e)
            {
                case null: return;
                case VariableExpr v:
                    if (!firstRead.ContainsKey(v.Name)) firstRead[v.Name] = idx;
                    return;
                case BinaryExpr b: Reads(b.Left, idx); Reads(b.Right, idx); return;
                case UnaryExpr u: Reads(u.Operand, idx); return;
                case CallExpr c:
                    Reads(c.Callee, idx);
                    foreach (var a in c.Args) Reads(a, idx);
                    return;
                case IndexExpr ix: Reads(ix.Target, idx); Reads(ix.Index, idx); return;
                case MemberAccessExpr m: Reads(m.Object, idx); return;
                case ListExpr le: foreach (var x in le.Elements) Reads(x, idx); return;
                case TupleExpr te: foreach (var x in te.Elements) Reads(x, idx); return;
            }
        }

        for (int i = 0; i < ast.GlobalStatements.Count; ++i)
        {
            var st = ast.GlobalStatements[i];
            foreach (var inner in TypeInference.WalkStatements(st)) CountWritesAnywhere(inner);
            switch (st)
            {
                // A CONSTANT straight-line top-level write. Anything else -- a call, a name, an
                // expression -- is not a value this can answer with, so the name keeps whatever
                // the counter said.
                case AssignStmt { Target: VariableExpr tv, Value: var val } when IsPlainConstant(val):
                    topWrites.TryAdd(tv.Name, new List<int>());
                    topWrites[tv.Name].Add(i);
                    break;
                case AssignStmt a2: Reads(a2.Value, i); break;
                case AnnAssign an: Reads(an.Value, i); break;
                case VarDecl vd2: Reads(vd2.Init, i); break;
                case ExprStmt ex: Reads(ex.Expr, i); break;
            }
        }

        var freed = new List<string>();
        foreach (var (name, writes) in topWrites)
        {
            if (writes.Count < 2) continue;
            // Every write the program makes has to be one of these: a nested one, or one from a
            // function, means the name really is mutable.
            if (allWrites.GetValueOrDefault(name) != writes.Count) continue;
            if (counts.GetValueOrDefault(name) != writes.Count) continue;
            if (firstRead.TryGetValue(name, out int r) && r < writes[^1]) continue;
            freed.Add(name);
        }
        return freed;
    }

    /// Whether a module-level binding holds a FLOAT: the annotation says so, or the
    /// initializer is a float literal (with or without a leading minus).
    private static bool IsFloatModuleBinding(string? annotation, Expression? initializer)
        => annotation == "float"
           || initializer is FloatLiteral
           || initializer is UnaryExpr { Op: PyMCU.Frontend.UnaryOp.Negate, Operand: FloatLiteral };

    /// A value a straight-line top-level write can be answered with.
    private static bool IsPlainConstant(Expression? e) =>
        e is IntegerLiteral or BooleanLiteral or StringLiteral or FloatLiteral
        || (e is UnaryExpr { Op: PyMCU.Frontend.UnaryOp.Negate, Operand: IntegerLiteral or FloatLiteral });

    /// <summary>
    /// Record every `Cls.ATTR` this statement uses as an assignment target, recursing into
    /// class bodies, methods and nested blocks. Defensive about statement shapes in the same
    /// direction as CollectAssignedMemberNames: a missed write leaves a constant that swallows
    /// it, so this must never UNDER-collect.
    /// </summary>
    private void CollectWrittenClassAttributes(Statement? s)
    {
        foreach (var st in TypeInference.WalkStatements(s))
        {
            switch (st)
            {
                // The shared walk does not descend into defs/classes; these are
                // walked explicitly, the same scopes the old recursion entered.
                case ClassDef cd:
                    CollectWrittenClassAttributes(cd.Body);
                    break;
                case FunctionDef fd:
                    CollectWrittenClassAttributes(fd.Body);
                    break;
                case ForStmt f:
                    // `for Dev.LIMIT in ...` writes the attribute on every iteration.
                    if (f.VarName.Contains('.')) writtenClassAttributes.Add(f.VarName);
                    break;
                case AssignStmt a: RecordClassAttrWriteTarget(a.Target); break;
                case AugAssignStmt ag: RecordClassAttrWriteTarget(ag.Target); break;
                case AnnAssign an:
                    // AnnAssign.Target is a (possibly dotted) name string, e.g. "Dev.LIMIT".
                    if (an.Target.Contains('.')) writtenClassAttributes.Add(an.Target);
                    break;
            }
        }
    }

    /// Record the class of every `self.<field> = SomeClass(...)` this statement reaches, at any
    /// depth. The FIRST assignment wins, so a branch choosing between two implementations
    /// records the one written first.
    private void RecordConstructedFieldClasses(Statement? s, string classKey)
    {
        foreach (var st in TypeInference.WalkStatements(s))
        {
            if (st is AssignStmt { Target: MemberAccessExpr { Object: VariableExpr sv } m2,
                                 Value: CallExpr { Callee: VariableExpr cv } }
                && sv.Name == "self" && !IsScalarTypeName(cv.Name)
                && !fieldClasses.ContainsKey(classKey + "|" + m2.Member)
                // Only a class construction records a field class -- a helper call's
                // result is scalar storage. classNames is still filling (a callee
                // declared later in the file is invisible), so classModuleMap, filed
                // for every class before this pass runs, answers for it too.
                && (classNames.Contains(cv.Name) || classNames.Contains(ResolveCallee(cv.Name))
                    || classModuleMap.ContainsKey(cv.Name)))
                fieldClasses[classKey + "|" + m2.Member] = ResolveCallee(cv.Name);
            // `self.<field> = mod.SomeClass(...)` -- the same record as the bare-name arm
            // above, reached through a dotted callee. `i2c_device.I2CDevice(...)` is how
            // every bus-device driver spells it; unrecorded, a boxed field read lost the
            // class and `with self._device` reported its manager's `__enter__` undefined.
            if (st is AssignStmt { Target: MemberAccessExpr { Object: VariableExpr sv3 } m3,
                                 Value: CallExpr { Callee: MemberAccessExpr callee3 } }
                && sv3.Name == "self" && !IsScalarTypeName(callee3.Member)
                && !fieldClasses.ContainsKey(classKey + "|" + m3.Member)
                && DottedExprText(callee3) is { } dottedCallee)
                fieldClasses[classKey + "|" + m3.Member] = ResolveCallee(dottedCallee);
        }
    }

    /// Record every `self.<field> = <class object>` this statement reaches, at any depth, in
    /// source order. Unlike the constructed-instance map above, ALL candidates matter: the
    /// field's layout byte carries a tag (the candidate's index here) that reads dispatch on,
    /// so a branch choosing between two classes records both. adafruit_seesaw's pin_mapping
    /// elif-chain is the shape this exists for.
    private void RecordClassObjectFieldBindings(Statement? s, string classKey)
    {
        foreach (var st in TypeInference.WalkStatements(s))
        {
            if (st is AssignStmt { Target: MemberAccessExpr { Object: VariableExpr sv } m2,
                                 Value: var rhs }
                && sv.Name == "self" && ClassObjectExprClass(rhs) is { } boundCls)
            {
                string coKey = classKey + "|" + m2.Member;
                if (!classObjectFields.TryGetValue(coKey, out var coList))
                    classObjectFields[coKey] = coList = new List<string>();
                if (!coList.Contains(boundCls)) coList.Add(boundCls);
            }
        }
    }

    /// The class an expression names when used as a VALUE -- `self.f = SAMD09_Pinmap` or the
    /// dotted `self.f = seesaw.SAMD09_Pinmap`. Null when the expression is an instance, a
    /// scalar, or anything that is not a class known at compile time.
    private string? ClassObjectExprClass(Expression e)
    {
        switch (e)
        {
            case VariableExpr ve:
                return ClassNameOf(ve);
            case MemberAccessExpr { Object: VariableExpr mv } mm:
            {
                string realMod = TryImportedAlias(mv.Name, out var rm) && rm != null ? rm : mv.Name;
                string mangled = realMod.Replace('.', '_') + "_" + mm.Member;
                if (classFieldLayout.ContainsKey(mangled) || classDirectMethods.ContainsKey(mangled))
                    return mangled;
                string bare = ResolveCallee(mm.Member);
                return classFieldLayout.ContainsKey(bare) || classDirectMethods.ContainsKey(bare)
                    ? bare : null;
            }
            default:
                return null;
        }
    }

    // `a.b.C` -> "a.b.C" when the chain is rooted at a plain name; null for anything
    // deeper (a call or subscript inside the chain is not a resolvable dotted name).
    private static string? DottedExprText(Expression e)
    {
        var parts = new List<string>();
        Expression cur = e;
        while (cur is MemberAccessExpr ma) { parts.Insert(0, ma.Member); cur = ma.Object; }
        if (cur is not VariableExpr root || parts.Count == 0) return null;
        parts.Insert(0, root.Name);
        return string.Join(".", parts);
    }

    private void RecordClassAttrWriteTarget(Expression target)
    {
        switch (target)
        {
            // `self.x` reaches here too and records "self.x", which matches no class name and
            // costs nothing. Narrowing this to known classes is not possible yet: this runs
            // before the pass that collects them, which is the whole point of running early.
            case MemberAccessExpr { Object: VariableExpr ov } ma:
                writtenClassAttributes.Add(ov.Name + "." + ma.Member);
                break;
            // `Dev.TABLE[0] = x` writes THROUGH the attribute. A folded constant has no
            // storage to index into, so the name has to keep its slot.
            case IndexExpr { Target: MemberAccessExpr { Object: VariableExpr ov2 } ma2 }:
                writtenClassAttributes.Add(ov2.Name + "." + ma2.Member);
                break;
            case TupleExpr tup: foreach (var e in tup.Elements) RecordClassAttrWriteTarget(e); break;
        }
    }

    /// <summary>
    /// Remember the initializer of a class-body attribute that did NOT become a compile-time
    /// constant, so it can be run as part of its module's init.
    ///
    /// Registering the storage was only half the job: nothing ever wrote the declared value,
    /// so `class Dev: limit = 7` read back 0 while `LIMIT = 7` two lines above read 7, with
    /// no error and no warning (#270). The module-level spelling of the same program has
    /// always worked, because a module's statements run and a class body's do not.
    ///
    /// The synthesized target is `Cls_attr` -- the flattened name the storage is registered
    /// under, minus the module prefix -- because the statement is compiled under the declaring
    /// module's own prefix, which puts it back.
    /// </summary>
    private void RecordClassAttrInit(ProgramNode ast, string className, string attrName,
                                     string attrType, Expression init, int? constValue)
    {
        // An aggregate is not a scalar global: its storage is sized by the array machinery,
        // not by this registration, and assigning a list literal to the single byte registered
        // here would be worse than the zero it reads today. Left exactly as it was.
        if (attrType.Contains('[')) return;
        if (init is ListExpr or DictExpr or SetExpr or TupleExpr) return;
        if (!string.IsNullOrEmpty(attrType)
            && DataTypeExtensions.StringToDataType(attrType) == DataType.UNKNOWN) return;

        // An UNANNOTATED attribute is typed from its initializer alone, exactly as an
        // unannotated module global is. StringToDataType("") answers uint8 for everything, and
        // the store this schedules would then truncate the attribute's own declared value:
        // `limit = 300` read back 44. NarrowLiteralOnlyGlobals does this for module level and
        // cannot see a class body -- it collects names, and this one lives under `Cls_`.
        if (string.IsNullOrEmpty(attrType) && constValue is { } cv)
            mutableGlobals[currentModulePrefix + attrName] = NarrowestTypeFor(cv, cv);

        var stmt = new AssignStmt(new VariableExpr(className + "_" + attrName), init)
            { Line = init.Line };
        if (!classAttrInits.TryGetValue(ast, out var list))
            classAttrInits[ast] = list = new List<Statement>();
        list.Add(stmt);
    }

    /// <summary>
    /// Registers a class body's ATTRIBUTES under the class prefix, and recurses into a class
    /// declared inside it. The recursion is what #319 was missing: `Outer.Inner.A` had no name
    /// anywhere, so the read was refused with "object has no attribute 'Inner'" -- a sentence
    /// about the hop rather than about the constant. It is how CircuitPython spells the UART
    /// parity (`busio.UART.Parity.ODD`), so the canonical spelling did not compile.
    /// </summary>
    /// <summary>
    /// The address a class-level register declaration names, resolved during the scan.
    /// <para>
    /// A grouped peripheral (RFC 0012) re-groups registers the module already declares:
    /// <c>TCCR1A: ptr[uint8] = ptr(TCCR1A)</c> keeps ONE copy of every address, so the
    /// grouped surface cannot drift from the loose one. EvaluateConstantExpr cannot answer
    /// that: it refuses a memory-address symbol as an operand, on purpose, so a register
    /// read never folds into an array size. This resolver answers only for an ADDRESS, and
    /// only from symbols the scan has already filed, so it emits nothing and needs no
    /// expression machinery.
    /// </para>
    /// </summary>
    private int? TryScanRegisterAddress(Expression e, string enclosingPrefix)
    {
        switch (e)
        {
            case IntegerLiteral il:
                return il.Value;

            case CallExpr { Args.Count: 1 } call when call.Callee is VariableExpr callee
                && ((callee.Name == "ptr" && intrinsicNames.Contains("ptr"))
                    || callee.Name == "PIORegister" || callee.Name == "const"):
                return TryScanRegisterAddress(call.Args[0], enclosingPrefix);

            case UnaryExpr ue when ue.Op is PyMCU.Frontend.UnaryOp.Negate
                                         or PyMCU.Frontend.UnaryOp.BitNot:
                if (TryScanRegisterAddress(ue.Operand, enclosingPrefix) is not int uv) return null;
                return ue.Op == PyMCU.Frontend.UnaryOp.Negate ? -uv : ~uv;

            case BinaryExpr be:
            {
                if (TryScanRegisterAddress(be.Left, enclosingPrefix) is not int l) return null;
                if (TryScanRegisterAddress(be.Right, enclosingPrefix) is not int r) return null;
                return be.Op switch
                {
                    PyMCU.Frontend.BinaryOp.Add => l + r,
                    PyMCU.Frontend.BinaryOp.Sub => l - r,
                    PyMCU.Frontend.BinaryOp.Mul => l * r,
                    PyMCU.Frontend.BinaryOp.Div or PyMCU.Frontend.BinaryOp.FloorDiv
                        => r != 0 ? l / r : (int?)null,
                    PyMCU.Frontend.BinaryOp.Mod => r != 0 ? l % r : (int?)null,
                    PyMCU.Frontend.BinaryOp.BitAnd => l & r,
                    PyMCU.Frontend.BinaryOp.BitOr => l | r,
                    PyMCU.Frontend.BinaryOp.BitXor => l ^ r,
                    PyMCU.Frontend.BinaryOp.LShift => l << r,
                    PyMCU.Frontend.BinaryOp.RShift => l >> r,
                    _ => null,
                };
            }

            case VariableExpr ve:
            {
                // The class's own prefix first (a register declared earlier in the same
                // group), then the module the class is written in, then the flat name.
                foreach (var key in new[] { currentModulePrefix + ve.Name, enclosingPrefix + ve.Name, ve.Name })
                {
                    if (!string.IsNullOrEmpty(key) && globals.TryGetValue(key, out var sym)) return sym.Value;
                }
                return null;
            }

            default:
                try { return EvaluateConstantExpr(e); }
                catch { return null; }
        }
    }

    private void ScanClassBodyAttributes(ProgramNode ast, string className, Block block, bool isEnum,
                                         string enclosingPrefix = "")
    {
        foreach (var innerStmt in block.Statements)
        {
            // `pwm_pins += (6, 7, 8)` in a class body: CPython concatenates the tuples
            // at class creation. The binding below holds the first tuple; extend it the
            // same way here (adafruit_seesaw attiny8x7 / attinyx16 spell it so).
            if (innerStmt is AugAssignStmt { Target: VariableExpr augTgt } aug
                && aug.Op == Frontend.AugOp.Add
                && aug.Value is TupleExpr or ListExpr
                && constSequenceBindings.TryGetValue(currentModulePrefix + augTgt.Name,
                                                     out var augSeq))
            {
                var augElems = aug.Value is TupleExpr at ? at.Elements : ((ListExpr)aug.Value).Elements;
                foreach (var el in augElems)
                    augSeq.Add(TryFoldConstElement(el, out int aev)
                        ? new IntegerLiteral(aev) { Line = el.Line } : el);
                continue;
            }

            var innerName = "";
            var innerType = "";
            Expression? innerInit = null;

            switch (innerStmt)
            {
                case VarDecl vDecl:
                    innerName = vDecl.Name;
                    innerType = vDecl.VarType;
                    innerInit = vDecl.Init;
                    break;
                case AssignStmt { Target: VariableExpr iVar } iAssign:
                    innerName = iVar.Name;
                    innerInit = iAssign.Value;
                    break;
                case AnnAssign iAnnAssign:
                    innerName = iAnnAssign.Target;
                    innerType = iAnnAssign.Annotation;
                    innerInit = iAnnAssign.Value;
                    break;
            }

            if (string.IsNullOrEmpty(innerName) || innerInit == null) continue;

            // A class-level dict is a compile-time lookup table, the same as a module-level
            // one. ScanGlobals registers those; the class body went through EvaluateConstantExpr,
            // which cannot fold a dict, and `self.gain_values[gain]` then fell through to a
            // register bit index -- "Bit index must be constant for reading" on a program with
            // no register in it. Adafruit VEML7700's gain_values / integration_time_values
            // are this shape. Fold class-constant keys here, while currentModulePrefix still
            // names the class, so ALS_GAIN_2 is in globals.
            if (innerInit is DictExpr dictInit)
            {
                dictLiteralBindings[currentModulePrefix + innerName] =
                    FoldDictConstEntries(dictInit);
                mutableGlobals[currentModulePrefix + innerName] =
                    DataTypeExtensions.StringToDataType(innerType);
                continue;
            }
            if (innerInit is SetExpr setInit)
            {
                setLiteralBindings[currentModulePrefix + innerName] = setInit;
                mutableGlobals[currentModulePrefix + innerName] =
                    DataTypeExtensions.StringToDataType(innerType);
                continue;
            }

            // A class-level tuple/list is a compile-time sequence: it has no storage and
            // the binding IS the whole meaning, same as a module-level one. Without this
            // `Cls.pins[0]` folded to a bit check on a zero-initialised scalar -- the
            // wrong answer said cleanly. Elements fold HERE, while currentModulePrefix
            // still names the class, so a name like _ADC_INPUT_0_PIN resolves to this
            // module's constant; a read in another module's method would not find it
            // (adafruit_seesaw's pinmap classes are exactly this shape).
            if (innerInit is TupleExpr or ListExpr)
            {
                var seqElems = innerInit is TupleExpr tq ? tq.Elements
                    : ((ListExpr)innerInit).Elements;
                var foldedSeq = new List<Expression>(seqElems.Count);
                foreach (var el in seqElems)
                    foldedSeq.Add(TryFoldConstElement(el, out int sev)
                        ? new IntegerLiteral(sev) { Line = el.Line } : el);
                constSequenceBindings[currentModulePrefix + innerName] = foldedSeq;
                continue;
            }

            // A class-level `TCCR1A: ptr[uint8] = ptr(0x80)` is a REGISTER, not a class
            // constant: the name carries an address and a width, and every `.value` /
            // `[bit]` access on it has to reach the MMIO paths. Folded to a plain Constant
            // it lost the width and left the write side with a Constant target it refuses
            // ("Cannot assign to .value of this expression type"). Module level has
            // recognised this shape since the first chip definition; a grouped peripheral
            // (RFC 0012) is the same declaration one scope deeper, so it is recognised the
            // same way, BEFORE the ALL-CAPS gate -- a register has no storage whatever its
            // name looks like -- and before the generic fold, because
            // EvaluateConstantExpr refuses a register SYMBOL as an operand and the catch
            // below then filed the group as a dead SRAM variable, in silence.
            if (!isEnum
                && ((innerInit is CallExpr regCall
                     && regCall.Callee is VariableExpr regCallee
                     && ((regCallee.Name == "ptr" && intrinsicNames.Contains("ptr"))
                         || regCallee.Name == "PIORegister"))
                    || (!string.IsNullOrEmpty(innerType)
                        && (innerType.Contains("ptr") || innerType.Contains("PIORegister")))))
            {
                if (TryScanRegisterAddress(innerInit, enclosingPrefix) is not int regAddr)
                    throw UserError(
                        $"'{innerName}' declares a register, so its address must be known while "
                        + "compiling -- a literal (ptr(0x80)), constant arithmetic on one, or the "
                        + "name of a register already declared in this module",
                        innerInit.Line > 0 ? innerInit : innerStmt);

                globals[currentModulePrefix + innerName] = new SymbolInfo
                {
                    IsMemoryAddress = true, Value = regAddr,
                    Type = DataTypeExtensions.StringToDataType(innerType),
                };
                // The QUALIFIED name only. Registering the bare spelling too reserved it
                // for the whole program: the target's chip file is scanned whether or not
                // the program imports it, so its `class Timer1` made a user's own
                // `class Timer1` unconstructible, with a message about silicon and
                // registers the user never named. A group is refused where the name
                // RESOLVES to it, not where the spelling matches.
                registerGroupClasses.Add(enclosingPrefix + className);
                continue;
            }

            try
            {
                var val = EvaluateConstantExpr(innerInit);

                // A name this program WRITES is not a constant, whatever it is
                // called: the fold left the write nowhere to land and it was
                // dropped in silence (#272). Module level has had this same gate
                // on `reassigned` since #220. An enum member keeps folding: its
                // value is the member's identity, not a variable's contents.
                var isAllUpper = innerName.All(c => !char.IsLower(c))
                    && !writtenClassAttributes.Contains(className + "." + innerName);

                if (isAllUpper || isEnum)
                {
                    globals[currentModulePrefix + innerName] = new SymbolInfo
                        { IsMemoryAddress = false, Value = val };
                }
                else
                {
                    mutableGlobals[currentModulePrefix + innerName] =
                        DataTypeExtensions.StringToDataType(innerType);
                    RecordClassAttrInit(ast, className, innerName, innerType,
                                        innerInit, val);
                }
            }
            catch
            {
                if (!isEnum)
                {
                    mutableGlobals[currentModulePrefix + innerName] =
                        DataTypeExtensions.StringToDataType(innerType);
                    // No folded value: the initializer is a run-time expression, so
                    // there is nothing to size the storage from beyond the
                    // annotation, and the store runs the expression as written.
                    RecordClassAttrInit(ast, className, innerName, innerType,
                                        innerInit, null);
                }
            }
        }

        foreach (var innerStmt in block.Statements)
        {
            if (innerStmt is not ClassDef deeper || deeper.Body is not Block deeperBlock) continue;
            var savedPrefix = currentModulePrefix;
            currentModulePrefix += deeper.Name + "_";
            ScanClassBodyAttributes(ast, className + "_" + deeper.Name, deeperBlock,
                                    deeper.Bases.Contains("Enum") || deeper.Bases.Contains("IntEnum"),
                                    savedPrefix);
            currentModulePrefix = savedPrefix;
        }
    }

    /// <summary>
    /// Class-constant names in a class-body dict (<c>ALS_GAIN_2: 2</c>) resolve while the
    /// scan still has that class's prefix. Later, from an inlined method of an imported
    /// class, the bare name is not in scope.
    /// </summary>
    private DictExpr FoldDictConstEntries(DictExpr d)
    {
        var folded = new List<(Expression, Expression)>(d.Entries.Count);
        foreach (var (k, v) in d.Entries)
            folded.Add((FoldDictEntryExpr(k), FoldDictEntryExpr(v)));
        return new DictExpr(folded) { Line = d.Line, Column = d.Column, Length = d.Length };
    }

    private Expression FoldDictEntryExpr(Expression e)
    {
        if (e is IntegerLiteral or FloatLiteral or StringLiteral or BooleanLiteral) return e;
        try
        {
            int n = EvaluateConstantExpr(e);
            return new IntegerLiteral(n) { Line = e.Line, Column = e.Column, Length = e.Length };
        }
        catch
        {
            return e;
        }
    }

    private void ScanGlobals(ProgramNode ast, ModuleScope? scope = null)
    {
        var reassigned = CollectModuleReassignedNames(ast);
        foreach (var n in reassigned) reassignedGlobals.Add(currentModulePrefix + n);
        // The subset whose writes happen from FUNCTION flow (`global x` inside a
        // def or method): the module's last store is not the value a later call
        // sees, so these are never recorded as constants at all. A name only the
        // module rebinds is different -- its stores are ordered, so the flow map
        // can hold it, and only reads inside other functions have to pass on it.
        foreach (var n in CollectGlobalDeclaredNames(ast))
            functionWrittenGlobals.Add(currentModulePrefix + n);

        // Collect every member name used as an assignment target anywhere in this module
        // (recursing into class methods and nested blocks). This forms the superset of all
        // class fields used to flag a read of an undefined instance attribute.
        foreach (var stmt in ast.GlobalStatements)
        {
            CollectAssignedMemberNames(stmt);
            CollectBoolNames(stmt);
        }
        foreach (var fn in ast.Functions) CollectBoolNames(fn);

        foreach (var stmt in ast.GlobalStatements)
        {
            string name = "";
            string type = "";
            Expression? initializer = null;

            // A bare module-level `raise CompileError(...)` in an imported module is an
            // arch/chip guard whose enclosing if/match was folded away. Record it so a use
            // of any symbol from this module reports the guard's message (see
            // EmitRegularFunctionCall); aborting here would be too eager — the module may
            // be pulled in transitively (hal/__init__.py) without its symbols being used.
            if (stmt is RaiseStmt guard && guard.ErrorType == "CompileError"
                && !string.IsNullOrEmpty(currentModulePrefix)
                && !moduleGuardErrors.ContainsKey(currentModulePrefix))
            {
                moduleGuardErrors[currentModulePrefix] = (
                    guard.Message.Length > 0 ? guard.Message : "module not supported on this target",
                    currentSourceFile, currentSourcePath, guard.Line, guard.Column, guard.Length);
                continue;
            }

            if (stmt is VarDecl varDecl)
            {
                name = varDecl.Name;
                type = varDecl.VarType;
                initializer = varDecl.Init;

                // The same immutability the AnnAssign branch below records. Which of the two
                // branches a declaration lands in is decided by the parser on one character:
                // an annotation containing '[' becomes an AnnAssign and anything else a
                // VarDecl. So `LIMIT: const[uint8] = 10` was recorded and refused a rebind,
                // and `LIMIT: const = 10` was recorded nowhere and accepted one in silence,
                // which is a const enforced or not depending on how it was spelled (#217).
                if (IsConstType(type))
                    declaredConstants.Add(currentModulePrefix + name);

                if (type == "bytearray" && initializer != null)
                    TryRegisterModuleBytearray(name, initializer);

                if ((type == "str" || type == "const[str]") && initializer is StringLiteral vdStr)
                    strConstantVariables[currentModulePrefix + name] = vdStr.Value;
            }
            else if (stmt is AssignStmt assign)
            {
                if (assign.Target is VariableExpr varExpr)
                {
                    name = varExpr.Name;
                    initializer = assign.Value;

                    // Unannotated module-level `name = bytearray(...)` (MicroPython idiom):
                    // register the fixed buffer just like the annotated form.
                    TryRegisterModuleBytearray(name, initializer);

                    // `g = [[v]*W for _ in range(H)]` at module level: a compile-time
                    // 2-D grid -- one flat array of W*H elements filed under the
                    // bare name, so the synthesized init's replay aliases `main.g`
                    // onto it exactly like `name = bytearray(N)`.
                    TryRegisterModuleGrid(name, initializer);

                    // Unannotated module-level `NAME = "..."`, for the same reason. Only the
                    // two annotated spellings above were registered here, so the bare one
                    // reached strConstantVariables solely as a side effect of LOWERING the
                    // module statement. Anything lowered before that could not see it, and a
                    // plain helper is: `raise CompileError(MSG)` in one was refused for a
                    // constant declared directly above it, while the same raise in main, at
                    // module level, or in an @inline helper resolved. #239.
                    if (initializer is StringLiteral asgStr)
                        strConstantVariables[currentModulePrefix + name] = asgStr.Value;
                }
            }
            else if (stmt is AnnAssign annAssign)
            {
                name = annAssign.Target;
                type = annAssign.Annotation;
                initializer = annAssign.Value;

                // A `const` annotation marks the name immutable; record it so a later
                // assignment to it is rejected (see VisitAssign's reassignment guard).
                //
                // Through IsConstType, which is the predicate the rest of the compiler uses and
                // accepts the bare spelling as well as the subscripted one. This site tested for
                // `const[` alone, so `LIMIT: const = 10` was never recorded and `global LIMIT`
                // then `LIMIT = 20` was accepted in silence, while `LIMIT: const[uint8] = 10`
                // was refused: one declaration enforced, one not, with nothing to say which was
                // which (#217). Module-level declarations only reach here; VisitAnnAssign's own
                // registration, which has always used IsConstType, is never called for them.
                if (IsConstType(type))
                    declaredConstants.Add(currentModulePrefix + name);

                // A module-level string constant (`str` or `const[str]`). Register its
                // compile-time value under the module-global key so ResolveStrConstant resolves
                // it for subscripting (S[i]), len(S) and iteration — previously these silently
                // dropped because ScanGlobals (which owns module globals) never recorded it.
                // (Done before the const[...] dispatch below, which would otherwise consume
                // const[str] as a malformed flash array.)
                if ((type == "str" || type == "const[str]") && initializer is StringLiteral strLit)
                    strConstantVariables[currentModulePrefix + name] = strLit.Value;

                // Detect const[uint8[N]] flash array annotation.
                if (type.StartsWith("const[") && type.EndsWith("]"))
                {
                    string constInner = type.Substring(6, type.Length - 7); // strip "const[" and "]"
                    int ciB = constInner.IndexOf('[');
                    int ciC = constInner.LastIndexOf(']');
                    if (ciB != -1 && ciC == constInner.Length - 1 && ciC > ciB + 1)
                    {
                        string ciNum = constInner.Substring(ciB + 1, ciC - ciB - 1);
                        if (!string.IsNullOrEmpty(ciNum) && ciNum.All(char.IsDigit))
                        {
                            int count = int.Parse(ciNum);
                            DataType elemDt = DataTypeExtensions.StringToDataType(constInner.Substring(0, ciB));
                            bool ciInteger = elemDt is DataType.UINT8 or DataType.INT8
                                or DataType.UINT16 or DataType.INT16
                                or DataType.UINT32 or DataType.INT32;
                            if (ciInteger)
                            {
                                // A table whose elements are wider than a byte used to fall
                                // through here entirely: the name was never registered as an
                                // array, so `T[i]` lowered to a REGISTER BIT TEST on a scalar
                                // that does not exist, every read folded to zero, and a run-time
                                // index failed the build talking about bit indices. A 16-bit
                                // calibration table in flash is one of the most ordinary things
                                // on an 8-bit part, precisely because the values do not fit in
                                // a byte.
                                //
                                // FlashData stays byte-oriented, and the element is stored
                                // little-endian, so the reader (see the flash branch of the
                                // subscript lowering) walks SizeOf() bytes and reassembles it.
                                // That needs no new IR node and no backend change, which is what
                                // makes every target that already reads a const[uint8[N]] table
                                // read a wide one too.
                                int elemSize = elemDt.SizeOf();
                                arraySizes[name] = count;
                                bufferLogicalLen[name] = count;
                                arrayElemTypes[name] = elemDt;
                                flashArrays.Add(name);

                                // Collect FlashData so Generate() can inject it into the
                                // main function body; ScanGlobals runs before VisitFunction.
                                // The same computed-sequence forms the function-scope path
                                // takes: a literal, `[x]*n`, concat, range() -- a missed
                                // one emitted a right-sized table of zeros.
                                var bytes = new List<int>(Enumerable.Repeat(0, count * elemSize));
                                var constElems = initializer != null
                                    ? TryConstElementSequence(initializer) : null;
                                if (constElems != null)
                                {
                                    for (int k = 0; k < Math.Min(count, constElems.Count); k++)
                                        if (TryEvalElemConst(constElems[k], out int v))
                                            for (int b = 0; b < elemSize; b++)
                                                bytes[k * elemSize + b] = (v >> (8 * b)) & 0xFF;
                                }
                                pendingFlashData.Add(new FlashData(name, bytes));
                            }
                        }
                    }
                }
                else if (type.StartsWith("list[") && type.EndsWith("]"))
                {
                    string elemTypeName = type.Substring(5, type.Length - 6);
                    DataType elemDt = DataTypeExtensions.StringToDataType(elemTypeName);
                    listVarElemTypes[name] = elemDt;
                    if (elemTypeName.StartsWith("list[") && elemTypeName.EndsWith("]"))
                        listInnerElemTypes[name] = DataTypeExtensions.StringToDataType(
                            elemTypeName.Substring(5, elemTypeName.Length - 6));
                }
                else
                {
                    int bracket = type.IndexOf('[');
                    int close = type.LastIndexOf(']');
                    if (bracket != -1 && close != -1 && close == type.Length - 1 && close > bracket + 1)
                    {
                        string inner = type.Substring(bracket + 1, close - bracket - 1);
                        if (!string.IsNullOrEmpty(inner) && inner.All(char.IsDigit))
                        {
                            int count = int.Parse(inner);
                            DataType elemDt = DataTypeExtensions.StringToDataType(type.Substring(0, bracket));
                            arraySizes[name] = count;
                            bufferLogicalLen[name] = count;
                            arrayElemTypes[name] = elemDt;
                            moduleSramArrays.Add(name);
                            // `g: uint8[W*H] = <grid comp>`: the dims ride along
                            // so a read before the replay still sees the grid.
                            if (initializer is ListCompExpr annGridComp
                                && IsGridComprehension(annGridComp)
                                && TryFoldGridDims(annGridComp, out var annDims)
                                && annDims.W * annDims.H == count)
                                gridDims[name] = annDims;
                        }
                    }
                }
            }
            else if (stmt is ClassDef classDef)
            {
                var oldPrefix = currentModulePrefix;
                currentModulePrefix += classDef.Name + "_";
                classModuleMap[classDef.Name] = oldPrefix;

                var isEnum = classDef.Bases.Contains("Enum") || classDef.Bases.Contains("IntEnum");
                if (isEnum)
                {
                    enumClassNames.Add(classDef.Name);
                    enumClassNames.Add(oldPrefix + classDef.Name);
                }

                if (classDef.Body is Block block)
                    ScanClassBodyAttributes(ast, classDef.Name, block, isEnum, oldPrefix);

                currentModulePrefix = oldPrefix;
            }

            if (!string.IsNullOrEmpty(name) && initializer != null)
            {
                // `wake_alarm = None` in an INSTALLED module: its top level never runs (only
                // the project's modules get a __module_init), so the None was never recorded
                // and `alarm.wake_alarm is None` answered False from a byte nobody wrote. A
                // name the module never writes again is None for good, which is a fact this
                // scan can record for every module alike.
                if (initializer is NoneLiteral && !reassigned.Contains(name))
                    noneValuedNames.Add(currentModulePrefix + name);

                // Dict/set literals bind their AST as a compile-time lookup table -- no
                // storage, no constant fold (EvaluateConstantExpr would throw and the
                // fallback would mis-register them as 1-byte mutable globals).
                if (initializer is DictExpr dictInit)
                {
                    dictLiteralBindings[currentModulePrefix + name] = dictInit;
                    continue;
                }
                if (initializer is SetExpr setInit)
                {
                    setLiteralBindings[currentModulePrefix + name] = setInit;
                    continue;
                }

                // A module-level FLOAT is storage of its own width, and it is registered here
                // before anything tries to fold it. EvaluateConstantExpr answers for a float
                // literal by TRUNCATING it -- 0.1 gives 0 and 1.5 gives 1 -- so the name was
                // typed an integer from that answer, the module-level store folded to the same
                // integer, and every read came back as a small int: `timeout = 0.1` then
                // `timeout * 1000000.0` printed 0.0, with nothing said. An int global one line
                // above keeps its value and a float literal in a function body is typed
                // correctly, which is what said the gap was this scan (#379).
                //
                // Both spellings go the same way, including the ALL-CAPS one: that convention
                // gives a name no storage and folds every read from a SymbolInfo, which carries
                // an int and has no float to carry.
                if (IsFloatModuleBinding(type, initializer))
                {
                    mutableGlobals[currentModulePrefix + name] = DataType.FLOAT;
                    if (scope != null) scope.MutableGlobals[name] = DataType.FLOAT;
                    continue;
                }

                try
                {
                    // `x = SOME_CONST` registers x as a constant ALIAS -- but only when x
                    // is never written again: a mutable variable whose INITIALIZER is a
                    // named constant (state: uint8 = IDLE, then state = HEAT in the loop)
                    // must stay a runtime global, or every later write silently vanishes.
                    if (initializer is VariableExpr varExprInit && !reassigned.Contains(name))
                    {
                        SymbolInfo? sourceInfo = null;
                        string lookupLocal = currentModulePrefix + varExprInit.Name;

                        if (globals.TryGetValue(lookupLocal, out var localSym))
                        {
                            sourceInfo = localSym;
                        }
                        else
                        {
                            foreach (var modName in modules.Keys)
                            {
                                string modKey = modName + "_" + varExprInit.Name;
                                if (globals.TryGetValue(modKey, out var modSym))
                                {
                                    sourceInfo = modSym;
                                    break;
                                }
                            }
                        }

                        if (sourceInfo.HasValue)
                        {
                            globals[currentModulePrefix + name] = sourceInfo.Value;
                            // The alias path must mirror into the scope like the
                            // literal path below: `mod_member` reads gate on the
                            // module's own Globals, so `ORDER = GRB` unseen here
                            // makes `ORDER` stop folding for readers (the
                            // cond-tuple-return probe's `ORDER in {RGB, GRB}`).
                            if (scope != null) scope.Globals[name] = sourceInfo.Value;
                            continue;
                        }
                    }

                    int val = EvaluateConstantExpr(initializer);
                    bool isMemoryAddress = false;

                    if (initializer is CallExpr callInit && callInit.Callee is VariableExpr cVar)
                    {
                        if ((cVar.Name == "ptr" && intrinsicNames.Contains("ptr")) || cVar.Name == "PIORegister")
                        {
                            isMemoryAddress = true;
                        }
                    }

                    if (!string.IsNullOrEmpty(type) && (type.Contains("ptr") || type.Contains("PIORegister")))
                    {
                        isMemoryAddress = true;
                    }

                    if (isMemoryAddress)
                    {
                        var info = new SymbolInfo
                            { IsMemoryAddress = true, Value = val, Type = DataTypeExtensions.StringToDataType(type) };
                        globals[currentModulePrefix + name] = info;
                        if (scope != null) scope.Globals[name] = info;
                    }
                    else
                    {
                        // ALL CAPS means "constant" by convention here, and the convention is
                        // what gives the name no storage so every read folds the initializer.
                        // A name this module WRITES is not one, whatever it is called, and
                        // `reassigned` is the set that already knows: its own comment says a
                        // second assignment or a `global` declaration makes the initializer
                        // merely happen to be constant. It was consulted for an alias
                        // initializer and not here, so `N = 10` with `global N; N = 20`
                        // elsewhere produced `globals: []` and a copy whose DESTINATION was the
                        // literal 10. The write went nowhere and every read folded 10 (#220).
                        //
                        // The lowercase spelling of the same program has always worked, which
                        // is what says this is the convention overriding a written statement
                        // rather than module globals being unsupported.
                        bool isAllUpper = name.All(c => !char.IsLower(c)) && !reassigned.Contains(name);
                        if (isAllUpper)
                        {
                            var info = new SymbolInfo { IsMemoryAddress = false, Value = val };
                            globals[currentModulePrefix + name] = info;
                            if (scope != null) scope.Globals[name] = info;
                        }
                        else
                        {
                            DataType t = DataTypeExtensions.StringToDataType(type);
                            mutableGlobals[currentModulePrefix + name] = t;
                            if (scope != null) scope.MutableGlobals[name] = t;

                            // An UNANNOTATED global is typed here from its initializer alone --
                            // `total = 0` is uint8 -- and a later store from inside a function is
                            // not in this scan's reach. Left un-widenable, `total = total + r`
                            // with r uint16 wrote 16 bits into 8 and a moving average wrapped
                            // silently (#205). Only the initializer-failed path used to register
                            // here, so a global whose initializer typed FINE could never widen.
                            // The store site widens on the real RHS width and never narrows.
                            if (string.IsNullOrEmpty(type))
                                widenableGlobals.Add(currentModulePrefix + name);
                        }
                    }
                }
                catch
                {
                    // SRAM arrays (already in moduleSramArrays) must not be added to
                    // mutableGlobals here — their size is determined by ArrayStore/ArrayLoad
                    // instructions in the StackAllocator, which uses count * elemSize.
                    // Adding them with StringToDataType("T[N]") → UNKNOWN (1 byte) would
                    // under-allocate SRAM and cause layout corruption.
                    if (moduleSramArrays.Contains(name)) continue;
                    DataType t = DataTypeExtensions.StringToDataType(type);
                    mutableGlobals[currentModulePrefix + name] = t;
                    if (scope != null) scope.MutableGlobals[name] = t;
                    if (string.IsNullOrEmpty(type))
                        widenableGlobals.Add(currentModulePrefix + name);
                }

                // Track module-level singleton instances (e.g. `mem8 = _Mem8()`) so
                // that subscript and method dispatch via GetValClass works when these
                // singletons are imported by user code.
                if (initializer is CallExpr ctorCallInst
                    && ctorCallInst.Callee is VariableExpr ctorVarInst)
                {
                    string fullKey = currentModulePrefix + name;
                    if (classModuleMap.TryGetValue(ctorVarInst.Name, out var classMod) && classMod != null)
                    {
                        instanceClasses[fullKey] = classMod + ctorVarInst.Name;
                    }
                    else
                    {
                        // The class was imported through a facade re-export (e.g. wifi.py does
                        // `from pymcu.hal.wifi import CYW43`, itself re-exported from the concrete
                        // module). Its concrete module isn't in classModuleMap yet (scan order),
                        // so record the import-resolved name; the dispatch maps it to the concrete
                        // class via ResolveConcreteClass once every class is scanned.
                        // A FUNCTION imported the same way resolves to the same mangled shape
                        // (`from mylib import plain` -> `mylib_plain`), and tagging its result
                        // as an instance of `plain` refused `print(w)` as "cannot interpolate
                        // an instance". The imported module is scanned before this one, so a
                        // function it defines is already known here.
                        string rc = ResolveCallee(ctorVarInst.Name);
                        bool rcIsFunction = functionParams.ContainsKey(rc)
                                            || inlineFunctions.ContainsKey(rc)
                                            || overloadedFunctions.Contains(rc);
                        if (rc.Contains('_') && rc != ctorVarInst.Name && !rcIsFunction
                            && !intrinsicNames.Contains(ctorVarInst.Name))
                            instanceClasses[fullKey] = rc;
                    }
                }
            }
        }

        NarrowLiteralOnlyGlobals(ast);
        MarkStrGlobalsRebound(ast);
    }

    /// <summary>
    /// Takes the compile-time value away from a module-level str that a FUNCTION binds to
    /// another text. Functions are lowered in an order the program does not decide, and the
    /// call that rebinds the name may not even run, so no single text is right at a read:
    /// `state = "idle"` with a `global state; state = "running"` elsewhere printed "idle" on
    /// every path, the store dead in a function nobody could see from the read (issue #145).
    ///
    /// Only assignments the language accepts as writing the global count: one under a
    /// `global` declaration, or one in `main`, which IS the module's top-level scope.
    /// </summary>
    private void MarkStrGlobalsRebound(ProgramNode ast)
    {
        // Methods are in the same scope-rule universe as functions for `global`: a str
        // binding a method rebinds under the declaration is a run-time name, the same as a
        // rebind inside a plain function.
        foreach (var fn in ast.Functions.Concat(TypeInference.ClassMethods(ast)))
        {
            var declaredGlobal = new HashSet<string>();
            CollectGlobalDeclarations(fn.Body, declaredGlobal);

            foreach (var kv in CollectStrBindings(new List<Statement> { fn.Body }))
            {
                if (fn.Name != "main" && !declaredGlobal.Contains(kv.Key)) continue;
                string key = currentModulePrefix + kv.Key;
                if (!strConstantVariables.TryGetValue(key, out var declared)) continue;
                if (kv.Value.Count == 1 && kv.Value[0] == declared) continue;

                multiStrCandidates[key] = kv.Value.Prepend(declared)
                    .Where(v => v != null).Select(v => v!).Distinct().ToList();
                MarkMultiStr(key, multiStrCandidates[key]);
            }
        }
    }

    /// <summary>Every name a `global` statement in this body declares.</summary>
    private static void CollectGlobalDeclarations(Statement? st, HashSet<string> into)
    {
        foreach (var s in TypeInference.WalkStatements(st))
            if (s is GlobalStmt g)
                foreach (var n in g.Names) into.Add(n);
    }

    /// <summary>
    /// Gives an unannotated module-level integer the width that holds every literal assigned to
    /// it. Without this the FIRST store fixed the width and every later value was truncated into
    /// it, in silence: `b = 5` then `b = 300` stored 44, and printed 44 on the board. Module
    /// level is the MicroPython and CircuitPython shape -- those programs have no def main() at
    /// all -- so it was the default spelling for anyone arriving from either port.
    /// </summary>
    private void NarrowLiteralOnlyGlobals(ProgramNode ast)
    {
        // A global written from inside a function is not literal-only: those assignments are not
        // in this scan's reach, so the name keeps whatever width it had. A method's body is
        // one of those functions: the name stays out of literal-only narrowing there too.
        var assignedInFunctions = new HashSet<string>();
        foreach (var fn in ast.Functions.Concat(TypeInference.ClassMethods(ast)))
            CollectAssignedNames(fn.Body, assignedInFunctions);

        var widths = CollectLiteralOnlyWidths(ast.GlobalStatements, assignedInFunctions);
        foreach (var kv in widths)
        {
            string key = currentModulePrefix + kv.Key;
            // Only names this module actually holds as a mutable global. A name carrying a
            // written annotation never reaches here: the scan above drops it.
            if (!mutableGlobals.ContainsKey(key)) continue;
            mutableGlobals[key] = kv.Value;
            widenableGlobals.Remove(key);
        }

        // What the FUNCTIONS assign decides the width too. The pass above sees only module-level
        // statements, so an accumulator initialised `total = 0` and then fed a uint16 inside a
        // function stayed eight bits and wrapped without a word (#205). Widening only: a name
        // this cannot type keeps exactly what the scan above gave it.
        //
        // The initializer has to be in this widening too, and it was not (#212). The exclusion
        // above is what keeps a function's assignment from being NARROWED away, and it was
        // written when nothing reached inside a function at all -- the comment on it still says
        // "not in this scan's reach", which stopped being true when the loop below was added.
        // With `wide = 300` at module level and `wide = 7` in a function, the initializer was
        // dropped for narrowing and 7 was not wide enough to widen, so the 300 the author wrote
        // was truncated at its own defining store and `wide` came out 44.
        //
        // Taking the widest of the two is safe in the direction that matters: widening only ever
        // adds room, so a function assigning something this cannot type still keeps whatever the
        // narrowing pass allowed, and a name the narrowing pass already sized is untouched.
        var initializerWidths = CollectLiteralOnlyWidths(ast.GlobalStatements, []);
        // The module's own statements are scanned the way `main`'s are: an accumulator fed
        // at module level (`n = 0` then `n = n + 1` inside a module-level loop) is typed from
        // the promoted width of its right-hand side, exactly as it would be inside a def. It
        // was not, and the same two lines counted to 300 in a function and to 44 at module
        // level -- the MicroPython and CircuitPython spelling.
        var moduleAsMain = new FunctionDef("main", new List<Param>(), "",
            new Block { Statements = { } });
        moduleAsMain.Body.Statements.AddRange(ast.GlobalStatements);
        // Methods count as functions for `global`: a method that widens a global under a
        // declaration is the same evidence the walk reads in a plain def (#205).
        var fromFunctions = CollectGlobalWidthsFromFunctions(
            ast.Functions.Concat(TypeInference.ClassMethods(ast)).Append(moduleAsMain));

        // A written annotation is the user's choice of storage width and outranks anything
        // inferred here: `presses: uint8 = 0` fed by `presses = presses + 1` in an ISR stays a
        // byte (and in GPIOR), whatever the promoted width of the sum says.
        var annotated = new HashSet<string>();
        foreach (var st in ast.GlobalStatements)
            switch (st)
            {
                case AnnAssign an: annotated.Add(an.Target); break;
                case VarDecl vd when !string.IsNullOrEmpty(vd.VarType): annotated.Add(vd.Name); break;
            }

        foreach (var name in fromFunctions.Keys.Concat(initializerWidths.Keys).Distinct())
        {
            if (annotated.Contains(name)) continue;
            string key = currentModulePrefix + name;
            if (!mutableGlobals.TryGetValue(key, out var have)) continue;

            var want = have;
            if (fromFunctions.TryGetValue(name, out var fnType) && fnType.SizeOf() > want.SizeOf())
                want = fnType;
            if (initializerWidths.TryGetValue(name, out var initType) && initType.SizeOf() > want.SizeOf())
                want = initType;

            // A written annotation is the user's choice and outranks anything inferred here;
            // those names never reach mutableGlobals unannotated, so only widen what is wider.
            if (want.SizeOf() > have.SizeOf())
            {
                mutableGlobals[key] = want;
                widenableGlobals.Remove(key);
            }
        }

        // Every store into an unannotated global is checked against the width chosen above,
        // and one an earlier run found too narrow starts at the width it needs.
        foreach (var st in ast.GlobalStatements)
        {
            if (st is not AssignStmt { Target: VariableExpr gv } || annotated.Contains(gv.Name)) continue;
            string key = currentModulePrefix + gv.Name;
            if (mutableGlobals.TryGetValue(key, out var gt) && WidthSeeds.IsInt(gt))
            {
                var seeded = InferredSlot(key, gt);
                if (seeded != gt)
                {
                    mutableGlobals[key] = seeded;
                    widenableGlobals.Remove(key);
                }
            }
        }
    }

    /// <summary>
    /// Give every field of a MODULE-LEVEL instance real storage when any function touches it.
    ///
    /// Functions are lowered in an order the program does not control, so a read in one
    /// function could be folded against the constructor's value while the write that changes
    /// it lives in a function lowered later, or -- worse -- read a name nobody ever wrote
    /// because the construction folded to a constant and emitted no store. `obj = Box(5)` with
    /// `def peek(): return obj.n` answered 0, and `obj.n = 77` in another function was lost.
    /// Touching the field in the SAME function that constructs always worked, which is what
    /// made both look like interrupt problems when they were found.
    ///
    /// Reached through a METHOD too: `obj.mark()` writes the field through the outline
    /// write-back convention, and no `obj.n = ...` appears anywhere in the source.
    ///
    /// Only an instance named at module level is affected. A Pin's `_bit` is read as
    /// `self._bit` inside its own methods, never as `led._bit`, so it stays compile-time.
    /// </summary>
    /// <summary>
    /// Register the ARRAY fields of every instance this module builds at its top level, so the
    /// stack overlay never reuses their SRAM.
    ///
    /// Module-level arrays are registered for exactly this reason -- Core.cs says it on the
    /// line: "so the overlay algorithm never aliases them with function-local arrays across
    /// sibling calls" -- and an array reached as `obj.buf` never got there. It has a name and a
    /// size only because the allocator infers both from the ArrayStore/ArrayLoad it sees, and it
    /// infers them inside whichever function did the store, so two such arrays written from two
    /// SIBLING calls were handed one slot. `lo.store(...)` in one helper and `hi.store(...)` in
    /// another put `lo_buf` and `hi_buf` at the same address; every read of one answered with
    /// the other's value, and nothing was refused and nothing was warned (#275). Reached
    /// directly from main it was correct, which is what kept it hidden.
    ///
    /// Read off `__init__` rather than off classFieldLayout, because the layout does not carry
    /// array fields at all: measured, not assumed. For `self.buf: uint8[2] = [0, 0]` the layout
    /// comes back EMPTY, which is also why the mutation analysis in MarkModuleInstanceFields
    /// concludes the method writes no field and marks nothing. Nothing downstream of the layout
    /// can see this shape, so the registration has to read the declaration.
    ///
    /// Both spellings of "an instance the module owns": a module-level `lo = Cell()` and a
    /// class attribute `class Reg: lo = Cell()`. The second is constructed by the module init
    /// #270 synthesizes, under the flattened name `Reg_lo`, and its buffer aliased the same way.
    ///
    /// Unconditional, and deliberately NOT gated on a function touching the field: such an
    /// array lives as long as the module does, exactly like a module-level one, so it belongs
    /// in the global section however it is used. A FUNCTION-LOCAL instance is untouched -- only
    /// what this module builds at its top level is collected here -- so the overlay keeps
    /// reusing the storage it exists to reuse.
    /// </summary>
    private void RegisterInstanceFieldArrays(ProgramNode ast)
    {
        var built = new List<(string Instance, string Cls)>();

        foreach (var st in ast.GlobalStatements)
            if (st is AssignStmt { Target: VariableExpr tv, Value: CallExpr { Callee: VariableExpr cv } })
                built.Add((tv.Name, ResolveCallee(cv.Name)));

        // `class Reg: lo = Cell()`. #270 turned these into real constructions in the module's
        // init; the name they are constructed under is the flattened one, which is also the
        // name their fields are read and written by.
        if (classAttrInits.TryGetValue(ast, out var attrInits))
            foreach (var st in attrInits)
                if (st is AssignStmt { Target: VariableExpr av, Value: CallExpr { Callee: VariableExpr acv } })
                    built.Add((av.Name, ResolveCallee(acv.Name)));

        foreach (var (instance, cls) in built)
        {
            FunctionDef? init = null;
            if (!instanceMethodDefs.TryGetValue(cls + "___init__", out init)
                && !methodAstByName.TryGetValue(cls + "___init__", out init)) continue;
            if (init?.Body is not Block initBody) continue;

            foreach (var s in TypeInference.WalkStatements(initBody.Statements))
            {
                string? field = null;
                string? ftype = null;
                switch (s)
                {
                    case AnnAssign aa when aa.Target.StartsWith("self.", StringComparison.Ordinal):
                        field = aa.Target.Substring("self.".Length); ftype = aa.Annotation; break;
                    case AssignStmt { Target: MemberAccessExpr { Object: VariableExpr { Name: "self" } } sm } sa:
                        field = sm.Member; ftype = sa.AnnotatedType; break;
                    case VarDecl vd when vd.Name.StartsWith("self.", StringComparison.Ordinal):
                        field = vd.Name.Substring("self.".Length); ftype = vd.VarType; break;
                }
                if (field == null || !IsFixedArrayParamType(ftype)) continue;

                // Under this module's prefix only. Registering the bare name as well would give
                // the array a second home and spend the SRAM twice, which is the reason Mark()
                // registers one key too.
                string key = currentModulePrefix + instance + "_" + field;
                int lb = ftype!.IndexOf('[');
                arraySizes[key] = int.Parse(ftype.Substring(lb + 1, ftype.Length - lb - 2));
                bufferLogicalLen[key] = arraySizes[key];
                arrayElemTypes[key] = DataTypeExtensions.StringToDataType(ftype.Substring(0, lb));
                moduleSramArrays.Add(key);
            }
        }
    }

    private void MarkModuleInstanceFields(ProgramNode ast)
    {
        // The instances this module builds at its top level. Collected from the AST being
        // marked, not only from the entry file: an instance built at the top level of an
        // IMPORTED module needs the same storage, and without this its fields were marked for
        // nobody, so the reader loaded a name that was never a global.
        foreach (var st0 in ast.GlobalStatements)
            if (st0 is AssignStmt { Target: VariableExpr tv0, Value: CallExpr })
                topLevelInstanceTargets.Add(tv0.Name);

        if (topLevelInstanceTargets.Count == 0) return;

        // The class each module-level instance is built from, read off the source. The
        // instance-to-class map is only filled when the construction is LOWERED, which
        // happens inside main and therefore after this pass.
        var ctorClass = new Dictionary<string, string>();
        // The arguments each instance was constructed with, so a field that holds another
        // instance can be followed back to the instance it holds (see MarkNestedWrites).
        var ctorArgs = new Dictionary<string, List<Expression>>();
        foreach (var st in ast.GlobalStatements)
            if (st is AssignStmt { Target: VariableExpr tv, Value: CallExpr { Callee: VariableExpr cv } cc })
            {
                ctorClass[tv.Name] = ResolveCallee(cv.Name);
                ctorArgs[tv.Name] = cc.Args;
            }

        void Mark(string instance, string field)
        {
            foreach (var key in new[] { currentModulePrefix + instance + "_" + field,
                                        instance + "_" + field })
            {
                killedConstants.Add(key);
                moduleInstanceMutableFields.Add(key);
            }

            // Give it storage here rather than at the store: the field is written from more
            // than one path (the constructor, a plain assignment, the outline write-back) and
            // only one of them was registering it, so a field that is only ever READ never
            // became a global at all and its reader loaded a register nobody had filled.
            if (!ctorClass.TryGetValue(instance, out var cls)) return;
            if (!classFieldLayout.TryGetValue(cls, out var layout)) return;
            foreach (var (f, ftype, _) in layout)
                if (f == field)
                {
                    // Under the module's own prefix: a field of an instance in an imported
                    // module is read as `counter_c_v`. Only the bare key used to be registered,
                    // which is right for the entry module and names nothing for any other.
                    // Registering both would give the field two homes and waste the SRAM.
                    var dt = DataTypeExtensions.StringToDataType(ftype);
                    mutableGlobals[currentModulePrefix + instance + "_" + field] = dt;
                    // RFC 0013 phase 0: this object is static duration (a field of a
                    // module-level instance) regardless of the mutableGlobals promotion
                    // above landing on the write-site test too -- see Assign.cs's
                    // unconditional record for the reason it needs recording independently.
                    staticFieldTypes[currentModulePrefix + instance + "_" + field] = dt;
                }
        }

        void MarkEveryField(string instance)
        {
            if (!ctorClass.TryGetValue(instance, out var cls)) return;
            if (!classFieldLayout.TryGetValue(cls, out var layout)) return;
            foreach (var (field, _, _) in layout) Mark(instance, field);
        }

        // `obj.go()` where go() writes through a field that HOLDS another instance.
        //
        // The write is not lost for want of a name: the outline write-back already targets the
        // held instance's own flattened field (`inner0_v`). What is missing is that nobody ever
        // marks THAT field, because the only mention of the held instance in the whole program
        // is `obj.inner`, and the walk below only ever sees `obj`. Unmarked, the field is not a
        // mutable global, so the constructor never materializes its store and every reader keeps
        // folding the value the constructor was given -- #183, compiling clean and answering
        // with the constructor's number forever.
        //
        // So the marking has to follow the field back to the instance it holds. `Outer(inner0)`
        // plus the layout's SourceParam says `self.inner` IS `inner0`, and from there this is
        // the ordinary one-level case that already works.
        //
        // Only the WRITTEN paths, never every nested field: a driver holding a Pin and calling
        // `self.pin.high()` must keep `_bit` a compile-time constant, which the backend needs to
        // build the mask and is not an optimization to trade away.
        void MarkNestedWrites(string instance, string cls, string callee)
        {
            if (!classFieldLayout.TryGetValue(cls, out _)) return;
            foreach (var (path, leafType) in NestedFieldsWrittenBy(callee))
                MarkResolved(instance, path, leafType);
        }

        // `path` reaches the written leaf through one or more instance-holding fields:
        // `bag_total` under `r` is `r.bag` -> `bag`, then `total`. Hop each holding field
        // the way HeldInstanceName does, so the mark lands on the name the store actually
        // flattens to (`bag_total`) and not on a prefix of it (`r_bag_total`). A hop whose
        // held instance is built inside the constructor argument has no name yet, so its
        // leaf defers to anonCtorMutableLeaves for the lowering to claim, as before; a hop
        // that resolves nowhere ends the walk and the flat mark stands -- the old answer,
        // which names nothing emitted but marks nothing incorrectly either.
        void MarkResolved(string instance, string path, string leafType)
        {
            while (ctorClass.TryGetValue(instance, out var instCls)
                   && classFieldLayout.TryGetValue(instCls, out var instLay))
            {
                var (field, _, srcParam) = instLay.FirstOrDefault(x =>
                    path.StartsWith(x.Field + "_", StringComparison.Ordinal)
                    && classFieldLayout.ContainsKey(x.Type));
                if (field == null || !ctorArgs.TryGetValue(instance, out var args)) break;

                var leaf = path.Substring(field.Length + 1);
                if (HeldInstanceName(instCls, srcParam, args) is { } held)
                {
                    instance = held;
                    path = leaf;
                    continue;
                }

                // `Outer(Inner(0))`: the held instance is built in the argument and has no
                // name yet. Leave the leaf for the lowering to claim when it mints one.
                if (HeldCtorCall(instCls, srcParam, args) is { } anon)
                {
                    if (!anonCtorMutableLeaves.TryGetValue(anon, out var leaves))
                        anonCtorMutableLeaves[anon] = leaves = new List<(string, string)>();
                    if (!leaves.Any(l => l.Leaf == leaf)) leaves.Add((leaf, leafType));
                    return;
                }
                break;
            }
            Mark(instance, path);
        }

        // The constructor call passed for the field whose SourceParam is `srcParam`, when the
        // argument is a call rather than a name.
        CallExpr? HeldCtorCall(string cls, string srcParam, List<Expression> args)
            => ArgumentFor(cls, srcParam, args) as CallExpr;

        // The module-level instance passed to the constructor for the field whose SourceParam is
        // `srcParam`. Null when the field is not initialized from a bare parameter, or the
        // argument is not a plain name: `Outer(Inner(...))` builds a temp that has no name until
        // lowering, and there is nothing here to mark.
        string? HeldInstanceName(string cls, string srcParam, List<Expression> args)
            => ArgumentFor(cls, srcParam, args) is VariableExpr av ? av.Name : null;

        // The constructor argument that initializes the field whose SourceParam is `srcParam`.
        // Null when the field is not initialized from a bare parameter, or the parameter cannot
        // be matched to an argument.
        Expression? ArgumentFor(string cls, string srcParam, List<Expression> args)
        {
            if (string.IsNullOrEmpty(srcParam)) return null;

            FunctionDef? init = null;
            if (!instanceMethodDefs.TryGetValue(cls + "___init__", out init)
                && !methodAstByName.TryGetValue(cls + "___init__", out init)) return null;
            if (init == null) return null;

            foreach (var a in args)
                if (a is KeywordArgExpr kw && kw.Key == srcParam) return kw.Value;

            // Positional: the parameter list carries self, the call's argument list does not.
            int idx = init.Params.FindIndex(p => p.Name == srcParam);
            if (idx < 0) return null;
            if (init.Params.Count > 0 && init.Params[0].Name == "self") idx--;
            if (idx < 0 || idx >= args.Count) return null;

            return args[idx] is KeywordArgExpr ? null : args[idx];
        }

        void Expr(Expression? e)
        {
            switch (e)
            {
                case null: return;
                // `obj.m(...)`: the method may write any of obj's fields, and through the
                // write-back convention it does so without an assignment anywhere in sight --
                // so unless the method can be SEEN to write nothing, every field is marked.
                //
                // The narrow case is worth having because a HAL object is mostly readers:
                // `Pin.high()` is `self._port[self._bit] = 1` and writes no field, yet one call
                // to it used to make `_port`, `_ddr`, `_pin` and `_bit` mutable for the rest of
                // the program, so the bit became a run-time shift instead of a constant mask.
                //
                // Deliberately all-or-nothing rather than marking exactly the fields the method
                // writes: a method that writes ANY field still marks them all, which is what it
                // did before. Marking exactly is possible (FieldsWrittenBy already computes the
                // set) but its flattened nested paths are not class-layout field names, and
                // getting that wrong is #124 and #127 again -- a write discarded and the reader
                // folding the constructor's value, compiling clean.
                case CallExpr { Callee: MemberAccessExpr { Object: VariableExpr recv, Member: var method } } c
                    when topLevelInstanceTargets.Contains(recv.Name):
                    if (!(ctorClass.TryGetValue(recv.Name, out var recvCls)
                          && MethodWritesNoField(recvCls + "_" + method)))
                    {
                        MarkEveryField(recv.Name);
                        if (recvCls != null)
                            MarkNestedWrites(recv.Name, recvCls, recvCls + "_" + method);
                    }
                    foreach (var a in c.Args) Expr(a);
                    return;
                case MemberAccessExpr { Object: VariableExpr ov } ma
                    when topLevelInstanceTargets.Contains(ov.Name):
                    Mark(ov.Name, ma.Member);
                    return;
                case CallExpr c2: Expr(c2.Callee); foreach (var a in c2.Args) Expr(a); return;
                case MemberAccessExpr m: Expr(m.Object); return;
                case BinaryExpr b: Expr(b.Left); Expr(b.Right); return;
                case UnaryExpr u: Expr(u.Operand); return;
                case IndexExpr ix: Expr(ix.Target); Expr(ix.Index); return;
                case TernaryExpr t: Expr(t.Condition); Expr(t.TrueVal); Expr(t.FalseVal); return;
                case KeywordArgExpr kw: Expr(kw.Value); return;
                case TupleExpr tu: foreach (var el in tu.Elements) Expr(el); return;
                case ListExpr le: foreach (var el in le.Elements) Expr(el); return;
                case FStringExpr fs: foreach (var part in fs.Parts) Expr(part.Expr); return;
            }
        }

        void Walk(Statement st)
        {
            switch (st)
            {
                case AssignStmt a: Expr(a.Target); Expr(a.Value); return;
                case AugAssignStmt ag: Expr(ag.Target); Expr(ag.Value); return;
                case AnnAssign an: Expr(an.Value); return;
                case VarDecl vd: Expr(vd.Init); return;
                case ExprStmt es: Expr(es.Expr); return;
                case ReturnStmt r: Expr(r.Value); return;
                case IfStmt i:
                    Expr(i.Condition);
                    foreach (var (c, _) in i.ElifBranches) Expr(c);
                    return;
                case WhileStmt w: Expr(w.Condition); return;
                case ForStmt f: Expr(f.Iterable); return;
                // Coverage gained over the old recursion: the `with` context
                // expression and match patterns/guards were never walked.
                case WithStmt wi: Expr(wi.ContextExpr); return;
                case MatchStmt m:
                    Expr(m.Target);
                    foreach (var br in m.Branches)
                    {
                        Expr(br.Pattern);
                        if (br.Guard != null) Expr(br.Guard);
                    }
                    return;
                case RaiseStmt rs: Expr(rs.MessageExpr); return;
            }
        }

        foreach (var func in ast.Functions)
            foreach (var st in TypeInference.WalkStatements(func.Body)) Walk(st);

        // The module's own statements run as main, and were never walked: a method call or
        // a member write there mutated nothing the scan could see, so the field stayed the
        // constructor's constant and every later read folded it. Walk them with two rules
        // the function walk does not share:
        //
        //   - a member READ marks nothing. A main-scope read shares the scope that stored
        //     the field, so the fold is correct -- and marking every `pin._bit` the top
        //     level touches would trade the constant masks the backend needs for runtime
        //     shifts, exactly what the narrow call case above exists to avoid.
        //   - a member WRITE TARGET marks: `o.inner.v = x` resolves `o.inner` back to the
        //     instance it holds and marks the leaf under the held instance's own name,
        //     the same hop MarkNestedWrites makes for `o.m()`.
        //
        // A one-hop target (`o.v = x`) needs no mark: the write path's own constant
        // tracking kills the field's fold the first time a value lands that differs.
        void ExprModule(Expression? e)
        {
            switch (e)
            {
                case null: return;
                case CallExpr { Callee: MemberAccessExpr { Object: VariableExpr recv2,
                        Member: var method2 } } cm
                    when topLevelInstanceTargets.Contains(recv2.Name):
                    if (ctorClass.TryGetValue(recv2.Name, out var recvCls2)
                        && !MethodWritesNoField(recvCls2 + "_" + method2))
                        // Only the nested leaves need a mark: the method's own fields are
                        // written through the outline write-back or an inline expansion,
                        // both of which store the flattened name in this very frame, and
                        // a member access in another function marks itself off Expr. A
                        // direct mark here made init-only fields (Life's width/height,
                        // written once in __init__) into mutable globals: `x % self.width`
                        // stopped folding to `% 32` and the div/mod runtime linked in.
                        MarkNestedWrites(recv2.Name, recvCls2, recvCls2 + "_" + method2);
                    foreach (var a2 in cm.Args) ExprModule(a2);
                    return;
                case CallExpr c3: ExprModule(c3.Callee); foreach (var a3 in c3.Args) ExprModule(a3); return;
                case MemberAccessExpr m3: ExprModule(m3.Object); return;
                case BinaryExpr b3: ExprModule(b3.Left); ExprModule(b3.Right); return;
                case UnaryExpr u3: ExprModule(u3.Operand); return;
                case IndexExpr ix3: ExprModule(ix3.Target); ExprModule(ix3.Index); return;
                case TernaryExpr t3: ExprModule(t3.Condition); ExprModule(t3.TrueVal); ExprModule(t3.FalseVal); return;
                case KeywordArgExpr kw3: ExprModule(kw3.Value); return;
                case TupleExpr tu3: foreach (var el3 in tu3.Elements) ExprModule(el3); return;
                case ListExpr le3: foreach (var el3 in le3.Elements) ExprModule(el3); return;
                case SetExpr se3: foreach (var el3 in se3.Elements) ExprModule(el3); return;
                case DictExpr de3: foreach (var (k3, v3) in de3.Entries) { ExprModule(k3); ExprModule(v3); } return;
                case FStringExpr fs3: foreach (var part in fs3.Parts) ExprModule(part.Expr); return;
                case SliceExpr sl3: ExprModule(sl3.Start); ExprModule(sl3.Stop); ExprModule(sl3.Step); return;
                case StarArgExpr st3: ExprModule(st3.Value); return;
                case DoubleStarArgExpr ds3: ExprModule(ds3.Value); return;
                case WalrusExpr wz3: ExprModule(wz3.Value); return;
                case AwaitExpr aw3: ExprModule(aw3.Operand); return;
                case YieldExpr y3: ExprModule(y3.Value); return;
                case ListCompExpr lc3:
                    ExprModule(lc3.Element); ExprModule(lc3.Iterable); ExprModule(lc3.Iterable2);
                    ExprModule(lc3.Filter); return;
                case GeneratorExpr ge3:
                    ExprModule(ge3.Element); ExprModule(ge3.Iterable); ExprModule(ge3.Iterable2);
                    ExprModule(ge3.Filter); return;
            }
        }

        // `o.<f1>.<f2>... = v`: follow the object path from the root instance to the field
        // that holds the next, the same HeldInstanceName resolution MarkNestedWrites uses.
        // Any hop that cannot be resolved (the field holds no instance, or the held one is
        // built inside the argument) ends the walk; the write still emits, only unmarked.
        void MarkMemberTarget(Expression t)
        {
            switch (t)
            {
                case MemberAccessExpr { Object: MemberAccessExpr } deep:
                {
                    var hops = new List<string>();
                    Expression? cur = deep.Object;
                    while (cur is MemberAccessExpr hop) { hops.Insert(0, hop.Member); cur = hop.Object; }
                    if (cur is not VariableExpr root || !topLevelInstanceTargets.Contains(root.Name))
                        return;
                    string held = root.Name;
                    foreach (var hop2 in hops)
                    {
                        if (!ctorClass.TryGetValue(held, out var heldCls)
                            || !classFieldLayout.TryGetValue(heldCls, out var heldLay)
                            || heldLay.FirstOrDefault(f => f.Field == hop2).SourceParam is not { } src
                            || string.IsNullOrEmpty(src))
                            return;
                        var next = HeldInstanceName(heldCls, src,
                            ctorArgs.TryGetValue(held, out var hArgs) ? hArgs : []);
                        if (next == null)
                        {
                            // The held instance is built inside the argument and has no name
                            // yet; leave the leaf for the lowering to claim when it mints one
                            // (the same fallback MarkNestedWrites makes). The type is the
                            // leaf's own declared type inside the anonymous class.
                            if (HeldCtorCall(heldCls, src,
                                    ctorArgs.TryGetValue(held, out var hArgs2) ? hArgs2 : [])
                                is { Callee: VariableExpr anonCallee } anon2)
                            {
                                var leafType2 =
                                    classFieldLayout.TryGetValue(ResolveCallee(anonCallee.Name),
                                        out var anonLay)
                                        ? anonLay.FirstOrDefault(f => f.Field == deep.Member).Type
                                        : null;
                                if (leafType2 != null)
                                {
                                    if (!anonCtorMutableLeaves.TryGetValue(anon2, out var leaves2))
                                        anonCtorMutableLeaves[anon2] = leaves2 = new List<(string, string)>();
                                    if (!leaves2.Any(l => l.Leaf == deep.Member))
                                        leaves2.Add((deep.Member, leafType2));
                                }
                            }
                            return;
                        }
                        held = next;
                    }
                    Mark(held, deep.Member);
                    return;
                }
                case TupleExpr tt: foreach (var el4 in tt.Elements) MarkMemberTarget(el4); return;
                case IndexExpr ix4: ExprModule(ix4.Target); ExprModule(ix4.Index); return;
            }
        }

        void WalkModule(Statement st)
        {
            switch (st)
            {
                case AssignStmt a4: MarkMemberTarget(a4.Target); ExprModule(a4.Value); return;
                case AugAssignStmt ag4: MarkMemberTarget(ag4.Target); ExprModule(ag4.Value); return;
                case AnnAssign an4: ExprModule(an4.Value); return;
                case VarDecl vd4: ExprModule(vd4.Init); return;
                case TupleUnpackStmt tp4: ExprModule(tp4.Value); return;
                case AssertStmt as4: ExprModule(as4.Condition); return;
                case ExprStmt es4: ExprModule(es4.Expr); return;
                case ReturnStmt r4: ExprModule(r4.Value); return;
                case IfStmt i4:
                    ExprModule(i4.Condition);
                    foreach (var (c4, _) in i4.ElifBranches) ExprModule(c4);
                    return;
                case WhileStmt w4: ExprModule(w4.Condition); return;
                case ForStmt f4:
                    ExprModule(f4.RangeStart); ExprModule(f4.RangeStop); ExprModule(f4.RangeStep);
                    ExprModule(f4.Iterable); return;
                case WithStmt wi4: ExprModule(wi4.ContextExpr); return;
                case MatchStmt m4:
                    ExprModule(m4.Target);
                    foreach (var br in m4.Branches)
                    {
                        ExprModule(br.Pattern);
                        if (br.Guard != null) ExprModule(br.Guard);
                    }
                    return;
                case RaiseStmt rs4: ExprModule(rs4.MessageExpr); return;
            }
        }

        foreach (var st5 in ast.GlobalStatements)
            foreach (var inner5 in TypeInference.WalkStatements(st5)) WalkModule(inner5);
    }

    private void ScanFunctions(ProgramNode ast, ModuleScope? scope = null)
    {
        // Record where every function in this module came from, before any of them is
        // registered. Done here rather than at the registration sites because there are
        // sixteen of those across five files, and a method reaches them by a different route
        // from a free function; this is the one place that sees the whole module with
        // `currentSourcePath` still set to it.
        RecordSourcePaths(ast);

        foreach (var func in ast.Functions)
        {
            string fullName = currentModulePrefix + func.Name;

            // UNREACHABLE, and left standing rather than stamped or deleted. AsyncTransform
            // runs on the entry file and on every module before this scan (Core.Generate), and
            // it either rewrites a coroutine into a class and removes the FunctionDef, or
            // throws its own diagnostic. Probed with a module-level `async def` with and
            // without `import asyncio`, and as a method: none arrives here. Giving it a node
            // would claim a caret for a message no program can produce.
            if (func.IsAsync)
                throw UserError(
                    $"async def '{func.Name}': the coroutine-to-state-machine lowering is not " +
                    "implemented yet. The syntax parses (this is the foundation); the transform " +
                    "is the next step. For now write the future as a small class with a poll() " +
                    "method driven from a cooperative loop -- the zero-cost pattern async lowers to.");

            // @asm_pio / @rp2.asm_pio: the body is PIO assembly, not CPU code.
            // Assemble it now and register it; never lower it as a function.
            if (func.IsPioProgram)
            {
                try
                {
                    pioPrograms[fullName] = PyMCU.Frontend.Pio.PioAssembler.Assemble(func);
                    if (currentModulePrefix == "" || currentModulePrefix == null)
                        pioPrograms[func.Name] = pioPrograms[fullName];
                }
                catch (PyMCU.Frontend.Pio.PioAsmException ex)
                {
                    // The program as a whole, so the `def` that names it. The assembler's own
                    // message carries the offending instruction; what this frame adds is which
                    // program it was in, and that is what the caret should agree with.
                    throw UserError($"in PIO program '{func.Name}': {ex.Message}", func);
                }
                continue;
            }

            functionReturnTypes[fullName] = func.ReturnType;
            // `def make(n): return chr(n)`: what crosses the return IS a character, and the
            // byte it lowers to says nothing about that. Recorded beside the return TYPE
            // because a caller is often lowered before the callee's body is (#436).
            if (ReturnsOnlyChars(func)) charReturningFunctions.Add(fullName);
            if (ReturnsOnlyBools(func)) boolReturningFunctions.Add(fullName);
            if (ReturnsOnlyNone(func)) noneReturningFunctions.Add(fullName);
            // `return <seq>` in an outlined function: record the returned name so a
            // call site compiled before this body can still resolve the element
            // type -- a module-level `x = f()` precedes f's emission, when
            // funcListReturnElems is still empty.
            if (!func.IsInline && SeqNameReturnedBy(func) is { } seqName)
                funcReturnSeqExprs[fullName] = (seqName, currentModulePrefix ?? "");
            if (!func.IsInline && ReturnsALocalList(func))
                funcReturnLocalLists.Add(fullName);
            var @params = new List<string>();
            var paramTypes = new List<DataType>();
            foreach (var p in func.Params)
            {
                // A fixed-array parameter type (`uint8[4]`) is not a real subroutine ABI: it has
                // no scalar register form and was silently misread as a ZCA handler param, so the
                // body was never compiled and the call failed only at link time. Reject it with a
                // pointer to the supported idiom (an array argument is passed by reference as a
                // `bytearray`), instead of emitting a dangling `undefined reference`.
                if (IsFixedArrayParamType(p.Type))
                    throw UserError(
                        $"parameter '{p.Name}' of '{func.Name}' has a fixed-array type '{p.Type}'; " +
                        "pass an array to a function as a 'bytearray' (by reference), " +
                        $"e.g. `def {func.Name}({p.Name}: bytearray, ...)`", p);
                // An UNANNOTATED parameter that the body subscripts is a buffer, not a
                // register. Callers already pass an array by its base address, so the pointer
                // was arriving all along and only the callee's reading of `buf[i]` was wrong:
                // with nothing saying otherwise, the subscript fell through to the register
                // BIT path. A run-time index then failed as "Bit index must be constant for
                // reading" -- which names no buffer, no parameter, and an operation the
                // program does not contain -- while a CONSTANT index was worse, compiling
                // silently into a bit test of the buffer's address.
                //
                // Decided here rather than when the body is compiled so that a call site sees
                // the same answer whichever of the two the compiler reaches first.
                if (!func.IsInline && p.Type.Length == 0 && IsSubscriptedInBody(func.Body, p.Name))
                    bytearrayParams.Add(fullName + "." + p.Name);

                @params.Add(p.Name);
                paramTypes.Add(ParamStorageType(func, p.Type));
            }

            functionParams[fullName] = @params;
            functionParamTypes[fullName] = paramTypes;
            NoteStrParamSlots(fullName, func, func.Params);
            functionParamDeclared[fullName] = func.Params.Select(p => p.Type).ToList();
            functionParamDefaults[fullName] = func.Params.Select(p => p.DefaultValue).ToList();
            functionModulePrefix[fullName] = currentModulePrefix ?? "";
            if (func.Params.Any(p => p.IsKeywordOnly || p.IsPositionalOnly))
                shapedSignatures[fullName] = func;
            RecordSlotDef(fullName, func);

            if (scope != null)
            {
                scope.FunctionReturnTypes[func.Name] = func.ReturnType;
                scope.FunctionParams[func.Name] = @params;
            }

            if (func.IsExtern)
            {
                externFunctionMap[fullName] = func.ExternSymbol;
            }
            else if (func.IsInline)
            {
                RefuseExpansionOnlyUnderUsed(func, "it is marked @inline");
                RegisterInlineFunction(func, fullName, scope);
            }
            else
            {
                // If the first parameter has an unknown (class) type, this is a ZCA-parameterized
                // handler (e.g. def on_irq(pin: Pin)). Store for on-demand synthesis; do NOT
                // add to functionsToCompile because the body references ZCA fields that are
                // only known at the call site.
                //
                // A `list[T]` parameter used to be refused outright here ("list parameters are
                // not supported ... mark the function @inline"), on the claim that leaving it
                // alone dropped the function silently. It does not: `list[T]` has no width
                // `StringToDataType` recognises either, so `hasZcaParam` below already classes
                // it exactly like a class-typed parameter and registers it for call-site
                // expansion -- which resolves `buf[i]`/`buf.append()`/`len(buf)` through
                // whichever list the caller actually passed, the same way a ZCA field does.
                // Measured (adafruit_dht's array.array-typed parameters, PyMCU#433): a
                // `list[uint16]` parameter on a plain (non-@inline) method, one call site,
                // reads and sums the caller's list correctly once this refusal is out of the
                // way. `array.array` on a parameter needed no extra handling at all: it is
                // ALSO an unrecognised name, so it reaches the identical path -- the concrete
                // element width comes from the argument bound at the call site, not from the
                // parameter's own (typecode-less) annotation.
                //
                // A parameter annotated with a class type carries a ZCA instance, which has no
                // subroutine ABI: the fields live in the caller's frame, so the body only has
                // meaning expanded at the call site. That is what @inline already does for the
                // same shape, so register these the same way and let the call site expand them.
                // Without it the two positions failed differently and both badly: a class in the
                // first parameter was kept only for on-demand ISR synthesis and never emitted
                // (`undefined reference` from the linker), and a class in any other position was
                // lowered as an ordinary function whose field reads were never bound, so it
                // silently computed on whatever the RAM held.
                // RFC 0009 phase 2: a union parameter whose members are all tag-carrying
                // scalars (or None) is NOT a ZCA-instance param -- the payload plus tag
                // byte is its subroutine ABI. A union containing a class keeps the
                // expansion treatment below, resolved per call site as before.
                bool hasZcaFirstParam = func.Params.Count > 0
                    && IsZcaHandlerParamType(func.Params[0].Type)
                    && !ParamUnionMembersTaggable(func.Params[0]);
                // `*args` and `**kwargs` have no subroutine ABI either, and for the same
                // reason: what they stand for is only known at the call site, where the extra
                // arguments were written. A subroutine would have nothing to bind them to, so
                // the body only has meaning expanded where it is called (#368).
                bool hasVariadicParam = func.Params.Any(p => p.IsVarArg || p.IsKwArg);
                // Growing a buffer from an enclosing scope has the same property for the same
                // reason: how many bytes the call adds decides that buffer's size, a size is
                // fixed while compiling, and there is no allocator to grow one at run time. So
                // the body only has meaning expanded where the number of bytes is known (#362).
                //
                // Never the entry point, whatever it does. `main` is called by the runtime and
                // not from any call site this could expand at, so registering it for expansion
                // would leave it compiled nowhere and the program would do nothing at all.
                bool growsAnOuterBuffer = func.Name != "main" && FunctionGrowsAnOuterBuffer(func);
                bool hasZcaParam = func.Params.Any(p => IsZcaInstanceParamType(p.Type)
                        && !ParamUnionMembersTaggable(p))
                    || hasVariadicParam || growsAnOuterBuffer;

                // The first-position form is also the ISR handler shape (`def on_irq(pin: Pin)`),
                // which is synthesized separately from the AST when the handler is registered.
                if (hasZcaFirstParam)
                    zcaHandlerAstNodes[fullName] = (func, currentModulePrefix ?? "");

                if (hasZcaParam || hasZcaFirstParam)
                {
                    var zcaParam = func.Params.FirstOrDefault(p => IsZcaInstanceParamType(p.Type));
                    RefuseExpansionOnlyUnderUsed(func,
                        zcaParam != null
                            ? $"its parameter '{zcaParam.Name}: {zcaParam.Type}' is read as a "
                              + "class instance, whose fields live in the caller's frame"
                            : hasVariadicParam
                                ? "it takes '*args' or '**kwargs', which only the call site fills in"
                                : growsAnOuterBuffer
                                    ? "it grows a buffer from an enclosing scope, whose size only "
                                      + "the call site fixes"
                                    : $"its first parameter '{func.Params[0].Name}: "
                                      + $"{func.Params[0].Type}' is read as a class instance");
                }

                if (hasZcaParam)
                {
                    RegisterInlineFunction(func, fullName, scope);
                }
                else if (!hasZcaFirstParam)
                {
                    compiledAsSubroutine.Add(func);
                    functionsToCompile.Add(new FunctionEntry
                        { Prefix = currentModulePrefix, Func = func, SourceFile = currentSourceFile, SourcePath = currentSourcePath });
                }
                // An UNANNOTATED first parameter lands in neither: it is the decorator shape the
                // stdlib uses (`def inline(f): return f`), which has never been lowered and must
                // not start being lowered here.
            }
        }

        foreach (var stmt in ast.GlobalStatements)
        {
            if (stmt is ClassDef classDef)
            {
                bool isEnum = classDef.Bases.Contains("Enum") || classDef.Bases.Contains("IntEnum");
                if (isEnum) continue;

                bool isException = classDef.Bases.Any(b =>
                    b is "Exception" or "BaseException" || constantVariables.ContainsKey(b) && exceptionNames.Contains(b));
                if (isException)
                {
                    int exnCode = nextUserExceptionCode++;
                    constantVariables[classDef.Name] = exnCode;
                    // `except mod.Exc:` resolves the class through the qualified key a
                    // `mod.X` read mangles to. The bare key above stays: `except Exc:`
                    // inside the defining module, and `from mod import Exc`, bind it.
                    constantVariables[currentModulePrefix + classDef.Name] = exnCode;
                    exceptionNames.Add(classDef.Name);

                    // The OSError subtree is the one place dispatch follows inheritance
                    // (`except OSError` has to catch a raised TimeoutError or a
                    // `class F(OSError)`). The edge is recorded only while the chain
                    // stays inside that subtree: a class based on ValueError gets none,
                    // so `except ValueError` keeps the flat answer it always had.
                    foreach (var b in classDef.Bases)
                    {
                        int parentCode;
                        string mangledBase = b.Replace('.', '_');
                        if (!constantVariables.TryGetValue(mangledBase, out parentCode)
                            && !constantVariables.TryGetValue(currentModulePrefix + mangledBase, out parentCode)
                            && !constantVariables.TryGetValue(b, out parentCode))
                            continue;
                        // Every base already inside the subtree contributes an IS-A
                        // edge: `class F(A, B)` over two OSError descendants is caught
                        // by `except A`, `except B` and `except OSError` alike, so a
                        // later base can carry an edge the first does not.
                        if (parentCode == BuiltinExceptionNames.Codes["OSError"]
                            || exceptionParents.ContainsKey(parentCode))
                        {
                            if (!exceptionParents.TryGetValue(exnCode, out var edges))
                                exceptionParents[exnCode] = edges = new List<int>();
                            if (!edges.Contains(parentCode))
                                edges.Add(parentCode);
                        }
                    }
                    continue;
                }

                if (classDef.Body != null)
                {
                    // Multiple inheritance is not supported (the ZCA model assumes a single base
                    // for layout + dispatch). Reject it clearly instead of later failing with an
                    // opaque "undefined function 'C_foo'" when a second base's method is called.
                    var realBases = classDef.Bases
                        .Where(b => b is not ("Enum" or "IntEnum" or "object" or "ABC")
                                    && !IsProtocolBaseName(b)).ToList();
                    if (realBases.Count > 1)
                        throw UserError(
                            $"class '{classDef.Name}' uses multiple inheritance " +
                            $"({string.Join(", ", realBases)}), which PyMCU does not support; " +
                            "use composition (hold an instance as a field) or a single base class",
                            classDef);

                    classNames.Add(classDef.Name);
                    if (classDef.IsValue) valueClasses.Add(classDef.Name);
                    if (classDef.IsGenerator) generatorClasses.Add(classDef.Name);
                    var oldPrefix = currentModulePrefix;
                    var classPrefix = currentModulePrefix + classDef.Name + "_";
                    currentModulePrefix = classPrefix;

                    // Ensure an entry exists in classDirectMethods even for empty classes.
                    string classKey = classPrefix.Substring(0, classPrefix.Length - 1);
                    if (!classDirectMethods.ContainsKey(classKey))
                        classDirectMethods[classKey] = new HashSet<string>();
                    if (classDef.Bases.Any(IsProtocolBaseName))
                        protocolClasses.Add(classKey);

                    if (classDef.Body is Block block)
                    {
                        // RFC 0001: derive the field layout once per class. A class with a
                        // single primitive field is eligible to be returned by value from a
                        // non-@inline factory (Model B register-packed handle).
                        var clsLayout = DeriveFieldLayout(block, classKey);
                        // Fields holding a COMPILE-TIME table: a list of constructions, a
                        // comprehension of them, a dict or a set. The layout types such a
                        // field as a scalar, so the outline check saw nothing wrong and
                        // shared the body -- where `self` is a run-time parameter and the
                        // table is unreachable, reported as a method that cannot be
                        // dispatched. They are compile-time per instance, like a ZCA field,
                        // so a method that touches one is force-inlined for the same reason.
                        var ctFields = CompileTimeTableFields(block);
                        // A subclass with no __init__ of its own inherits the base's fields, so an
                        // OVERRIDDEN method can resolve them (`self.a` otherwise errors "not a
                        // member"). Inherit the layout ONLY when the base is a slot class: that is
                        // the case where the override would be outlined and needs the field layout.
                        // Plain virtual/@inline HAL classes (base NOT in slotClasses) keep an empty
                        // layout so their construction model is unchanged (inheriting it there
                        // wrongly promotes the subclass to a slot and crashes codegen).
                        if (clsLayout.Count == 0 && !InitCallsSuperInit(block, classDef.Bases))
                            foreach (var baseName in classDef.Bases)
                            {
                                if (baseName is "Enum" or "IntEnum") continue;
                                // Inherit the base's field layout so an inherited/overridden method
                                // can resolve `self.<field>`. Allow it for a real ZCA data class --
                                // a slot (>= 2 fields) OR a single-field data class (zcaFactoryClasses)
                                // -- but NOT for a virtual/@inline HAL class (neither), whose multi-
                                // field layout would wrongly promote the subclass to a slot (A66).
                                bool IsDataClass(string k) =>
                                    slotClasses.Contains(k) || zcaFactoryClasses.ContainsKey(k);
                                string bk = oldPrefix + baseName;
                                if (classFieldLayout.TryGetValue(bk, out var bl) && bl.Count > 0
                                    && IsDataClass(bk))
                                {
                                    clsLayout = bl;
                                    break;
                                }

                                if (classFieldLayout.TryGetValue(baseName, out var bl2) && bl2.Count > 0
                                    && IsDataClass(baseName))
                                {
                                    clsLayout = bl2;
                                    break;
                                }

                                // A base named through an explicit import (`from base_mod
                                // import Base`) is registered under its DEFINING module's
                                // prefix, neither of the two tried above -- the same gap
                                // ResolveBase() (a few hundred lines down, for method
                                // inheritance) has, here for FIELD layout. Without it a
                                // subclass across a module boundary kept an empty layout, so
                                // construction found no fields to fill and #391 read it as a
                                // class taking no arguments (#391 again: MCP3008(SPI, cs), a
                                // subclass of MCP3xxx in a different file, declares no
                                // __init__ of its own).
                                string bkImported = ResolveCallee(baseName);
                                if (bkImported != baseName
                                    && classFieldLayout.TryGetValue(bkImported, out var bl3) && bl3.Count > 0
                                    && IsDataClass(bkImported))
                                {
                                    clsLayout = bl3;
                                    break;
                                }
                            }

                        // A subclass __init__ that calls super().__init__() also owns the base's
                        // fields (the base ctor sets them on the same self). Merge the base layout
                        // AHEAD of the subclass's own fields (base ctor runs first), so an outlined
                        // method on the subclass receives every field and `self.<inherited>` resolves
                        // instead of erroring "not a member". Runs even when the subclass adds no own
                        // field (a super-only __init__, common at intermediate/leaf levels of a deep
                        // chain) -- merged becomes the full base layout, which keeps the chain
                        // propagating through any number of levels (L0->L1->...->Ln).
                        if (InitCallsSuperInit(block, classDef.Bases))
                            foreach (var baseName in classDef.Bases)
                            {
                                if (baseName is "Enum" or "IntEnum") continue;
                                List<(string Field, string Type, string SourceParam)>? baseLayout = null;
                                string baseLayoutKey = "";
                                if (classFieldLayout.TryGetValue(oldPrefix + baseName, out var blm) && blm.Count > 0)
                                    (baseLayout, baseLayoutKey) = (blm, oldPrefix + baseName);
                                else if (classFieldLayout.TryGetValue(baseName, out var blm2) && blm2.Count > 0)
                                    (baseLayout, baseLayoutKey) = (blm2, baseName);
                                else
                                {
                                    // Same cross-module gap as above: a base named through an
                                    // explicit import lives under its defining module's prefix.
                                    string blmImported = ResolveCallee(baseName);
                                    if (blmImported != baseName
                                        && classFieldLayout.TryGetValue(blmImported, out var blm3) && blm3.Count > 0)
                                        (baseLayout, baseLayoutKey) = (blm3, blmImported);
                                }
                                if (baseLayout == null) continue;
                                // A store through the subclass into an inherited unannotated
                                // field is checked against the base's slot, which is the one
                                // the subclass copies.
                                foreach (var bf in baseLayout)
                                    InheritFieldSlot(classKey, baseLayoutKey, bf.Field);

                                // A field the subclass ALSO writes itself is the same slot the
                                // base constructor fills (try: super().__init__() / except:
                                // self.v = 0 writes v on both paths). Keep ONE entry at the
                                // base's slot position, joined wide enough for both writes --
                                // the same join DeriveFieldLayout applies within one body.
                                // Skipping the base's entry left the field at the subclass
                                // write's width and truncated the base's wider value.
                                var merged = new List<(string Field, string Type, string SourceParam)>();
                                foreach (var bf in baseLayout)
                                {
                                    int ownIdx = clsLayout.FindIndex(f => f.Field == bf.Field);
                                    if (ownIdx < 0) { merged.Add(bf); continue; }
                                    string joined = bf.Type;
                                    ApplyInferredFieldType(ref joined, clsLayout[ownIdx].Type);
                                    merged.Add((bf.Field, joined, bf.SourceParam));
                                    clsLayout.RemoveAt(ownIdx);
                                }
                                merged.AddRange(clsLayout);
                                clsLayout = merged;
                                break;
                            }

                        classFieldLayout[classKey] = clsLayout;

                        // `__match_args__` names the fields a positional class pattern binds,
                        // in order. It parsed and was ignored before, so writing one changed
                        // nothing and positional patterns had no order to work from.
                        foreach (var cs in block.Statements)
                        {
                            Expression? maVal = cs switch
                            {
                                AssignStmt { Target: VariableExpr { Name: "__match_args__" } } ma => ma.Value,
                                AnnAssign { Target: "__match_args__" } maa => maa.Value,
                                VarDecl { Name: "__match_args__" } mad => mad.Init,
                                _ => null,
                            };
                            if (maVal == null) continue;

                            var argNames = new List<string>();
                            var elems = maVal switch
                            {
                                TupleExpr te => te.Elements,
                                ListExpr le => le.Elements,
                                _ => null,
                            };
                            if (elems == null) continue;
                            foreach (var el in elems)
                                if (el is StringLiteral sl && sl.Value.Length > 0)
                                    argNames.Add(sl.Value);
                            classMatchArgs[classKey] = argNames;
                            break;
                        }
                        // Record any field whose type is itself a class, so a member read can
                        // recover the nested class identity a single-field ZCA loses on collapse.
                        // (1) param-typed fields (`self.x = pin` where pin: SomeClass): the layout
                        //     already carries the class name as the field type.
                        foreach (var (fld, ty, _) in clsLayout)
                        {
                            if (string.IsNullOrEmpty(ty) || IsScalarTypeName(ty)) continue;
                            fieldClasses[classKey + "|" + fld] = ResolveCallee(ty);
                        }
                        // (2) constructor-assigned fields (`self.x = SomeClass(...)`): the layout
                        //     records these as a scalar (the class collapses), so recover the class
                        //     from the write's RHS. Resolved here in the defining module's scope.
                        //     Every statement of every method, not only `__init__`'s top level:
                        //     a driver chooses its implementation in a branch, and the field it
                        //     assigns there is the one a program tests. `adafruit_hcsr04` writes
                        //     `self._echo = PulseIn(echo_pin)` under `if _USE_PULSEIO:` and the
                        //     `DigitalInOut` form under the `else`, so at the top level there is
                        //     no assignment at all and the field had no recorded class (#385).
                        //     And `adafruit_framebuf` writes `self._font = BitmapFont(...)` in
                        //     `text`, not `__init__` -- a field first assigned outside the
                        //     constructor is still instance storage.
                        foreach (var s0 in block.Statements)
                            if (s0 is FunctionDef fdI && fdI.Name == "__init__")
                                RecordConstructedFieldClasses(fdI.Body, classKey);
                        // ...and the same for fields first bound in ANOTHER method -- a
                        // lifted coroutine local's `self.x = SomeClass(...)` lives in poll(),
                        // not __init__ (async-task-local-object). __init__ ran first, so the
                        // first-wins map keeps the constructor's answer wherever both write.
                        foreach (var s0 in block.Statements)
                            if (s0 is FunctionDef fdM && fdM.Name != "__init__")
                                RecordConstructedFieldClasses(fdM.Body, classKey);
                        // `self.f = SomeClass` (a class OBJECT, not `SomeClass()`) is the
                        // same family but a different record: every method, every arm,
                        // because the field's tag byte has to name each class it can hold.
                        foreach (var s0 in block.Statements)
                            if (s0 is FunctionDef fdC)
                                RecordClassObjectFieldBindings(fdC.Body, classKey);
                        if (InitCallsSuperInit(block, classDef.Bases)) classInitCallsSuper.Add(classKey);
                        // Note: slotClasses (>= 2 fields) is marked only when an @outline method
                        // is actually present (below), so plain @inline HAL classes with multiple
                        // fields keep their normal virtual-construction path. zcaFactoryClasses is
                        // safe to mark eagerly: it is only consulted in factory-return contexts,
                        // never in direct construction.
                        if (clsLayout.Count == 1)
                            zcaFactoryClasses[classKey] = clsLayout[0].Type;

                        // This class body's methods by name, so the field-mutation analysis can
                        // follow a self.<method>() call into the method it names.
                        var classMethods = new Dictionary<string, FunctionDef>();
                        foreach (var s1 in block.Statements)
                            if (s1 is FunctionDef mf) classMethods[mf.Name] = mf;

                        foreach (var inner in block.Statements)
                        {
                            if (inner is FunctionDef func)
                            {
                                // Register as directly-defined BEFORE the inheritance copy so
                                // ResolveMROMethod can distinguish "defined here" from "inherited".
                                classDirectMethods[classKey].Add(func.Name);

                                // A dunder PyMCU never calls compiles quietly and then fails at
                                // whichever use site the reader reaches first, in a different
                                // shape each time (print(v), str(v) and f"{v}" gave three
                                // different messages, none of them mentioning __str__). Say it
                                // where the method is written.
                                if (func.Name is "__str__" or "__repr__" or "__format__"
                                    or "__iter__" or "__next__"
                                    or "__ipow__" or "__abs__" or "__int__" or "__float__"
                                    or "__divmod__" or "__index__")
                                {
                                    string why = func.Name switch
                                    {
                                        "__iter__" or "__next__" =>
                                            "PyMCU does not run the iterator protocol; iterate the "
                                            + "underlying array, or give the class a method you call by name",
                                        "__str__" or "__repr__" or "__format__" =>
                                            "PyMCU has no run-time string formatting; print the fields "
                                            + "explicitly, or give the class a method you call by name",
                                        // `**` is expanded at compile time (the IR has no pow
                                        // operation, which is why the exponent must be constant),
                                        // so `a **= 2` is rewritten to `a = a ** 2` in the parser
                                        // and the in-place attempt is gone before dispatch runs.
                                        // __pow__ IS dispatched, so that is the spelling to name.
                                        "__ipow__" =>
                                            "'**=' is rewritten to 'a = a ** 2' because '**' is expanded "
                                            + "at compile time, so the in-place form is never tried; "
                                            + "define __pow__ instead, which is dispatched",
                                        // __index__ has no builtin of its own: it is what a
                                        // subscript would consult, and a subscript on an instance
                                        // is refused by name now (#171), so there is no call site
                                        // left to reach it.
                                        "__index__" =>
                                            "PyMCU resolves a subscript without consulting the class, "
                                            + "and an instance subscript is refused outright; index a "
                                            + "fixed array, or give the class a method you call by name",
                                        // These are builtin call sites rather than operators, and
                                        // the builtin lowers its argument numerically without
                                        // consulting the class.
                                        _ =>
                                            $"PyMCU lowers {func.Name.Trim('_')}() on a numeric value "
                                            + "and does not consult the class; give the class a method "
                                            + "you call by name instead",
                                    };

                                    PyMCU.Common.Diagnostic.Warning(line: func.Line, code: "method-never-called", text:
                                        $"line {func.Line}: '{classDef.Name}.{func.Name}' is "
                                        + $"defined but never called -- {why}.");
                                }

                                RefuseUnsupportedMethodDecorators(func, classDef.Name);
                                RefuseUnrunnableConstructionHooks(func, classDef.Name);

                                // A destructor is dead code on this target, and saying so at the
                                // definition site alone would report every layer class that
                                // mirrors an upstream __del__ to a program that never builds one
                                // (machine.Timer, for one, in every MicroPython-layer build). The
                                // site is filed here and reported at the end, for the classes the
                                // program actually constructs (#491).
                                if (func.Name == "__del__")
                                    destructorSites[classKey] = (classDef.Name, func.Line);

                                string fullName = currentModulePrefix + func.Name;
                                // A property setter shares the getter's fullName, and
                                // registering its `-> None` under that key overwrote the
                                // getter's `-> bool`, so `print(d.value)` reached the
                                // decimal writer where CPython spells True/False. The
                                // setter's own expansion key is the ___setter spelling.
                                functionReturnTypes[func.IsPropertySetter
                                    ? fullName + "___setter" : fullName] = func.ReturnType;
                                if (!func.IsPropertySetter && ReturnsOnlyNone(func))
                                    noneReturningFunctions.Add(fullName);
                                if (!func.IsPropertySetter && ReturnsOnlyBools(func))
                                    boolReturningFunctions.Add(fullName);
                                var @params = new List<string>();
                                var paramTypes = new List<DataType>();
                                foreach (var p in func.Params)
                                {
                                    // Same rule as top-level functions (above): an
                                    // unannotated parameter the body subscripts is a
                                    // buffer passed by reference, not a register.
                                    if (!func.IsInline && p.Type.Length == 0
                                        && IsSubscriptedInBody(func.Body, p.Name))
                                        bytearrayParams.Add(fullName + "." + p.Name);
                                    @params.Add(p.Name);
                                    paramTypes.Add(ParamStorageType(func, p.Type));
                                }

                                functionParams[fullName] = @params;
                                functionParamTypes[fullName] = paramTypes;
                                NoteStrParamSlots(fullName, func, func.Params);
                                functionParamDeclared[fullName] = func.Params.Select(p => p.Type).ToList();
                                RecordSlotDef(fullName, func);
                                // Methods need their defaults recorded too. Only top-level
                                // functions were, so an outlined method called with an argument
                                // omitted got nothing for that parameter and its body read zero
                                // instead of the declared default.
                                functionParamDefaults[fullName] =
                                    func.Params.Select(p => p.DefaultValue).ToList();

                                if (func.IsPropertyGetter)
                                {
                                    // Record the getter so a bare `obj.<prop>` read is desugared
                                    // into a getter call. @property forces IsInline, so the inline
                                    // registration in the branch below still runs.
                                    string getterClass = classPrefix.Substring(0, classPrefix.Length - 1);
                                    propertyGetters.Add(getterClass + "." + func.Name);
                                }

                                if (func.IsPropertySetter)
                                {
                                    string setterKey = fullName + "___setter";
                                    inlineFunctions[setterKey] = func;
                                    string className = classPrefix.Substring(0, classPrefix.Length - 1);
                                    propertySetters[className + "." + func.PropertyName] = setterKey;
                                }
                                else if (func.IsOutline && func.Name != "__init__")
                                {
                                    // RFC 0001: explicit @outline -- compile this method ONCE as a
                                    // shared subroutine (Model A field-params, or Model B SRAM slot
                                    // for >= 2 fields). After F4 this is redundant with the default
                                    // for outline-safe methods; kept as an explicit request.
                                    //
                                    // @outline on __init__ is not handled here at all: a constructor
                                    // establishes the instance and cannot be shared, and taking it out
                                    // of the ordinary path left `A(s)` unable to find the class, which
                                    // was then reported as the class having no __init__ -- on a file
                                    // whose next line defines one. It takes the undecorated path.
                                    //
                                    // The safety check applies here too. @outline asks for a shared
                                    // body wherever one is possible; it cannot make an unshareable
                                    // one shareable. A body that reaches THROUGH a field
                                    // (`self.inner.get()`) has no standalone form: outlining it
                                    // anyway mangled the call into `self_inner_get` and failed the
                                    // build over a method the program may never call.
                                    if (IsOutlineSafe(func, clsLayout, ctFields))
                                        RegisterOutlinedMethod(func, classKey, clsLayout, fullName, classMethods);
                                    else
                                        instanceMethodDefs[fullName] = func;
                                }
                                else if (func.IsInline)
                                {
                                    // Once a name is overloaded its bare key is vacated, so a later
                                    // same-named overload must register under its suffixed key — never
                                    // re-occupy the bare key (a TryAdd there would succeed and hide the
                                    // overload from suffix-based resolution; e.g. Pin's 3rd const[str]
                                    // __init__ landing on the bare key and never being found).
                                    if (overloadedFunctions.Contains(fullName))
                                    {
                                        inlineFunctions[fullName + "___" + BuildOverloadSuffix(func.Params)] = func;
                                    }
                                    else if (!inlineFunctions.TryAdd(fullName, func))
                                    {
                                        var existing = inlineFunctions[fullName];
                                        if (existing?.Params != null)
                                        {
                                            var existingSfx = BuildOverloadSuffix(existing.Params);
                                            inlineFunctions[fullName + "___" + existingSfx] = existing;
                                        }

                                        inlineFunctions.Remove(fullName);
                                        overloadedFunctions.Add(fullName);
                                        inlineFunctions[fullName + "___" + BuildOverloadSuffix(func.Params)] = func;
                                    }
                                }
                                else
                                {
                                    // Overloads are a property of @inline methods: the registration
                                    // that keeps them apart by parameter suffix lives in that branch
                                    // only. An undecorated method is outlined by default and has no
                                    // such registration, so a second definition of the same name used
                                    // to REPLACE the first without a word -- and every caller of the
                                    // shape that disappeared failed at its own call site, naming a
                                    // mangled symbol. Say it here, where both definitions are visible.
                                    if (classDirectMethods[classKey].Contains(func.Name)
                                        && !func.IsInline
                                        && (instanceMethodDefs.ContainsKey(fullName)
                                            || inlineFunctions.ContainsKey(fullName)
                                            || outlinedMethods.Contains(fullName)
                                            // A method with no `self` is compiled as an ordinary
                                            // function (#201) and lands in none of the three
                                            // above, so a second definition of one fell past this
                                            // check to the generic duplicate-symbol message, which
                                            // names the mangled `A_w` and advises giving the
                                            // overloads different parameter types -- advice that
                                            // does not work, since overloads need @inline. This
                                            // is the accurate sentence and it has to keep firing
                                            // for them.
                                            || classPlainFunctions.Contains(fullName)))
                                        throw UserError(
                                            $"class '{classDef.Name}' defines '{func.Name}' more than "
                                            + "once, and overloads are only supported on @inline "
                                            + "methods (an undecorated method is compiled once as a "
                                            + "shared subroutine, which one name cannot address twice)."
                                            + $" Mark every '{func.Name}' @inline to overload by "
                                            + "parameter types, or give them different names.",
                                            func);

                                    // A method with no `self` parameter is a plain function that
                                    // happens to be written in a class body, and `A.f(x)` is the only
                                    // way to call it. It has no receiver, so the field layout the
                                    // outline decision turns on says nothing about it: a class with no
                                    // fields has an empty layout, IsOutlineSafe refuses an empty one,
                                    // and the method went to expansion-only. The call site still built
                                    // the name `A_f` and emitted a call to it, so the two halves
                                    // disagreed inside one build and it surfaced at the linker (#201).
                                    //
                                    // Compiled as an ordinary function under the class prefix, which
                                    // is the name the call site already forms. @inline keeps working
                                    // as it did, by expansion, which is what fixtures/static-method
                                    // has been relying on.
                                    var defLayout = clsLayout;
                                    if (func.Params.Count == 0 || func.Params[0].Name != "self")
                                    {
                                        compiledAsSubroutine.Add(func);
                                        functionsToCompile.Add(new FunctionEntry
                                        {
                                            Prefix = currentModulePrefix, Func = func,
                                            SourceFile = currentSourceFile, SourcePath = currentSourcePath,
                                        });
                                        // Recorded so a SECOND definition of the same name is
                                        // caught by the duplicate check above, which reads the
                                        // registries a method can land in and did not know
                                        // about this one.
                                        classPlainFunctions.Add(fullName);
                                    }
                                    else if (func.Name == "__init__")
                                    {
                                        // A constructor establishes the instance and cannot be
                                        // shared. Explicit @outline on __init__ is already ignored
                                        // above. The undecorated path used to outline a large
                                        // __init__ anyway, so a class-typed parameter took its
                                        // annotation class (digitalio.DigitalInOut) instead of the
                                        // argument (adafruit_mcp230xx.DigitalInOut). pin.high()
                                        // then became HAL gpio with a runtime bit index.
                                        instanceMethodDefs[fullName] = func;
                                    }
                                    else if (IsOutlineSafe(func, defLayout, ctFields,
                                                 InstanceFieldsOf(classKey)))
                                    {
                                        // A single-field mutator that ALSO has explicit returns
                                        // cannot use write-back-via-return (one return slot can't
                                        // carry both a value and the field). Force-inline it so
                                        // self.field aliasing persists the mutation. Registered in
                                        // inlineFunctions ONLY (never functionsToCompile) so it is
                                        // expanded per call site, not compiled standalone (which
                                        // would treat self as numeric and fail).
                                        if (defLayout.Count == 1
                                            && MethodMutatesField(func, defLayout[0].Field, classMethods)
                                            && MethodHasReturnStmt(func))
                                        {
                                            inlineFunctions[fullName] = func;
                                            instanceMethodDefs[fullName] = func;
                                        }
                                        else
                                        {
                                            RegisterOutlinedMethod(func, classKey, defLayout, fullName, classMethods);
                                        }
                                    }
                                    else
                                    {
                                        // Not representable as a shared body (uses self.method(),
                                        // passes self, non-derivable field, or an unhandled construct):
                                        // force-inline is the only way to give it a runtime form, so
                                        // it is registered for expansion ONLY. Compiling it standalone
                                        // as well would bind `self` to nothing -- a body reading
                                        // `self._pin.value()` mangled the field to a call on
                                        // `self__pin`, and the whole program failed to build over a
                                        // method the program may never even call.
                                        instanceMethodDefs[fullName] = func;
                                    }
                                }

                                if (!func.IsPropertySetter)
                                {
                                    methodInstanceTypes[fullName] =
                                        currentModulePrefix.Substring(0, currentModulePrefix.Length - 1);
                                    // Keep every instance method's AST reachable by symbol so a
                                    // super().<method>() can inline-expand the base body even when
                                    // the base method is outlined (not in inlineFunctions).
                                    methodAstByName[fullName] = func;
                                    if (MethodCallsSelfMethod(func)) methodsWithSelfCall.Add(fullName);
                                }
                            }
                            else if (inner is ClassDef nestedClass)
                            {
                                // Nested class (class defined inside another class), e.g.
                                // CircuitPython's alarm.time.TimeAlarm. Register its methods
                                // under the nested prefix so it is ZCA-constructible like a
                                // top-level class (VisitCall finds <prefix>___init__).
                                ScanNestedClassMembers(nestedClass, classPrefix);
                            }
                        }
                    }

                    foreach (var baseName in classDef.Bases)
                    {
                        // #279: remember the base so it can be checked once EVERY module has
                        // been scanned. Checking it here would refuse a base defined later in
                        // the file, or in a module scanned after this one, so the check is
                        // deferred rather than done in place. ResolveBase() below cannot do it
                        // either: it falls back to `basePrefix` unconditionally, which is what
                        // let an undefined base through in the first place.
                        // The PATH as well, for the same reason the check is deferred at all
                        // (#347): every module's classes reach one sweep after the scan, and
                        // by then nothing says which file any of them was written in, so the
                        // refusal carried the class's line under the entry program's name --
                        // adafruit_ssd1306.py:63 reported as main.py:63.
                        pendingBaseChecks.Add((classDef, baseName, oldPrefix, currentSourcePath));

                        string basePrefix = oldPrefix + baseName + "_";

                        string ResolveBase()
                        {
                            if (!string.IsNullOrEmpty(oldPrefix))
                            {
                                foreach (var k in inlineFunctions.Keys)
                                {
                                    if (k.StartsWith(basePrefix)) return basePrefix;
                                }
                            }

                            string bare = baseName + "_";
                            foreach (var k in inlineFunctions.Keys)
                            {
                                if (k.StartsWith(bare)) return bare;
                            }

                            // A base named through an explicit import (`from base_mod import
                            // Base`) lives under its DEFINING module's prefix, not the
                            // subclass's own -- ResolveCallee already walks TryImportedAlias to
                            // answer exactly this question everywhere else a class name is
                            // resolved. Without it, a base reached this way fell back to
                            // `basePrefix` (the subclass's own module + the bare base name), a
                            // prefix nothing is registered under, so the copy loop below found
                            // no methods to inherit -- including __init__, so `MCP3008(spi,
                            // cs)`, whose class declares no __init__ of its own and inherits a
                            // real one from `MCP3xxx` in another file, was refused as taking no
                            // arguments (#391 synthesized a no-op it was never meant to get).
                            string imported = ResolveCallee(baseName);
                            if (imported != baseName)
                            {
                                string importedPrefix = imported + "_";
                                foreach (var k in inlineFunctions.Keys)
                                {
                                    if (k.StartsWith(importedPrefix)) return importedPrefix;
                                }
                            }

                            return basePrefix;
                        }

                        string resolvedBasePrefix = ResolveBase();
                        string childClassName = classPrefix.Substring(0, classPrefix.Length - 1);
                        classBasePrefixes[childClassName] = resolvedBasePrefix;

                        // Register child → parent edge in the class-children graph.
                        string parentKey = resolvedBasePrefix.EndsWith("_")
                            ? resolvedBasePrefix[..^1] : resolvedBasePrefix;
                        if (!classChildren.TryGetValue(parentKey, out var childSet))
                            classChildren[parentKey] = childSet = new HashSet<string>();
                        childSet.Add(childClassName);

                        var toInherit = new List<KeyValuePair<string, FunctionDef>>();
                        foreach (var kvp in inlineFunctions)
                        {
                            if (kvp.Key.StartsWith(resolvedBasePrefix))
                            {
                                string methodSuffix = kvp.Key.Substring(resolvedBasePrefix.Length);
                                string childKey = classPrefix + methodSuffix;
                                if (!inlineFunctions.ContainsKey(childKey))
                                {
                                    if (kvp.Value != null)
                                        toInherit.Add(new KeyValuePair<string, FunctionDef>(childKey, kvp.Value));
                                }
                            }
                        }

                        foreach (var (childKey, value) in toInherit)
                        {
                            inlineFunctions[childKey] = value;
                            string srcKey = resolvedBasePrefix + childKey[classPrefix.Length..];

                            if (functionParams.TryGetValue(srcKey, out var p)) functionParams[childKey] = p;
                            if (functionParamTypes.TryGetValue(srcKey, out var pt)) functionParamTypes[childKey] = pt;
                            if (functionReturnTypes.TryGetValue(srcKey, out var rt)) functionReturnTypes[childKey] = rt;

                            methodInstanceTypes[childKey] = classPrefix.Substring(0, classPrefix.Length - 1);
                        }
                    }

                    // #391: CPython synthesizes a trivial no-op constructor for a class that
                    // declares no __init__ of its own. Checked here, after every base's
                    // methods (including an inherited __init__) have been copied in above, so
                    // this only fires when NEITHER this class NOR any base in its chain gives
                    // it one -- a class that inherits a real __init__ keeps using it. A class
                    // reaching this point has no field layout: nothing could have assigned
                    // self.<field> anywhere in its chain without an __init__ to do it in, so
                    // the synthesized constructor is unconditionally a true no-op (no params
                    // beyond self, no body). Registered the same way a hand-written `__init__`
                    // that calls no super and sets no field would be: force-inlined, so
                    // construction expands it away at the call site instead of compiling it as
                    // a standalone subroutine that would treat `self` as numeric.
                    string implicitInit = classPrefix + "__init__";
                    if (!inlineFunctions.ContainsKey(implicitInit))
                    {
                        var selfParam = new Param("self", "");
                        var syntheticInit = new FunctionDef(
                            "__init__", new List<Param> { selfParam }, "void", new Block(), isInline: true);
                        inlineFunctions[implicitInit] = syntheticInit;
                        functionParams[implicitInit] = new List<string> { "self" };
                        functionParamTypes[implicitInit] =
                            new List<DataType> { DataTypeExtensions.StringToDataType("") };
                        functionParamDefaults[implicitInit] = new List<Expression?> { null };
                        functionReturnTypes[implicitInit] = "void";
                        methodInstanceTypes[implicitInit] = classKey;
                        methodAstByName[implicitInit] = syntheticInit;
                        classDirectMethods[classKey].Add("__init__");
                    }

                    currentModulePrefix = oldPrefix;
                }
            }
        }
    }

    private static bool IsProtocolBaseName(string b) =>
        b == "Protocol" || b.EndsWith(".Protocol", StringComparison.Ordinal);

    /// Every `class C(Base)` seen, with the module prefix it was seen under, checked once all
    /// modules have been scanned. See CheckBaseClassNames.
    private readonly List<(ClassDef Def, string BaseName, string Prefix, string Path)> pendingBaseChecks = new();

    /// <summary>
    /// Reject a base class that names nothing this compiler knows (#279).
    ///
    /// An undefined base used to be accepted in silence, and stayed silent until something
    /// inherited was USED -- at which point every message blamed somewhere else. A one-character
    /// typo in a base name produced this, and no message in the sequence contained the typo:
    ///
    ///     class Foo(Basse): ...        accepted, nothing said
    ///     Foo()                        "class 'Foo' cannot be constructed: it has no __init__
    ///                                   method ... add `def __init__(self): ...`"
    ///     (follow that advice)         "'f' is an integer: 'greet()' is not available"
    ///
    /// The second message is false of the program -- `Foo` DOES inherit an `__init__` -- and
    /// following it adds a constructor that shadows the inherited one, so the reader ends up
    /// further from the fix than they started. Naming the base here makes both unreachable for
    /// this cause.
    ///
    /// Deferred until every module is scanned, because a base may be defined after its subclass
    /// or in another module. The known-name test is CheckAnnotationNames' one: that path already
    /// resolves a class name through aliases, module prefixes and dotted spellings, and an
    /// annotation naming an unknown type has been refused for exactly the same reason since the
    /// `unit8` truncation. This is the check that existed on the other path.
    /// </summary>
    private void CheckBaseClassNames()
    {
        foreach (var (def, baseName, prefix, definedIn) in pendingBaseChecks)
        {
            if (string.IsNullOrEmpty(baseName)) continue;
            // The file the class is written in, so the refusal below names it rather than the
            // entry program (#347).
            currentSourcePath = definedIn;
            currentSourceFile = definedIn.Length > 0 ? SourceFileLabel(definedIn) : "";
            // Bases the language gives meaning to rather than the program. Enum/IntEnum and the
            // exception bases are handled before the scan reaches here, but a class carrying one
            // alongside a real base still records it, so they are named again rather than relied on.
            if (baseName is "object" or "Enum" or "IntEnum" or "Exception" or "BaseException"
                or "Protocol" or "ABC") continue;
            if (exceptionNames.Contains(baseName)) continue;

            if (classNames.Contains(baseName) || classFieldLayout.ContainsKey(baseName)) continue;
            if (classNames.Contains(prefix + baseName)) continue;
            if (IsImportedAlias(baseName) || aliasToOriginal.ContainsKey(baseName)) continue;
            if (classNames.Any(c => c.EndsWith("." + baseName, StringComparison.Ordinal)
                                    || c.EndsWith("_" + baseName, StringComparison.Ordinal))) continue;
            if (ResolveCallee(baseName) is { } resolved
                && (classNames.Contains(resolved) || classFieldLayout.ContainsKey(resolved))) continue;
            // A dotted base (`mod.Base`) whose tail names a class the compiler holds. The
            // capability question -- whether a dotted base SHOULD resolve -- is open and separate;
            // this only avoids inventing a refusal for a name that does exist.
            int dot = baseName.LastIndexOf('.');
            if (dot > 0)
            {
                string tail = baseName[(dot + 1)..];
                if (classNames.Contains(tail)
                    || classNames.Any(c => c.EndsWith("." + tail, StringComparison.Ordinal)
                                           || c.EndsWith("_" + tail, StringComparison.Ordinal)))
                    continue;
            }

            string? near = classNames
                .Select(c => c.Contains('.') ? c[(c.LastIndexOf('.') + 1)..] : c)
                .Where(n => EditDistance(n, baseName) <= 2)
                .OrderBy(n => EditDistance(n, baseName))
                .FirstOrDefault();
            throw UserError(
                $"class '{def.Name}' has a base class '{baseName}' that is not defined"
                + (near != null ? $" (did you mean '{near}'?)" : "")
                + ". An undefined base used to be accepted in silence, and the class then "
                + "behaved as though it had no base at all -- so the next error named the "
                + "constructor or the field, never the base. Define it, import it, or remove "
                + "it from the class header.",
                def);
        }
    }

    /// <summary>
    /// PyMCU#373. An outlined method (RFC 0001 Model A/B: compiled ONCE as a shared body,
    /// `self` bound to the DECLARING class, not any particular instance) that calls a sibling
    /// via `self.&lt;method&gt;()` can only forward that call statically, to whatever
    /// <see cref="ResolveMROMethod"/> resolves from the DECLARING class -- see
    /// <see cref="TryEmitSelfOutlinedMethodCall"/>. That forwarding is sound only when the
    /// target is ALSO a shared body (itself outlined): forwarding to one that is not means the
    /// generic sibling-dispatch path is reached instead, which has no instance to resolve
    /// `self` against and reported the receiver as a numeric value it is not.
    ///
    /// Neither half of that condition -- whether the sibling ends up outline-safe, whether it
    /// is overridden by a subclass discovered later in the file or in another module -- is
    /// known while the containing method is itself being scanned; both are settled only once
    /// scanning is complete. So this runs once, after <see cref="CheckBaseClassNames"/>, over
    /// the STABLE final registry: every outlined method whose self-call cannot be forwarded
    /// this way is demoted to force-inline (the same as an ordinary unsafe-to-outline method),
    /// which resolves the receiver from the concrete instance at each call site instead and is
    /// what a plain, undecorated `self._reset()` written directly in `__init__` already does
    /// correctly. A fixed-point loop, because demoting one method can turn a sibling that
    /// forwarded to IT from safe to unsafe in turn.
    /// </summary>
    private void DemoteUnsafeOutlinedSelfCalls()
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var fullName in outlinedMethods.ToList())
            {
                if (!methodAstByName.TryGetValue(fullName, out var func) || func == null) continue;
                if (!methodInstanceTypes.TryGetValue(fullName, out var classKey)) continue;

                var selfCallTargets = new List<string>();
                CollectSelfCallMembers(func.Body, selfCallTargets);
                if (selfCallTargets.Count == 0) continue;

                bool unsafeSibling = selfCallTargets.Any(member =>
                {
                    string target = ResolveMROMethod(classKey, member) + "_" + member;
                    return !outlinedMethods.Contains(target) || IsVirtualDispatch(classKey, member);
                });
                if (!unsafeSibling) continue;

                outlinedMethods.Remove(fullName);
                slotMethods.Remove(fullName);
                instanceMethodDefs[fullName] = func;
                // The synthesized shared body has no other caller once this is demoted (a
                // demoted method is never again used as a TryEmitSelfOutlinedMethodCall
                // forwarding target), so compiling it would only risk the very same error on
                // dead code -- Base's own `_write_register_byte() -> raise NotImplementedError()`
                // is never called at run time, and must not be why the build fails.
                functionsToCompile.RemoveAll(fe => (fe.Prefix ?? "") + fe.Func.Name == fullName);
                changed = true;
            }
        }
    }

    // Every `self.<name>(...)` call reachable inside an outline-safe body (so the grammar is
    // exactly the one IsOutlineSafe already restricted it to), collecting <name>. Used only to
    // decide whether the forwarding in TryEmitSelfOutlinedMethodCall will hold; conservative in
    // the collecting direction, not the restricting one -- an unrecognized node is simply not
    // descended into, since IsOutlineSafe already refused anything outside this grammar.
    private void CollectSelfCallMembers(Statement? s, List<string> into)
    {
        void E(Expression? e)
        {
            switch (e)
            {
                case CallExpr { Callee: MemberAccessExpr { Object: VariableExpr { Name: "self" } } m } call:
                    into.Add(m.Member);
                    foreach (var a in call.Args) E(a);
                    return;
                case MemberAccessExpr ma: E(ma.Object); return;
                case BinaryExpr b: E(b.Left); E(b.Right); return;
                case UnaryExpr u: E(u.Operand); return;
                case CallExpr c: E(c.Callee); foreach (var a in c.Args) E(a); return;
                case KeywordArgExpr kw: E(kw.Value); return;
                case IndexExpr ix: E(ix.Target); E(ix.Index); return;
                case TernaryExpr t: E(t.Condition); E(t.TrueVal); E(t.FalseVal); return;
                case TupleExpr tu: foreach (var el in tu.Elements) E(el); return;
                case ListExpr le: foreach (var el in le.Elements) E(el); return;
                default: return;
            }
        }

        foreach (var st in TypeInference.WalkStatements(s))
        {
            switch (st)
            {
                case VarDecl vd: E(vd.Init); break;
                case AnnAssign a: E(a.Value); break;
                case AssignStmt asg: E(asg.Target); E(asg.Value); break;
                case AugAssignStmt aug: E(aug.Target); E(aug.Value); break;
                case ReturnStmt r: E(r.Value); break;
                case ExprStmt ex: E(ex.Expr); break;
                case IfStmt iff:
                    E(iff.Condition);
                    foreach (var br in iff.ElifBranches) E(br.Condition);
                    break;
                case WhileStmt wh: E(wh.Condition); break;
                // The old recursion stopped at while: a self-call under for/try/with/match
                // in an outline-safe body was collected by nobody.
                case ForStmt fo: E(fo.Iterable); break;
                case WithStmt wi: E(wi.ContextExpr); break;
                case MatchStmt m2:
                    E(m2.Target);
                    foreach (var br in m2.Branches)
                        if (br.Guard != null) E(br.Guard);
                    break;
            }
        }
    }

    // RFC 0001 Model A: derives the ordered runtime-field layout of a ZCA class
    // from its __init__ body. Each `self.<field> = <expr>` becomes a (field, type)
    // entry; the type is taken from the matching __init__ parameter when the RHS is
    // that parameter, else defaults to uint8. Used to synthesize the leading params
    // of an @outline method.
    // Recursively walk a statement (into class bodies, methods and nested blocks) and record
    // every member name used as an assignment target. Defensive about statement types so it
    // never UNDER-collects (a missed write would risk a false "no attribute" error).
    private void CollectAssignedMemberNames(Statement? s, string? owner = null)
    {
        foreach (var st in TypeInference.WalkStatements(s))
        {
            switch (st)
            {
                // Entering a class body names the owner for everything beneath it, including
                // the methods -- `self.x = ...` in a method is as much a field of THIS class
                // as one in __init__. The shared walk does not descend into defs, so the
                // same scopes the old recursion entered are entered here. A nested class
                // re-owns its own body, so the inner name wins.
                case ClassDef cd: CollectAssignedMemberNames(cd.Body, currentModulePrefix + cd.Name); break;
                case FunctionDef fd: CollectAssignedMemberNames(fd.Body, owner); break;
                case ForStmt f:
                    // `for self.x in ...` (a member loop target) also writes the member.
                    if (f.VarName.Contains('.')) NoteAssignedMember(f.VarName[(f.VarName.LastIndexOf('.') + 1)..], owner);
                    break;
                case AssignStmt a: RecordMemberAssignTarget(a.Target, owner); break;
                case AugAssignStmt ag: RecordMemberAssignTarget(ag.Target, owner); break;
                // `self.a, self.b = ...` writes the members too -- its targets are the dotted
                // strings TryUnpackIntoAttributes walks, not AssignStmt targets, so without
                // this they were never counted as fields at all.
                case TupleUnpackStmt tu:
                    foreach (var t in tu.Targets)
                    {
                        int td = t.LastIndexOf('.');
                        if (td >= 0) NoteAssignedMember(t[(td + 1)..], owner);
                    }
                    break;
                case AnnAssign an:
                    // AnnAssign.Target is a (possibly dotted) name string, e.g. "self._buf".
                    int dot = an.Target.LastIndexOf('.');
                    if (dot >= 0) NoteAssignedMember(an.Target[(dot + 1)..], owner);
                    break;
            }
        }
    }

    // Walk a statement and classify every plain-name binding as bool or not-bool (see the
    // boolNames/nonBoolNames comment in State.cs). A bool-shaped value (a literal, a
    // comparison, `not`, a truth builtin) binds a bool; everything else -- arithmetic, a
    // loop variable, a parameter -- vetoes the name. Module-level bindings veto program-wide (one flat namespace);
    // bindings inside a function veto only within that function's scope, since a callee's
    // `off` parameter is a different binding from the program's `off` local.
    private void CollectBoolNames(Statement? s, string? scope = null, string? cls = null)
    {
        foreach (var st in TypeInference.WalkStatements(s))
        {
            switch (st)
            {
                // The shared walk does not descend into defs/classes; these are walked
                // explicitly, the same scopes the old recursion entered.
                case ClassDef cd:
                    CollectBoolNames(cd.Body, scope, cd.Name);
                    break;
                case FunctionDef fd:
                {
                    // Methods scope under the `Class_method` key the inline tables and
                    // currentFunction already use; a plain function keys on its
                    // module-qualified name.
                    string fScope = cls != null
                        ? cls + "_" + fd.Name
                        : (currentModulePrefix ?? "") + fd.Name;
                    foreach (var p in fd.Params) NoteNonBool(fScope, p.Name);
                    CollectBoolNames(fd.Body, fScope);
                    break;
                }
                case ForStmt f:
                    NoteNonBool(scope, f.VarName);
                    if (!string.IsNullOrEmpty(f.Var2Name)) NoteNonBool(scope, f.Var2Name);
                    break;
                case AssignStmt { Target: VariableExpr av } a:
                    NoteBoolBinding(av.Name, a.Value, scope, a.AnnotatedType);
                    break;
                case AssignStmt { Target: TupleExpr tup }:
                    foreach (var e in tup.Elements)
                        if (e is VariableExpr tv) NoteNonBool(scope, tv.Name);
                    break;
                case AugAssignStmt { Target: VariableExpr gv }: NoteNonBool(scope, gv.Name); break;
                case VarDecl vd: NoteBoolBinding(vd.Name, vd.Init, scope, vd.VarType); break;
                case AnnAssign an when !an.Target.Contains('.'):
                    NoteBoolBinding(an.Target, an.Value, scope, an.Annotation);
                    break;
            }
        }
    }

    private void NoteBoolBinding(string name, Expression? value, string? scope,
                                 string? declaredType = null)
    {
        if (value is CallExpr { Callee: VariableExpr { Name: "chr" }, Args.Count: 1 })
        {
            NoteScoped(nonBoolScopes, nonBoolNames, scope, name);
            NoteScoped(charScopes, charNames, scope, name);
            return;
        }
        // An annotation is the binding's declared type in PyMCU's model:
        // `c: uint8 = 300 in d` stores the membership's answer as a byte and
        // prints 1/0, while the same `in` unannotated is CPython's bool and
        // prints True/False (#386). `bool` (or no annotation) leaves the call
        // to the value's own shape; anything else vetoes the mark.
        if (declaredType != null && declaredType != "bool")
        {
            NoteNonBool(scope, name);
            return;
        }
        // A name bound to a comparison holds a Python bool (`v = base > k; print(v)` is
        // False, not 0, in CPython): anything bool-shaped binds a bool (#386).
        if (value != null && IsBoolShaped(value)) NoteBool(scope, name);
        else NoteNonBool(scope, name);
    }

    private static void NoteScoped(Dictionary<string, HashSet<string>> scoped, HashSet<string> flat,
                                   string? scope, string name)
    {
        if (scope == null) flat.Add(name);
        else
        {
            if (!scoped.TryGetValue(scope, out var s)) scoped[scope] = s = new();
            s.Add(name);
        }
    }

    /// <summary>
    /// Takes back a bool mark CollectBoolNames filed for `v = a == b` before any class was
    /// scanned: a comparison whose left operand is an instance of a class that defines the
    /// operator's dunder returns whatever the dunder returns, not a Python bool
    /// (`Cell.__eq__` returning `self.n + other.n` makes `v` hold 7, and `print(v)` must
    /// spell 7, not True -- probe 282_cmp_dunder_in_value_position).
    ///
    /// Runs after every module's scan, when classModuleMap, classDirectMethods and
    /// boolReturningFunctions are all complete -- they are empty while CollectBoolNames
    /// walks. Re-walks the same statements under the same scope keys, tracking
    /// `name = Ctor(...)` bindings itself because instanceClasses only records module-level
    /// instances at scan time. Conservative in both directions: only a comparison whose
    /// left operand is POSITIVELY an instance of a class POSITIVELY defining the exact
    /// dunder (through the MRO) is demoted, and only when that dunder does not return
    /// exclusively bools. A class with no dunder keeps the bool: `==`/`!=` then fold to
    /// identity, and an ordering op is refused before it ever prints.
    /// </summary>
    private void DemoteDunderBoundComparisons(ProgramNode ast, string modPrefix)
    {
        var moduleCtors = new Dictionary<string, string>();
        foreach (var stmt in ast.GlobalStatements)
            DemoteDunderWalk(stmt, null, null, modPrefix, moduleCtors);
        foreach (var fn in ast.Functions)
            DemoteDunderWalk(fn, null, null, modPrefix, new Dictionary<string, string>(moduleCtors));
    }

    // The walk mirrors CollectBoolNames statement-for-statement: same walk, same scope
    // keys, same order, so the veto lands on the binding the earlier pass filed.
    private void DemoteDunderWalk(Statement? s, string? scope, string? cls, string modPrefix,
                                  Dictionary<string, string> ctorOf)
    {
        foreach (var st in TypeInference.WalkStatements(s))
        {
            switch (st)
            {
                case ClassDef cd:
                    DemoteDunderWalk(cd.Body, scope, cd.Name, modPrefix,
                                     new Dictionary<string, string>(ctorOf));
                    break;
                case FunctionDef fd:
                {
                    string fScope = cls != null
                        ? cls + "_" + fd.Name
                        : modPrefix + fd.Name;
                    var local = new Dictionary<string, string>(ctorOf);
                    // An annotated parameter arrives already an instance of the named class
                    // (`def go(a: Cell)`); an unannotated one carries nothing this can trust.
                    foreach (var p in fd.Params)
                    {
                        local.Remove(p.Name);
                        if (p.Type.Length > 0
                            && classModuleMap.TryGetValue(p.Type, out var pmod) && pmod != null)
                            local[p.Name] = pmod + p.Type;
                    }
                    DemoteDunderWalk(fd.Body, fScope, null, modPrefix, local);
                    break;
                }
                case AssignStmt { Target: VariableExpr av } a:
                    // The RHS reads the OLD binding of av; decide before updating it.
                    DemoteComparisonBinding(av.Name, a.Value, scope, modPrefix, ctorOf);
                    UpdateCtorBinding(ctorOf, av.Name, a.Value);
                    break;
                case VarDecl vd:
                    DemoteComparisonBinding(vd.Name, vd.Init, scope, modPrefix, ctorOf);
                    UpdateCtorBinding(ctorOf, vd.Name, vd.Init);
                    break;
                case AnnAssign an when !an.Target.Contains('.'):
                    DemoteComparisonBinding(an.Target, an.Value, scope, modPrefix, ctorOf);
                    UpdateCtorBinding(ctorOf, an.Target, an.Value);
                    break;
                case AssignStmt { Target: TupleExpr tup }:
                    foreach (var e in tup.Elements)
                        if (e is VariableExpr tv) ctorOf.Remove(tv.Name);
                    break;
                case AugAssignStmt { Target: VariableExpr gv }: ctorOf.Remove(gv.Name); break;
            }
        }
    }

    private void DemoteComparisonBinding(string name, Expression? value, string? scope,
                                         string modPrefix, Dictionary<string, string> ctorOf)
    {
        if (value is not BinaryExpr b || b.Left is not VariableExpr lv) return;
        string? dunder = b.Op switch
        {
            PyMCU.Frontend.BinaryOp.Equal => "__eq__",
            PyMCU.Frontend.BinaryOp.NotEqual => "__ne__",
            PyMCU.Frontend.BinaryOp.Less => "__lt__",
            PyMCU.Frontend.BinaryOp.LessEq => "__le__",
            PyMCU.Frontend.BinaryOp.Greater => "__gt__",
            PyMCU.Frontend.BinaryOp.GreaterEq => "__ge__",
            // `is`, `in`, `and`/`or`/`not` keep the bool they were filed under: identity and
            // membership answer True/False no matter what an operand is.
            _ => null,
        };
        if (dunder == null) return;

        string? cls = ctorOf.TryGetValue(lv.Name, out var known)
            ? known
            // A module-level instance bound in an earlier statement (or an imported module's)
            // was filed under its qualified key during ScanGlobals.
            : instanceClasses.TryGetValue(modPrefix + lv.Name, out var scanned) ? scanned : null;
        if (cls == null) return;

        string owner = ResolveMROMethod(cls, dunder);
        if (ClassDefinesMethod(owner, dunder)
            && !boolReturningFunctions.Contains(owner + "_" + dunder))
            NoteNonBool(scope, name);
    }

    private void UpdateCtorBinding(Dictionary<string, string> ctorOf, string name, Expression? value)
    {
        if (value is CallExpr cc && CtorClassOf(cc) is { } qc) ctorOf[name] = qc;
        else if (value is VariableExpr vv && ctorOf.TryGetValue(vv.Name, out var alias))
            ctorOf[name] = alias;
        else ctorOf.Remove(name);
    }

    // The qualified class key (`main_Cell`, `lib1_S`) a constructor call builds, or null
    // when the callee is not a class the scan can name.
    private string? CtorClassOf(CallExpr cc)
    {
        if (cc.Callee is VariableExpr cv
            && classModuleMap.TryGetValue(cv.Name, out var mod) && mod != null)
            return mod + cv.Name;
        if (cc.Callee is MemberAccessExpr { Object: VariableExpr mv, Member: var member }
            && classModuleMap.TryGetValue(member, out var mm) && mm != null
            && mm == mv.Name.Replace('.', '_') + "_")
            return mm + member;
        return null;
    }

    // Whether a name holds a CHARACTER at the read: 1 when every binding it has in the
    // read's scope is a `chr(...)` call, -1 when chr() binds it on some path and something
    // else on another (the value alone cannot say which it holds), 0 when chr() never binds
    // it. Same scoping as IsBoolName.
    private int CharNameState(string name)
    {
        string? scope = CurrentBoolScope();
        bool chr = charNames.Contains(name), other = nonCharNames.Contains(name);
        if (scope != null)
        {
            bool sChr = charScopes.TryGetValue(scope, out var cs) && cs.Contains(name);
            bool sOther = nonCharScopes.TryGetValue(scope, out var ns) && ns.Contains(name);
            // A binding in the function's own scope is a different name from the module's.
            if (sChr || sOther) { chr = sChr; other = sOther; }
        }
        return !chr ? 0 : other ? -1 : 1;
    }

    private void NoteBool(string? scope, string name)
    {
        if (scope == null) boolNames.Add(name);
        else
        {
            if (!boolScopes.TryGetValue(scope, out var s)) boolScopes[scope] = s = new();
            s.Add(name);
        }
    }

    private void NoteNonBool(string? scope, string name)
    {
        NoteScoped(nonCharScopes, nonCharNames, scope, name);
        if (scope == null) nonBoolNames.Add(name);
        else
        {
            if (!nonBoolScopes.TryGetValue(scope, out var s)) nonBoolScopes[scope] = s = new();
            s.Add(name);
        }
    }

    // The scope the read belongs to: inside an inline expansion the prefix's callee
    // segment, the last one (`inline{depth}.{callee}.`, or `inline{depth}.{enclosing}.
    // {callee}.` outside main), inside a regular body the qualified function name, at
    // module level null (the flat namespace).
    private string? CurrentBoolScope()
    {
        string p = currentInlinePrefix;
        if (p.Length > 1 && p[^1] == '.')
        {
            int d2 = p.Length - 1, d1 = p.LastIndexOf('.', d2 - 1);
            if (d1 >= 0 && d2 > d1 + 1) return p.Substring(d1 + 1, d2 - d1 - 1);
        }
        return currentFunction.Length > 0 ? currentFunction : null;
    }

    // True when `name` is bound to True/False everywhere the read can see it, so
    // interpolating it must print Python's words rather than the underlying byte.
    private bool IsBoolName(string name) => IsBoolNameIn(CurrentBoolScope(), name);

    private bool IsBoolNameIn(string? scope, string name)
    {
        if (scope != null && nonBoolScopes.TryGetValue(scope, out var ns) && ns.Contains(name))
            return false;
        if (scope != null && boolScopes.TryGetValue(scope, out var bs) && bs.Contains(name))
            return true;
        return boolNames.Contains(name) && !nonBoolNames.Contains(name);
    }

    private void RecordMemberAssignTarget(Expression target, string? owner = null)
    {
        switch (target)
        {
            case MemberAccessExpr ma: NoteAssignedMember(ma.Member, owner); break;
            case IndexExpr { Target: MemberAccessExpr ma2 }: NoteAssignedMember(ma2.Member, owner); break;
            case TupleExpr tup: foreach (var e in tup.Elements) RecordMemberAssignTarget(e, owner); break;
        }
    }

    // Record an assigned member in BOTH the program-wide superset and, when the enclosing class
    // is known, that class's own set. The superset is still written unconditionally: callers of
    // it outside the attribute-read check rely on it, and a receiver whose class cannot be
    // resolved still falls back to it.
    private void NoteAssignedMember(string member, string? owner)
    {
        assignedMemberNames.Add(member);
        if (string.IsNullOrEmpty(owner)) return;
        if (!assignedMemberNamesByClass.TryGetValue(owner, out var set))
            assignedMemberNamesByClass[owner] = set = new HashSet<string>();
        set.Add(member);
    }

    // True when the method body calls a sibling method on self (self.<m>(...)). Such a method,
    // if outlined, binds the self-call statically to its defining class; called on a subclass
    // instance it must be force-inlined instead so the call dispatches to the concrete override.
    private static bool MethodCallsSelfMethod(FunctionDef method)
    {
        bool found = false;
        void E(Expression? e)
        {
            if (found || e == null) return;
            switch (e)
            {
                case CallExpr { Callee: MemberAccessExpr { Object: VariableExpr { Name: "self" } } }:
                    found = true; return;
                case CallExpr c: E(c.Callee); foreach (var a in c.Args) E(a); return;
                case MemberAccessExpr ma: E(ma.Object); return;
                case BinaryExpr b: E(b.Left); E(b.Right); return;
                case UnaryExpr u: E(u.Operand); return;
                case TernaryExpr t: E(t.Condition); E(t.TrueVal); E(t.FalseVal); return;
                case IndexExpr ix: E(ix.Target); E(ix.Index); return;
                case KeywordArgExpr kw: E(kw.Value); return;
                case TupleExpr tu: foreach (var el in tu.Elements) E(el); return;
                case ListExpr le: foreach (var el in le.Elements) E(el); return;
            }
        }
        // The shared walk reaches arms the old per-statement recursion skipped:
        // try/with/match bodies and the expressions a for/with/match carries.
        foreach (var st in TypeInference.WalkStatements(method.Body.Statements))
        {
            if (found) break;
            switch (st)
            {
                case VarDecl vd: E(vd.Init); break;
                case AnnAssign a: E(a.Value); break;
                case AssignStmt asg: E(asg.Value); break;
                case AugAssignStmt aug: E(aug.Value); break;
                case ReturnStmt r: E(r.Value); break;
                case ExprStmt ex: E(ex.Expr); break;
                case IfStmt iff:
                    E(iff.Condition);
                    foreach (var br in iff.ElifBranches) E(br.Condition);
                    break;
                case WhileStmt wh: E(wh.Condition); break;
                case ForStmt fr:
                    E(fr.Iterable); E(fr.RangeStart); E(fr.RangeStop); E(fr.RangeStep);
                    break;
                case WithStmt wi: E(wi.ContextExpr); break;
                case MatchStmt m:
                    E(m.Target);
                    foreach (var br in m.Branches) { E(br.Pattern); E(br.Guard); }
                    break;
            }
        }
        return found;
    }

    // True when the class's own __init__ delegates to its base ctor. Such a subclass gains the
    // base's fields (set by the base ctor) in addition to its own, so its slot layout must merge
    // the base fields ahead of its own.
    //
    // Both spellings count: `super().__init__(...)` and the unbound `Base.__init__(self, ...)`.
    // The unbound one is ordinary Python and reaches the same base body, so a subclass written
    // that way owns the base's fields exactly as the super() one does -- reading only super()
    // here left the base fields out of the layout of any subclass that used the other spelling.
    private static bool InitCallsSuperInit(Block classBody, List<string>? bases = null)
    {
        FunctionDef? init = null;
        foreach (var s in classBody.Statements)
            if (s is FunctionDef f && f.Name == "__init__") { init = f; break; }
        if (init == null) return false;
        // Nested counts the same as top-level: `if external_vcc: super().__init__()`
        // and a `try:`-wrapped one still run the base constructor on the same self, so the
        // base's fields belong in the subclass layout either way. Nested defs are skipped
        // by the walk -- a closure calling super().__init__ is not the constructor running.
        foreach (var st in TypeInference.WalkStatements(init.Body.Statements))
        {
            if (st is not ExprStmt { Expr: CallExpr { Callee: MemberAccessExpr {
                    Member: "__init__" } recv } }) continue;
            if (recv.Object is CallExpr { Callee: VariableExpr { Name: "super" } }) return true;
            if (bases != null && recv.Object is VariableExpr baseVe && bases.Contains(baseVe.Name))
                return true;
        }
        return false;
    }

    private static readonly HashSet<string> ScalarTypeNames = new()
    {
        "uint8", "int8", "uint16", "int16", "uint32", "int32", "uint64", "int64",
        "int", "bool", "float", "void", "None", "str", "bytes", "bytearray",
        "const", "Callable", "gc_ref", "char",
    };

    // True for primitive/built-in type names (and any bracketed form like const[..]/ptr[..]/T[N]).
    // A field type that is NOT one of these is a class name -- tracked in fieldClasses.
    // CPython builtin types this compiler stores (memoryview, object, ...) live in
    // PythonBuiltinNames.RepresentedTypes so a call builtin and its annotation cannot drift.
    private static bool IsKnownBareTypeName(string ty) =>
        ScalarTypeNames.Contains(ty) || PythonBuiltinNames.IsRepresentedType(ty);

    private static bool IsScalarTypeName(string ty)
    {
        if (string.IsNullOrEmpty(ty)) return true;
        if (ty.Contains('[')) return true;
        return IsKnownBareTypeName(ty);
    }

    /// <summary>
    /// A list or tuple of compile-time numbers assigned to a field is an ARRAY field, not a
    /// scalar. Adafruit DPS310 writes <c>self._oversample_scalefactor = (524288, ...)</c>;
    /// counted as a uint8 the class became a one-field data class and <c>self.scale[n]</c>
    /// compiled as a bit index.
    /// </summary>
    private bool IsCompileTimeNumberSequence(Expression e)
    {
        if (IsRepeatedConstList(e)) return true;
        List<Expression>? elems = e switch
        {
            ListExpr le => le.Elements,
            TupleExpr te => te.Elements,
            _ => null
        };
        if (elems is not { Count: > 0 }) return false;
        foreach (var x in elems)
        {
            try { EvaluateConstantExpr(x); }
            catch { return false; }
        }
        return true;
    }

    /// <summary>
    /// <c>[None] * n</c> / <c>[0] * n</c> is an array field, not a scalar. The
    /// count may be <c>len(self)</c>, which only folds once the instance exists,
    /// so the shape is enough to keep the field out of the uint8 layout.
    /// </summary>
    private static bool IsRepeatedConstList(Expression e)
    {
        if (e is not BinaryExpr { Op: Frontend.BinaryOp.Mul } be) return false;
        ListExpr? lit = be.Left as ListExpr ?? be.Right as ListExpr;
        if (lit is not { Elements.Count: > 0 }) return false;
        foreach (var x in lit.Elements)
        {
            if (x is NoneLiteral) continue;
            if (x is not IntegerLiteral and not BooleanLiteral) return false;
        }
        return true;
    }

    private List<(string Field, string Type, string SourceParam)> DeriveFieldLayout(Block classBody,
        string classKey = "")
    {
        var layout = new List<(string, string, string)>();
        var seen = new HashSet<string>();

        FunctionDef? init = null;
        foreach (var s in classBody.Statements)
            if (s is FunctionDef f && f.Name == "__init__") { init = f; break; }
        if (init == null) return layout;

        var paramTypes = new Dictionary<string, string>();
        foreach (var p in init.Params) paramTypes[p.Name] = p.Type;

        // The class's own methods by name: `self.v = self._read()` is as wide as _read's
        // declared return type (used by InferAssignedFieldType below), and the transitive
        // construction-reachability walk follows `self.<m>()` edges through this table.
        var methodsByName = new Dictionary<string, FunctionDef>();
        foreach (var s in classBody.Statements)
            if (s is FunctionDef mf) methodsByName[mf.Name] = mf;

        // The annotated locals of __init__: a field first stored from `w: uint16 = v` is as
        // wide as w says, the way one stored from a `v: uint16` parameter already was. It
        // was laid out as a byte, and every later store, the runtime ones included,
        // truncated into it: a uint16 duty read back 0 (PyMCU#294).
        var localTypes = new Dictionary<string, string>();
        foreach (var s in TypeInference.WalkStatements(init.Body.Statements))
            switch (s)
            {
                case VarDecl vd when !string.IsNullOrEmpty(vd.VarType): localTypes[vd.Name] = vd.VarType; break;
                case AnnAssign an when !string.IsNullOrEmpty(an.Annotation): localTypes[an.Target] = an.Annotation; break;
            }
        InferMethodLocalTypes(init, paramTypes, localTypes);

        // Index into layout for every field that has one, so a write ANYWHERE in the body --
        // inside a loop, a branch, a try, a with -- can widen the entry the first write
        // introduced (#488). The layout used to rescan only the TOP-LEVEL statements of
        // __init__ for that join, so a 31-bit LCG update nested in `for x: for y:` never
        // reached it and the param-seeded field stayed a byte.
        var layoutIndex = new Dictionary<string, int>();
        // An explicit `self.f: T = ...` is the reader's own declaration: it pins the field's
        // type against anything a later write might infer.
        var pinnedTypes = new HashSet<string>();
        // Kind by field, for the categorical-mismatch diagnostic in the method scan below.
        var fieldKind = new Dictionary<string, string>();

        // The width an assignment's right-hand side evidences: an explicit annotation is the
        // writer's own declaration, anything else is inferred from the value.
        string? WriteEvidence(string? annotated, Expression? value,
            Dictionary<string, string> pt, Dictionary<string, string> lt) =>
            annotated != null
                ? (annotated.StartsWith("const[") && annotated.EndsWith("]")
                    ? annotated.Substring(6, annotated.Length - 7) : annotated)
                : InferAssignedFieldType(value, pt, lt, methodsByName);

        void WidenField(string field, string? evidence)
        {
            if (evidence == null || pinnedTypes.Contains(field)
                || !layoutIndex.TryGetValue(field, out var wi)) return;
            var cur = layout[wi];
            string t = cur.Item2;
            ApplyInferredFieldType(ref t, evidence);
            if (t == cur.Item2) return;
            layout[wi] = (cur.Item1, t, cur.Item3);
            fieldKind[field] = ClassifyFieldKind(t);
        }

        foreach (var s in TypeInference.WalkStatements(init.Body.Statements))
        {
            string? field = null;
            Expression? rhs = null;
            string? annotatedType = null;
            ASTNode? writeTarget = null;
            List<string>? declaredMembers = null;
            if (s is AssignStmt asg && asg.Target is MemberAccessExpr ma
                && ma.Object is VariableExpr sv && sv.Name == "self")
            {
                field = ma.Member;
                rhs = asg.Value;
                annotatedType = asg.AnnotatedType;
                writeTarget = ma;
            }
            // `self.f += v` is a write to the field too -- it counts for the join at any depth.
            else if (s is AugAssignStmt aug && aug.Target is MemberAccessExpr ama
                && ama.Object is VariableExpr asv && asv.Name == "self")
            {
                field = ama.Member;
                rhs = aug.Value;
                writeTarget = ama;
            }
            // `self.f: Union[A, B, ...] = v` -- an AnnAssign the parser keeps for
            // its bracketed annotation. A union member list declares the field's
            // tag domain outright (fieldDeclaredUnionMembers in NoteFieldWrite);
            // every other bracketed annotation on a member is the array-field
            // path, which lays its own storage and stays out of this scan.
            else if (s is AnnAssign uaa && uaa.UnionMembers != null
                     && uaa.Target.StartsWith("self.", StringComparison.Ordinal))
            {
                field = uaa.Target.Substring("self.".Length);
                rhs = uaa.Value;
                annotatedType = uaa.Annotation;
                writeTarget = uaa;
                declaredMembers = uaa.UnionMembers;
            }

            if (field == null) continue;
            // But `+=` cannot be the write that INTRODUCES the field: it reads the member
            // it augments, so a `self.f += v` to a name no assignment has declared is the
            // same AttributeError CPython raises -- not a declaration. Leaving it out of
            // `seen` keeps the field for a real assignment to claim, or for the read-side
            // refusal to name.
            if (s is AugAssignStmt && !seen.Contains(field)) continue;
            NoteFieldWrite(classKey, field, rhs, annotatedType, paramTypes, localTypes,
                declaredMembers);
            if (!seen.Add(field))
            {
                // The categorical-mismatch check the method scan below runs applies inside
                // __init__ too: `self.x = 5` then `self.x = "s"` is the same two writes
                // whether the second sits in a helper or three lines down in the same
                // constructor. Only evidenced kinds fire -- "unknown" stays silent, exactly
                // as it does between methods.
                string initWriteKind = ClassifyWriteKind(rhs, annotatedType, paramTypes, localTypes,
                    methodsByName);
                if (initWriteKind != "unknown"
                    && fieldKind.TryGetValue(field, out var initKind)
                    && initKind != "unknown" && initKind != initWriteKind)
                    throw UserError(
                        $"field '{field}' is first typed as {initKind} and is later given a "
                        + $"{initWriteKind} value in '__init__' -- PyMCU lays each field out at a "
                        + "single fixed type and width, so the two writes cannot share one field. "
                        + "Give the two roles different names, or keep the assigned type consistent",
                        writeTarget);

                WidenField(field, WriteEvidence(annotatedType, rhs, paramTypes, localTypes));
                continue;
            }

            // `self.buf = [0, 0, 0]` / `self.scale = (524288, ...)` declares an ARRAY field,
            // not a scalar one -- the same thing `self.buf: uint8[3] = [...]` declares, and
            // that spelling is an AnnAssign, which never reaches this layout at all. Counted
            // as a scalar the class became a one-field data class, its methods were outlined
            // with the field passed BY VALUE, and `self.buf[1]` inside one compiled as bit 1
            // of a byte: a silent wrong answer, not a diagnostic. Only all-constant literals:
            // `self.pins = [Pin(1), Pin(2)]` is a list of instances with its own lowering and
            // stays a field here.
            if (rhs != null && IsCompileTimeNumberSequence(rhs))
                continue;

            // `self._gpio = bytearray(n)` declares a BUFFER field -- EmitMemberAssign lays it
            // out as a fixed SRAM array (or the write refuses on its own for a runtime size).
            // Filed as a scalar here it took the uint8 default, and the property setter's
            // `self._gpio = val` (val: ReadableBuffer) then collided with a numeric field
            // (adafruit_74hc595). The field stays in `seen` -- so no later write re-introduces
            // it -- and out of `layout`, so no scalar kind exists to conflict with.
            if (rhs is CallExpr { Callee: VariableExpr bufCtor }
                && bufCtor.Name is "bytearray" or "bytes")
            {
                if (!classBufferFields.TryGetValue(classKey, out var bufSet))
                    classBufferFields[classKey] = bufSet = new HashSet<string>();
                bufSet.Add(field);
                continue;
            }

            // `self.cells = [[v]*W for _ in range(H)]` -- a grid FIELD: one flat
            // per-instance array, not a scalar. Same exemption as the bytearray
            // buffer above: kept out of `layout`, kept in `seen`.
            if (rhs is ListCompExpr compRhs && IsGridComprehension(compRhs))
            {
                if (!classBufferFields.TryGetValue(classKey, out var gridSet))
                    classBufferFields[classKey] = gridSet = new HashSet<string>();
                gridSet.Add(field);
                continue;
            }

            // `self._device = i2c_device.I2CDevice(i2c, address)` declares an OBJECT
            // field -- a nested instance whose fields flatten under `<obj>_<field>_*`,
            // not a scalar the slot could hold. Filed as a uint8 the boxed instance
            // stored the dead anchor name into its slot, and every `with self._device`
            // then read flattened names under the manager variable -- read-never-written
            // slots that sent 0x00 on the bus (adafruit_tcs34725). Same exemption as the
            // buffer field above: kept out of `layout`, kept in `seen`.
            if (IsObjectFieldWrite(rhs, annotatedType, paramTypes, localTypes))
            {
                if (!classInstanceFields.TryGetValue(classKey, out var objSet))
                    classInstanceFields[classKey] = objSet = new HashSet<string>();
                objSet.Add(field);
                continue;
            }

            // SourceParam: the __init__ param that directly initializes the field
            // (RHS is a bare parameter), else "" -- needed for factory return lowering.
            // A string literal or bytearray() is not a uint8: leaving it as the default
            // made `self._message = ""` numeric and the setter's `str` a contradiction
            // (adafruit_character_lcd), and `self._gpio = bytearray(n)` the same
            // (adafruit_74hc595).
            string type = "uint8";
            string srcParam = "";
            // An explicit `self.x: T = ...` annotation wins -- the field gets its
            // declared width (otherwise a uint32 field would default to uint8 and
            // truncate, e.g. a timer deadline or a 1<<24 bit mask).
            if (annotatedType != null)
            {
                type = annotatedType.StartsWith("const[") && annotatedType.EndsWith("]")
                    ? annotatedType.Substring(6, annotatedType.Length - 7)
                    : annotatedType;
                // A declared union field's scalar slot is its widest member -- the
                // tag byte lives beside it, not inside the layout's size.
                if (PyMCU.Common.AnnotationText.UnionMembers(annotatedType) is { } ufm
                    && ufm.Count >= 2)
                    type = WidestUnionMemberName(ufm);
                pinnedTypes.Add(field);
            }
            if (rhs is VariableExpr rv && paramTypes.TryGetValue(rv.Name, out var pt))
            {
                // An augmented write (`self.x += p`) is not the field's initial value, so it
                // cannot claim p as the constructor argument that fills it.
                if (s is AssignStmt) srcParam = rv.Name;
                if (annotatedType == null)
                    type = pt.StartsWith("const[") && pt.EndsWith("]")
                        ? pt.Substring(6, pt.Length - 7) // const[uint8] -> uint8
                        : pt;
            }
            else if (rhs is VariableExpr lv && annotatedType == null
                     && localTypes.TryGetValue(lv.Name, out var lt) && !string.IsNullOrEmpty(lt))
            {
                type = lt.StartsWith("const[") && lt.EndsWith("]") ? lt.Substring(6, lt.Length - 7) : lt;
            }
            // `self._message = ""` is a STR field -- the uint8 default filed it numeric, and
            // the setter's `self._message = message` (message: str) then read as a kind
            // conflict (adafruit_character_lcd). Same type the `self.x: str = ...` spelling
            // already lands on.
            else if (rhs is StringLiteral) type = "str";

            // `self._active = False` is a BOOL field, the same evidence `= val` where
            // `val: bool` gives through paramTypes above. It has to be decided HERE, at
            // introduction: bool ranks with uint8 (one byte) in ScalarWidthRank, so a
            // later ApplyInferredFieldType join can never promote the uint8 default to
            // bool -- and without the tag the field prints 0/1 where CPython spells
            // False/True (adafruit_tcs34725.active). A comparison is the same evidence:
            // `self.is_differential = negative_pin is not None` stores the bool the
            // `is not` yields, not negative_pin's width (adafruit_mcp3xxx).
            else if (rhs is BooleanLiteral
                     or UnaryExpr { Op: Frontend.UnaryOp.Not }
                     or BinaryExpr { Op: Frontend.BinaryOp.Equal or Frontend.BinaryOp.NotEqual
                         or Frontend.BinaryOp.Less or Frontend.BinaryOp.LessEq
                         or Frontend.BinaryOp.Greater or Frontend.BinaryOp.GreaterEq
                         or Frontend.BinaryOp.Is or Frontend.BinaryOp.IsNot
                         or Frontend.BinaryOp.In or Frontend.BinaryOp.NotIn })
                type = "bool";

            // A field whose value is an EXPRESSION took uint8 and truncated silently: the
            // width came from the field, not from what was stored in it, so
            // `self._period = uint16(1000000 // uint32(hz))` read back as 20000 & 0xFF. Nothing
            // was said, and a servo driven from such a field ran at a fraction of the pulse it
            // was asked for (PyMCU#322). An explicit annotation still wins -- that is the
            // reader's declaration -- and everything else takes the widest value assigned.
            // Every later write to this field (at any depth, in any method) joins through
            // WidenField when the walk reaches it.
            if (annotatedType == null)
                ApplyInferredFieldType(ref type,
                    InferAssignedFieldType(rhs, paramTypes, localTypes, methodsByName));

            layoutIndex[field] = layout.Count;
            layout.Add((field, type, srcParam));
            // Tracked from the first write so a LATER write inside this same __init__ can
            // be judged against it -- the kind-conflict check above needs the established
            // kind populated here, not only after the loop. The kind is what the WRITE
            // evidences, not `type`: an unevidenced write (`self._cs = cs.name`, a member
            // read of a `const`-typed field) still lands on the uint8 default in `layout`,
            // but filing that default as "numeric" made the stdlib's own sentinel
            // (`self._cs = cs.name` / `self._cs = ""`, softspi) read as a conflict.
            fieldKind[field] = ClassifyWriteKind(rhs, annotatedType, paramTypes, localTypes,
                methodsByName);
        }

        // Every OTHER method of the class (property setters, and plain helper methods called
        // from __init__) can also introduce a field: the layout used to come from __init__
        // alone, so a field first written from a setter (adafruit_tcs34725's
        // `integration_time.setter` sets `self._integration_time`, PyMCU#397) or from a helper
        // method __init__ calls (adafruit_motor.servo's `set_pulse_width_range` sets
        // `self._min_duty`, same issue) was invisible to the layout, and every read or write of
        // it OUTSIDE __init__ was refused as "not a field". Measured against CPython,
        // MicroPython and CircuitPython (issue #397): a plain method assigning `self.x = ...`
        // makes `x` exactly as real a field as one set in __init__.
        //
        // Writes are collected at ANY depth of the method body (#488): a `self.f = ...`
        // inside a `for`/`while`/`if`/`try`/`with` is the same write for layout purposes,
        // so it joins the field's width and answers the kind-conflict diagnostic exactly
        // as a top-level one does. Nested `def` bodies keep being skipped -- a function
        // closing over `self` is not this method's evidence.
        //
        // Which of this class's methods run during construction: every `self.<m>()` call
        // __init__ can reach, chased TRANSITIVELY through the helpers themselves --
        // `__init__` -> `_setup` -> `_reset` puts all three inside the constructor's own
        // call graph, so a field `_reset` first writes is still a construction-time field.
        // A method no such path reaches (called only from user code after construction)
        // keeps the typo-safety refusal below, exactly as before.
        var ctorReachable = new HashSet<string>();
        var pendingMethods = new Queue<FunctionDef>();
        pendingMethods.Enqueue(init);
        while (pendingMethods.Count > 0)
            foreach (var callee in SelfMethodCalls(pendingMethods.Dequeue()))
                if (ctorReachable.Add(callee)
                    && methodsByName.TryGetValue(callee, out var calleeDef))
                    pendingMethods.Enqueue(calleeDef);

        foreach (var s in classBody.Statements)
        {
            if (s is not FunctionDef m || m.Name == "__init__") continue;

            // A method may INTRODUCE a field only when it is a property setter (the shape
            // adafruit_tcs34725's `integration_time.setter` uses for `self._integration_time`)
            // or is called directly from __init__ (the shape
            // adafruit_motor.servo's `__init__` uses, calling `set_pulse_width_range` which sets
            // `self._min_duty`). Both are the constructor's own initialization logic, just
            // factored out of its body.
            //
            // A method reachable only from OUTSIDE the constructor (called from user code after
            // construction, e.g. `update()`) does NOT get this privilege: `seen.Contains` below
            // still lets it WRITE an already-declared field, but a name novel to this method is
            // left for the existing write-path check in Assign.cs to refuse, exactly as before
            // this change (FieldWrite_UndeclaredOutsideInit_NamesTheClassAndTheField) -- a typo
            // there (`self.tempreature = raw` for a field the class calls `temperature`) has to
            // stay a compile error, not a silently-created shadow field, and real interpreters
            // give no signal here to tell the two apart (Phase 1 measurement: they simply allow
            // both). Restricting to the constructor's own call graph is what keeps that
            // typo-safety net for the code it always protected, without also blocking the two
            // real, measured shapes it was never meant to catch.
            bool mayIntroduceFields = m.IsPropertySetter || ctorReachable.Contains(m.Name);

            var mParamTypes = new Dictionary<string, string>();
            foreach (var p in m.Params) mParamTypes[p.Name] = p.Type;
            var mLocalTypes = new Dictionary<string, string>();
            foreach (var ls in TypeInference.WalkStatements(m.Body.Statements))
                switch (ls)
                {
                    case VarDecl vd when !string.IsNullOrEmpty(vd.VarType): mLocalTypes[vd.Name] = vd.VarType; break;
                    case AnnAssign an when !string.IsNullOrEmpty(an.Annotation): mLocalTypes[an.Target] = an.Annotation; break;
                }
            InferMethodLocalTypes(m, mParamTypes, mLocalTypes);

            foreach (var ms in TypeInference.WalkStatements(m.Body.Statements))
            {
                string? field = null;
                Expression? rhs = null;
                string? annotatedType = null;
                ASTNode? writeTarget = null;
                List<string>? declaredMembers = null;
                if (ms is AssignStmt masg && masg.Target is MemberAccessExpr mma
                    && mma.Object is VariableExpr msv && msv.Name == "self")
                {
                    field = mma.Member;
                    rhs = masg.Value;
                    annotatedType = masg.AnnotatedType;
                    writeTarget = mma;
                }
                else if (ms is AugAssignStmt maug && maug.Target is MemberAccessExpr ama
                    && ama.Object is VariableExpr asv && asv.Name == "self")
                {
                    field = ama.Member;
                    rhs = maug.Value;
                    writeTarget = ama;
                }
                // Same `self.f: Union[...]` AnnAssign arm as the __init__ scan above:
                // a declared union field's member list is filed wherever the write sits.
                else if (ms is AnnAssign muaa && muaa.UnionMembers != null
                         && muaa.Target.StartsWith("self.", StringComparison.Ordinal))
                {
                    field = muaa.Target.Substring("self.".Length);
                    rhs = muaa.Value;
                    annotatedType = muaa.Annotation;
                    writeTarget = muaa;
                    declaredMembers = muaa.UnionMembers;
                }
                if (field == null) continue;
                // `+=` widens and kind-checks a field already introduced, but cannot be the
                // write that declares one (same reasoning as the __init__ loop above).
                if (ms is AugAssignStmt && !seen.Contains(field)) continue;
                NoteFieldWrite(classKey, field, rhs, annotatedType, mParamTypes, mLocalTypes,
                    declaredMembers);

                // Same array-field exemption as the __init__ scan above: a field whose value is
                // a literal list or tuple of compile-time constants is an array field, handled
                // by its own lowering, not a scalar the layout should claim.
                if (rhs != null && IsCompileTimeNumberSequence(rhs))
                    continue;

                // `self.f = bytearray(n)` is a BUFFER write wherever it appears -- codegen
                // lowers it from the call's own shape, so it must not feed the scalar kind
                // check: a None-seeded field later handed a buffer is the lazy-allocation
                // idiom (adafruit_pixelbuf's `_pre_brightness_buffer`, assigned inside the
                // brightness setter's `if`), not a numeric-vs-other conflict.
                if (rhs is CallExpr { Callee: VariableExpr mbc } && mbc.Name is "bytearray" or "bytes")
                {
                    if (!classBufferFields.TryGetValue(classKey, out var mBufSet))
                        classBufferFields[classKey] = mBufSet = new HashSet<string>();
                    mBufSet.Add(field);
                    if (mayIntroduceFields) seen.Add(field);
                    continue;
                }

                // Same exemption as the __init__ scan above: `self.f = SomeClass(...)` (or a
                // class-typed parameter/annotation) is an OBJECT write wherever it appears.
                // A field first seeded `None` and later handed an instance is the lazy-init
                // idiom, not a numeric-vs-other conflict -- and the scalar entry the seed
                // write left in `layout` is filtered out below, with the buffer fields'.
                if (IsObjectFieldWrite(rhs, annotatedType, mParamTypes, mLocalTypes))
                {
                    if (!classInstanceFields.TryGetValue(classKey, out var mObjSet))
                        classInstanceFields[classKey] = mObjSet = new HashSet<string>();
                    mObjSet.Add(field);
                    if (mayIntroduceFields) seen.Add(field);
                    fieldKind.Remove(field);
                    continue;
                }

                string writeKind = ClassifyWriteKind(rhs, annotatedType, mParamTypes, mLocalTypes,
                    methodsByName);

                if (seen.Contains(field))
                {
                    // A later write is only flagged when BOTH sides carry enough evidence to
                    // judge a categorical mismatch (numeric vs. str vs. anything else); scalar
                    // widening (uint8 -> uint16, say) is the SAME kind and always allowed, exactly
                    // as the widening pass above already allows it within __init__ itself.
                    if (writeKind != "unknown" && fieldKind.TryGetValue(field, out var establishedKind)
                        && establishedKind != "unknown" && establishedKind != writeKind)
                        throw UserError(
                            $"field '{field}' is first typed as {establishedKind} and is later given a "
                            + $"{writeKind} value in '{m.Name}' -- PyMCU lays each field out at a single "
                            + "fixed type and width, so the two writes cannot share one field. Give the "
                            + "two roles different names, or keep the assigned type consistent",
                            writeTarget);

                    // The same write also joins the field's width (#488): a 31-bit LCG
                    // update inside a method's nested loop is what makes a param-bound
                    // field uint32, not the byte its __init__ seed happened to fit in.
                    WidenField(field, WriteEvidence(annotatedType, rhs, mParamTypes, mLocalTypes));
                    continue;
                }

                if (!mayIntroduceFields) continue;

                seen.Add(field);
                string mType = "uint8";
                string mSrcParam = "";
                if (annotatedType != null)
                {
                    mType = annotatedType.StartsWith("const[") && annotatedType.EndsWith("]")
                        ? annotatedType.Substring(6, annotatedType.Length - 7)
                        : annotatedType;
                    pinnedTypes.Add(field);
                }
                else
                {
                    string? w = InferAssignedFieldType(rhs, mParamTypes, mLocalTypes, methodsByName);
                    if (w != null) mType = w;
                }

                layoutIndex[field] = layout.Count;
                layout.Add((field, mType, mSrcParam));
                // Same rule as the __init__ introduction above: the recorded kind is the
                // write's own evidence (`writeKind`), not `mType` -- the uint8 default is a
                // layout fallback, not proof the field is numeric.
                fieldKind[field] = writeKind;
            }
        }

        // A field an object write claimed anywhere stays out of the scalar layout no
        // matter which write introduced it: a `None` seed in __init__ may have filed a
        // uint8 entry before the method scan ever saw the instance write.
        if (classInstanceFields.TryGetValue(classKey, out var claimedObj))
            layout.RemoveAll(e => claimedObj.Contains(e.Item1));

        // A field no write annotated is checked at every store, and one an earlier run found
        // too narrow is laid out at the width it needs (WidthSeeds).
        for (int i = 0; i < layout.Count; i++)
            if (!pinnedTypes.Contains(layout[i].Item1))
                layout[i] = (layout[i].Item1, SeedFieldType(classKey, layout[i].Item1, layout[i].Item2),
                             layout[i].Item3);

        return layout;
    }

    // `self.f = SomeClass(...)` or `self.f = mod.SomeClass(...)`: the write settles the field
    // to a nested INSTANCE, whose storage is its own flattened `<obj>_f_*` names. The slot
    // layout can only file a scalar byte for it -- the dead anchor var -- so fields matching
    // this shape are kept out of `layout` and registered in classInstanceFields instead.
    // Two exceptions keep the field's layout entry: an explicit `self.f: SomeClass`
    // annotation already records the class as the entry's own type (the record the
    // nested-instance machinery reads to mint `<obj>_f_*` -- excluding it orphans the
    // field, held-instance-field), and `self.f = p` where p is a class-typed parameter
    // threads the caller's instance through the field's own slot (held-write-anon).
    private bool IsObjectFieldWrite(Expression? rhs, string? annotatedType,
        Dictionary<string, string> paramTypes, Dictionary<string, string> localTypes)
    {
        if (annotatedType != null && IsKnownClassPath(annotatedType)) return false;
        string? ty = null;
        switch (rhs)
        {
            case CallExpr { Callee: VariableExpr cv } when !IsScalarTypeName(cv.Name):
                ty = cv.Name;
                break;
            case CallExpr { Callee: MemberAccessExpr cm } when !IsScalarTypeName(cm.Member):
                ty = DottedExprText(cm);
                break;
        }
        return ty != null && IsKnownClassPath(ty);
    }

    /// <summary>
    /// How wide a scalar type name is, for "the widest value assigned wins". Anything that is
    /// not a plain integer or float width ranks 0, so it never widens a field on its own.
    /// </summary>
    private static int ScalarWidthRank(string ty) => ty switch
    {
        "bool" or "uint8" or "int8" or "char" => 1,
        "uint16" or "int16" => 2,
        "int" or "uint32" or "int32" => 3,
        "float" => 4,
        _ => 0,
    };

    /// A string or buffer inference replaces the uint8 default; it must not then be
    /// overwritten by a numeric rank (str ranks 0, uint16 ranks 2).
    private static void ApplyInferredFieldType(ref string type, string? w)
    {
        if (w == null) return;
        if (w is "str" or "bytearray" or "bytes")
        {
            type = w;
            return;
        }
        if (type is "str" or "bytearray" or "bytes") return;
        if (ScalarWidthRank(w) > ScalarWidthRank(type)) type = w;
    }

    // The `self.<m>(...)` calls a method's body makes, at any statement depth -- a helper
    // invoked inside the caller's own `for`/`if`/`try` is still invoked by the caller (#488).
    // DeriveFieldLayout walks these edges from __init__ outward to find every method that
    // runs during construction. Only bare call STATEMENTS count, the same shape
    // IsCalledDirectlyFromInit recognised: a `self.<m>()` buried in an expression is no less
    // a call, but widening what counts as construction reach is its own change.
    private static IEnumerable<string> SelfMethodCalls(FunctionDef fn)
    {
        foreach (var s in TypeInference.WalkStatements(fn.Body.Statements))
            if (s is ExprStmt { Expr: CallExpr { Callee: MemberAccessExpr { Member: var callee } ma } }
                && ma.Object is VariableExpr { Name: "self" })
                yield return callee;
    }

    // Coarse type "kind" used ONLY to catch a field whose declared/inferred type changes
    // categorically across write sites (numeric <-> str <-> anything else) -- NOT to reject
    // normal scalar widening (uint8 -> uint16 stays "numeric" and is always allowed, matching
    // the widening DeriveFieldLayout already does across multiple writes within __init__).
    // An EMPTY type is "unknown", not "other": it is what an unannotated parameter leaves
    // (`self.rng = seed`), a field whose kind nothing has yet stated -- filing it "other"
    // refused its first evidenced write as a conflict (#488). A bare `const` is numeric:
    // the parameter is a compile-time integer placeholder by construction.
    private static string ClassifyFieldKind(string type) =>
        type.Length == 0 ? "unknown"
        : type == "const" || ScalarWidthRank(type) > 0 ? "numeric"
        : type is "str" or "const[str]" ? "str" : "other";

    // The kind an assignment's right-hand side settles a field to, or "unknown" when nothing
    // here has enough evidence to say (a call, a field/index read, an unrecognized expression,
    // ...). Deliberately conservative: "unknown" never triggers the incompatible-type
    // diagnostic in DeriveFieldLayout, so this only fires on a REAL, evidenced mismatch (e.g.
    // Phase 1 probe C: a field first assigned an int literal, later assigned a string literal).
    private string ClassifyWriteKind(Expression? rhs, string? annotatedType,
        Dictionary<string, string> paramTypes, Dictionary<string, string> localTypes,
        Dictionary<string, FunctionDef>? selfMethods = null)
    {
        if (annotatedType != null)
        {
            if (annotatedType == "const") return "unknown";
            var t = annotatedType.StartsWith("const[") && annotatedType.EndsWith("]")
                ? annotatedType.Substring(6, annotatedType.Length - 7) : annotatedType;
            // Same reasoning as the parameter branch below: a Union/Optional annotation is
            // a per-call-site choice of types, not one kind a field layout can contradict.
            if (t.StartsWith("Union[") || t.StartsWith("Optional[")) return "unknown";
            return ClassifyFieldKind(t);
        }
        if (rhs is StringLiteral) return "str";
        // A numeric literal is categorical evidence even when it is too small to widen the
        // field's type (InferAssignedFieldType answers null for a value that fits uint8 --
        // "no wider width needed", not "no kind"): `self.x = "s"` then `self.x = 5` is the
        // same conflict as `= 70000`, which the width path already catches.
        if (rhs is IntegerLiteral or FloatLiteral) return "numeric";
        if (rhs is VariableExpr ve)
        {
            string? d = paramTypes.TryGetValue(ve.Name, out var pv) ? pv
                      : localTypes.TryGetValue(ve.Name, out var lv) ? lv : null;
            if (!string.IsNullOrEmpty(d))
            {
                // A `Union[...]`/`Optional[...]` annotation is a per-call-site CHOICE, not a
                // kind this scan can file: `address: Union[uint8, List[uint8]]` bound to a
                // field reads "other" here and collided with the uint8 its index-extraction
                // write implied. Like bare `const`, it states no single kind.
                if (d.StartsWith("Union[") || d.StartsWith("Optional[")) return "unknown";
                // A bare `const` parameter (e.g. `pull_mode: const`) is PyMCU's own
                // compile-time-constant placeholder -- its real value type is whatever the
                // call site passes, not a fixed kind this scan can see. Real case: AVR's Pin
                // class had `self._pull_up = 0` in __init__ and `self._pull_up = pull_mode`
                // (a bare `const` param, always called with an int) in a later method; treating
                // the placeholder as its own "kind" misclassified this as numeric-vs-other and
                // refused the whole stdlib build. `const[X]` (a KNOWN wrapped type) is unwrapped
                // and classified normally, same as everywhere else in this file.
                if (d == "const") return "unknown";
                if (d.StartsWith("const[") && d.EndsWith("]")) d = d.Substring(6, d.Length - 7);
                return ClassifyFieldKind(d);
            }
        }
        var inferred = InferAssignedFieldType(rhs, paramTypes, localTypes, selfMethods);
        return inferred != null ? ClassifyFieldKind(inferred) : "unknown";
    }

    // RFC 0009 phase 3: record one `self.<field> = <rhs>` write's None/scalar
    // evidence. A field that sees BOTH kinds across the class's methods is a union
    // field (payload + tag byte, the field-level Optional). Scalar means "a value
    // the payload can hold" -- a literal, a name, arithmetic, a call -- anything
    // whose write is not a sequence literal, a buffer, an instance construction or
    // a string. Those carry their own field kinds and stay out of the tag domain.
    private void NoteFieldWrite(string classKey, string field, Expression? rhs,
        string? annotatedType, Dictionary<string, string> paramTypes,
        Dictionary<string, string> localTypes, List<string>? declaredMembers = null)
    {
        if (!fieldNoneWrites.TryGetValue(classKey, out var noneSet))
            fieldNoneWrites[classKey] = noneSet = new HashSet<string>();
        if (!fieldScalarWrites.TryGetValue(classKey, out var scalarSet))
            fieldScalarWrites[classKey] = scalarSet = new HashSet<string>();

        // `self.f: Union[...]`/`Optional[...]`/`A | B` declares the member list
        // outright. The AnnAssign arm carries it on the AST node itself; the
        // plain-assignment arm can only re-derive it from the annotation text,
        // which `Optional[X]` has already collapsed to X -- that single-member
        // shape answers null and the write evidence below decides the field.
        List<string>? dm = declaredMembers
            ?? (annotatedType is { } at ? PyMCU.Common.AnnotationText.UnionMembers(at) : null);
        if (dm is { Count: >= 2 })
        {
            // The member list is validated where it is recorded: an unchecked list
            // let `self.x: uint8[2] | bool` file a plain array field and drop the
            // `| bool` with no diagnostic at all.
            ValidateUnionMembers($"the union field '{classKey}.{field}'", dm, rhs);
            fieldDeclaredUnionMembers[classKey + "|" + field] = dm;
            if (dm.Any(m => m != "None")) scalarSet.Add(field);
            if (dm.Contains("None")) noneSet.Add(field);
        }

        if (rhs is NoneLiteral) { noneSet.Add(field); return; }
        if (rhs == null) return;
        if (annotatedType is { } at2)
        {
            // An annotated scalar write is scalar evidence; a str/buffer/instance
            // annotation is a different field kind.
            var ant = at2.StartsWith("const[") && at2.EndsWith("]") ? at2[6..^1] : at2;
            if (ScalarWidthRank(ant) > 0 || ant == "bool" || ant == "float")
            {
                scalarSet.Add(field);
                NoteUnionMemberEvidence(classKey, field, MemberNameForTypeText(ant));
            }
            return;
        }
        bool scalar = rhs switch
        {
            IntegerLiteral or FloatLiteral or BooleanLiteral => true,
            StringLiteral or FStringExpr => false,
            ListExpr or TupleExpr or DictExpr or SetExpr or ListCompExpr or GeneratorExpr => false,
            VariableExpr ve =>
                !(paramTypes.TryGetValue(ve.Name, out var pv) && IsNonScalarFieldType(pv))
                && !(localTypes.TryGetValue(ve.Name, out var lv) && IsNonScalarFieldType(lv)),
            // `self.f = Cls(...)` builds an instance; a scalar conversion call or a
            // method/function result is scalar evidence. classNames fills as the
            // scan walks, so a class declared LATER in the same file would be missed;
            // classModuleMap is filed for every class before the layout pass runs.
            CallExpr { Callee: VariableExpr cv } =>
                cv.Name is not ("bytearray" or "bytes" or "str")
                && !classNames.Contains(cv.Name) && !classNames.Contains(ResolveCallee(cv.Name))
                && !classModuleMap.ContainsKey(cv.Name),
            // `self.f = mod.Cls(...)` is the same instance construction through a
            // dotted path -- IsObjectFieldWrite is the identical check the layout
            // pass files the field under, so whatever claims the field as an
            // object there cannot also feed the tag domain here. An instance has
            // no member slot (RFC 0009 decision 4): None is the field's absence
            // marker (adafruit_74hc595's `self._device`, SPIDevice or bitbang).
            // A module FUNCTION's result stays scalar evidence.
            CallExpr { Callee: MemberAccessExpr } =>
                !IsObjectFieldWrite(rhs, annotatedType, paramTypes, localTypes),
            _ => true,
        };
        if (scalar)
        {
            scalarSet.Add(field);
            NoteUnionMemberEvidence(classKey, field, ScanUnionMemberName(rhs, paramTypes, localTypes));
        }
    }

    /// Append <paramref name="memberName"/> to the class|field evidence list, first-seen
    /// order preserved. A name the scan cannot spell (a call result, a union source) is
    /// left out -- the emit-time member growth covers it.
    private void NoteUnionMemberEvidence(string classKey, string field, string? memberName)
    {
        if (memberName == null) return;
        string key = classKey + "|" + field;
        if (!fieldUnionMemberEvidence.TryGetValue(key, out var ev))
            fieldUnionMemberEvidence[key] = ev = new List<string>();
        if (!ev.Contains(memberName)) ev.Add(memberName);
    }

    /// The member name a field write's rhs contributes at scan time, mirroring
    /// UnionMemberNameFor's emit-time rules: literals name their kind (an int by
    /// smallest fit), a variable the type its annotation/declaration gave it.
    /// Anything the scan cannot type returns null -- not a member refusal, just
    /// "the emitter will name it when it lowers the write".
    private static string? ScanUnionMemberName(Expression rhs,
        Dictionary<string, string> paramTypes, Dictionary<string, string> localTypes)
        => rhs switch
        {
            FloatLiteral => "float",
            BooleanLiteral => "bool",
            IntegerLiteral il => il.Value switch
            {
                < short.MinValue => "int32", < sbyte.MinValue => "int16", < 0 => "int8",
                <= byte.MaxValue => "uint8", <= ushort.MaxValue => "uint16",
                _ => "uint32",
            },
            VariableExpr ve => MemberNameForTypeText(
                paramTypes.TryGetValue(ve.Name, out var pt) ? pt
                : localTypes.TryGetValue(ve.Name, out var lt) ? lt : ""),
            _ => null,
        };

    /// The union-member spelling of an annotation/local type text, or null for a
    /// kind a scalar payload never holds (str, buffer, instance, another union --
    /// a live-union source's members join wholesale at emit time).
    private static string? MemberNameForTypeText(string t)
    {
        if (t.StartsWith("const[") && t.EndsWith("]")) t = t[6..^1];
        if (t.Contains('[')) return null;
        if (t == "bool") return "bool";
        return DataTypeExtensions.StringToDataType(t) switch
        {
            DataType.FLOAT => "float",
            DataType.INT8 => "int8", DataType.INT16 => "int16", DataType.INT32 => "int32",
            DataType.UINT8 => "uint8", DataType.UINT16 => "uint16", DataType.UINT32 => "uint32",
            _ => null,
        };
    }

    private bool IsNonScalarFieldType(string t)
    {
        if (t.StartsWith("const[") && t.EndsWith("]")) t = t[6..^1];
        if (t is "str" or "bytearray" or "bytes" or "ptr") return true;
        if (t.StartsWith("Union[") || t.StartsWith("Optional[")) return false;
        if (t.Contains('[')) return true;   // list[..], tuple[..], ptr[..], array
        // classNames fills as the scan walks -- a callee defined LATER in the same
        // file (BitmapFont below Framebuf's text()) is still invisible there, so
        // classModuleMap, which ScanGlobals files for every class before the layout
        // pass runs, answers the ordering classNames cannot.
        return classNames.Contains(t) || classNames.Contains(ResolveCallee(t))
            || classModuleMap.ContainsKey(t);
    }

    /// <summary>
    /// The width an expression stored into an unannotated field needs, or null when nothing
    /// here decides it. Deliberately narrow: a conversion call says its own type, a literal
    /// says the narrowest type that holds it, a parameter or annotated local says its
    /// declaration, and an arithmetic expression is as wide as its widest operand. Anything
    /// else (a call to a function, a field read, an index) answers null and leaves the field
    /// at the width it already had.
    /// </summary>
    /// <summary>
    /// Give an UNANNOTATED method local a scan-time type when its own assignments
    /// reveal one: `x = a / b` is float (ExprCouldBeFloat), `x = 100000` is uint32,
    /// `x = p` is p's type. Iterate to a fixpoint so `b = a` settles after
    /// `a = x / 10`. Without it `self.f = tmp` (tmp unannotated) filed no member
    /// evidence and no width: a union field kept a byte-wide payload and a member
    /// list missing 'float' -- both fatal to a slot layout, which is fixed before
    /// any body lowers. Annotations always win; among inferred writes the widest
    /// keeps the entry.
    /// </summary>
    private void InferMethodLocalTypes(FunctionDef m, Dictionary<string, string> paramTypes,
        Dictionary<string, string> localTypes)
    {
        var inferred = new HashSet<string>();
        for (int pass = 0; pass < 4; ++pass)
        {
            bool grew = false;
            foreach (var ls in TypeInference.WalkStatements(m.Body.Statements))
            {
                if (ls is not AssignStmt la || la.Target is not VariableExpr lv
                    || paramTypes.ContainsKey(lv.Name)) continue;
                if (localTypes.ContainsKey(lv.Name) && !inferred.Contains(lv.Name)) continue;
                string? t = ExprCouldBeFloat(la.Value) ? "float"
                    : InferAssignedFieldType(la.Value, paramTypes, localTypes);
                if (t == null) continue;
                if (!inferred.Contains(lv.Name)
                    || ScalarWidthRank(t) > ScalarWidthRank(localTypes[lv.Name]))
                {
                    localTypes[lv.Name] = t;
                    inferred.Add(lv.Name);
                    grew = true;
                }
            }
            if (!grew) break;
        }
    }

    private string? InferAssignedFieldType(Expression? e,
                                           Dictionary<string, string> paramTypes,
                                           Dictionary<string, string> localTypes,
                                           Dictionary<string, FunctionDef>? selfMethods = null)
    {
        switch (e)
        {
            case null:
                return null;

            case CallExpr { Callee: VariableExpr cv } when ScalarWidthRank(cv.Name) > 0:
                return cv.Name;

            // `self.v = self._read()` is as wide as `_read`'s DECLARED return type -- the
            // callee's own contract, the same evidence an explicit `self.v: uint16 = ...`
            // annotation would give. Only a numeric scalar declaration answers here: a
            // `-> str`/buffer/class return is not a width this scalar layout holds (the
            // write's kind simply stays "unknown", exactly as before), and an unannotated
            // helper has no declared type at all -- inferring it is return-inference's job,
            // which does not see class methods.
            case CallExpr { Callee: MemberAccessExpr { Member: var mname,
                    Object: VariableExpr { Name: "self" } } }
                when selfMethods != null && selfMethods.TryGetValue(mname, out var calleeFn):
            {
                var rt = calleeFn.ReturnType ?? "";
                if (rt.StartsWith("const[") && rt.EndsWith("]"))
                    rt = rt.Substring(6, rt.Length - 7);
                return ScalarWidthRank(rt) > 0 ? rt : null;
            }

            // `self.t = time.monotonic()` is as wide as the callee's DECLARED return
            // type -- the same evidence `self.v = self._read()` takes above, for a
            // module function instead of a sibling method. Without it the field kept
            // the width of an earlier literal write (`self._last_called = 0` laid out
            // as a byte, the monotonic() float truncated to 0) and every reader of the
            // field saw the truncated value (adafruit_dht's rate-limit timestamp).
            case CallExpr { Callee: MemberAccessExpr { Object: VariableExpr mod, Member: var modFn } }
                when NamesAModuleMember(mod.Name, modFn):
            {
                string realMod = TryImportedAlias(mod.Name, out var rm) && rm != null ? rm : mod.Name;
                if (!functionReturnTypes.TryGetValue(realMod.Replace('.', '_') + "_" + modFn, out var mrt))
                    return null;
                if (mrt.StartsWith("const[") && mrt.EndsWith("]"))
                    mrt = mrt.Substring(6, mrt.Length - 7);
                return ScalarWidthRank(mrt) > 0 ? mrt : null;
            }

            // `self._message = ""` is a string field, not a uint8 that a later
            // `self._message = message` (str) then contradicts. adafruit_character_lcd.
            case StringLiteral:
                return "str";

            // `self._gpio = bytearray(n)` is a buffer field. adafruit_74hc595.
            case CallExpr { Callee: VariableExpr { Name: "bytearray" or "bytes" } cv }:
                return cv.Name;

            case IntegerLiteral il:
                if (il.Value < -32768) return "int32";
                if (il.Value < -128) return "int16";
                if (il.Value > 65535) return "uint32";
                if (il.Value > 255) return "uint16";
                return null;

            // `self._active = False` is a bool field. Without this the write answers null
            // and the layout guesses a numeric byte, which then prints as 0/1 instead of
            // False/True through every path that consults the field's declared type.
            case BooleanLiteral:
                return "bool";

            case FloatLiteral:
                return "float";

            case VariableExpr ve:
            {
                string? d = paramTypes.TryGetValue(ve.Name, out var pv) ? pv
                          : localTypes.TryGetValue(ve.Name, out var lv2) ? lv2 : null;
                if (string.IsNullOrEmpty(d)) return null;
                if (d.StartsWith("const[") && d.EndsWith("]")) d = d.Substring(6, d.Length - 7);
                return ScalarWidthRank(d) > 0 ? d : null;
            }

            // `x = not p` / `x = a is not None` / `x = a < b`: the stored value is the
            // comparison's bool, not the operands' joined width -- returning that width
            // let ApplyInferredFieldType promote the field off "bool" again.
            case UnaryExpr { Op: Frontend.UnaryOp.Not }:
                return "bool";

            case BinaryExpr { Op: var cop } when cop is Frontend.BinaryOp.Equal
                or Frontend.BinaryOp.NotEqual or Frontend.BinaryOp.Less
                or Frontend.BinaryOp.LessEq or Frontend.BinaryOp.Greater
                or Frontend.BinaryOp.GreaterEq or Frontend.BinaryOp.Is
                or Frontend.BinaryOp.IsNot or Frontend.BinaryOp.In
                or Frontend.BinaryOp.NotIn:
                return "bool";

            case UnaryExpr ue:
                return InferAssignedFieldType(ue.Operand, paramTypes, localTypes, selfMethods);

            case BinaryExpr be:
            {
                string? l = InferAssignedFieldType(be.Left, paramTypes, localTypes, selfMethods);
                string? r = InferAssignedFieldType(be.Right, paramTypes, localTypes, selfMethods);
                if (l == null) return r;
                if (r == null) return l;
                return ScalarWidthRank(l) >= ScalarWidthRank(r) ? l : r;
            }

            default:
                return null;
        }
    }

    /// Refuses the two decorators a METHOD cannot honour, whatever path the method then takes.
    ///
    /// Both were silently dropped, and both are decorators whose entire purpose is to change the
    /// code generated for the function (PyMCU#229).
    ///
    ///   @interrupt  an ISR is entered by the hardware. There is no caller, so nothing writes
    ///               the leading parameters an instance method is compiled with -- the fields of
    ///               `self`. Emitting the vector entry anyway would trade a dropped flag for a
    ///               handler that reads uninitialised storage, which is the worse of the two. A
    ///               method with no `self` is a plain function in a class body and keeps working.
    ///
    ///   @extern     was not merely dropped. The symbol was never registered and the body WAS
    ///               compiled, so the call reached an empty PyMCU function and the C function was
    ///               never called at all. With `-> None` that program builds clean.
    ///
    /// Refusing is the minimum honest answer for @extern rather than the finished one: making it
    /// work needs the class path to register the symbol and skip the body, which is its own job.
    /// <summary>
    /// The two construction hooks PyMCU has no step to run them in.
    ///
    /// Both compiled clean and never executed: `__new__` returning a cached instance and
    /// `__init_subclass__` counting subclasses produced the same firmware as a program without
    /// them (#491). There is nothing to intercept. An instance is laid out in static storage
    /// with no allocation call, so `__new__` has no allocation to replace and no object to
    /// return instead; and a class is a compile-time layout with no class object and no
    /// creation event, so `__init_subclass__` has no moment to be called at.
    ///
    /// Refused where the method is written rather than implemented: measured across the stdlib,
    /// both compatibility layers and the whole AVR example and fixture corpus, not one file
    /// defines either, so nothing asks for them today and a refusal costs nothing, while a
    /// half-answer would be a trap.
    /// </summary>
    private void RefuseUnrunnableConstructionHooks(FunctionDef func, string className)
    {
        if (func.Name == "__new__")
            throw UserError(
                $"'{className}.__new__' is defined, but PyMCU never calls it: an instance is laid "
                + "out in static storage, there is no allocation step to intercept and no object "
                + "to hand back in its place. Move the work into __init__, or build the instance "
                + "in a module-level factory function that returns it.", func);

        if (func.Name == "__init_subclass__")
            throw UserError(
                $"'{className}.__init_subclass__' is defined, but PyMCU never calls it: a class is "
                + "a compile-time layout with no class object and no creation event to hook. Do "
                + "the work in each subclass's __init__, or at module level where the subclass is "
                + "declared.", func);
    }

    private void RefuseUnsupportedMethodDecorators(FunctionDef func, string className)
    {
        bool hasSelf = func.Params.Count > 0 && func.Params[0].Name == "self";

        if (func.IsInterrupt && hasSelf)
            throw UserError(
                $"'{className}.{func.Name}' is marked @interrupt, but an interrupt handler is "
                + "entered by the hardware, with no caller to pass `self`. Move it out of the "
                + $"class and pass what it reads as arguments (`def {func.Name}()` at module "
                + "level), or drop the decorator and call it from the handler.", func);

        if (func.IsExtern)
            throw UserError(
                $"'{className}.{func.Name}' is marked @extern, which is only supported on a "
                + "module-level function. In a class the symbol is not registered and the body is "
                + "compiled instead, so the call would reach an empty PyMCU function and "
                + $"'{func.ExternSymbol}' would never run. Declare it at module level and call it "
                + "from the method.", func);
    }

    /// Every function carrying a codegen decorator must have become a subroutine.
    ///
    /// Run for a module immediately after its own ScanFunctions, while currentSourcePath still
    /// names that module: the node's line belongs to the file being scanned, and a diagnostic
    /// that states one file's line against another file's name is a location that does not
    /// exist. `@naked` and `@interrupt` say what the compiler
    /// must emit for a function's entry and exit; a function that is EXPANDED into its call sites
    /// has no entry and no exit, so the decorator has nothing to act on and was being discarded
    /// without a word. Measured on five separate ways out of being a subroutine: a module-level
    /// `@inline`, an `@inline` method, a function taking a ZCA instance parameter, a single-field
    /// mutator that also returns, and a property accessor. All five compiled clean.
    ///
    /// Written against the RESULT (did this become a subroutine?) and not against the branches
    /// that decide it. Guarding each registry would have needed seventeen call sites to be found
    /// and kept in step, and missing one is exactly the defect being fixed.
    private void RefuseCodegenDecoratorsOnExpandedFunctions(ProgramNode ast)
    {
        foreach (var (func, owner) in FunctionsWithOwners(ast))
        {
            if (!func.IsNaked && !func.IsInterrupt) continue;
            if (compiledAsSubroutine.Contains(func)) continue;

            string what = func.IsNaked ? "@naked" : "@interrupt";
            string who = owner is null ? $"'{func.Name}'" : $"'{owner}.{func.Name}'";
            string why = func.IsNaked
                ? "suppresses the prologue and epilogue of a subroutine"
                : "installs a subroutine in an interrupt vector";
            throw UserError(
                $"{who} is marked {what}, but it is expanded into its callers instead of "
                + $"being compiled as a subroutine, and {what} {why}. An @inline function, a "
                + "property accessor and a function taking a class instance as a parameter "
                + $"are all expanded. Remove @inline (or the class parameter) so {who} is "
                + $"compiled once, or remove {what}.", func);
        }
    }

    /// <summary>
    /// The refusal for a call whose result was never produced: the callee reached the end of
    /// its body without returning, and the caller reads the value anyway.
    ///
    /// Python has None for the path that falls off the end. There is none here: the result of
    /// an @inline is a temporary the expansion copies its `return` into, and the result of a
    /// subroutine is a register. A body that reaches its end without returning leaves both
    /// untouched, and the caller reads whatever was in them.
    ///
    /// Measured (#302): an AVR PWM HAL whose `pwm_prescaler_for_freq(pin, freq) -> uint8` had
    /// lost its `return` compiled to `MOV R4, R16` -- R16 being the low byte of RAMEND, left
    /// there by the reset prologue -- and `PWM("PD6", 128, 500)` programmed TCCR0B = 0x3F
    /// instead of 3. The timer then ran off the T0 pin and the output never toggled. The
    /// firmware built clean, and the value was wrong by whatever the prologue happened to
    /// leave behind, which is the worst kind of wrong: it is stable, plausible and silent.
    ///
    /// Raised at the CALL, and only when the result is read. A call written as a statement
    /// discards the result, produces no wrong value, and is left alone -- which matters: the
    /// stdlib has accessors (`Pin.mode(m)`) whose no-argument path returns nothing and whose
    /// one-argument path is written as a statement everywhere it is used.
    /// </summary>
    private Exception UnproducedResultError(FunctionDef func)
    {
        return UserError(
            $"'{func.Name}' is declared to return {func.ReturnType} and this call reads the "
            + "value, but a path through its body reaches the end without returning one. "
            + "Python would hand back None; there is no None here, so this reads whatever the "
            + "register or stack slot happened to hold. Return a value on every path -- a "
            + "`match` needs a `case _:` arm and an `if` needs an `else:` -- or call it as a "
            + "statement and drop the return type.");
    }

    /// <summary>
    /// True when control cannot reach the end of this statement: every path through it either
    /// returns or raises. Answering "no" is always safe; answering "yes" wrongly would let a
    /// miscompile through, so every case here is one that can be shown.
    /// </summary>
    private static bool AlwaysLeaves(Statement? s) => s switch
    {
        null => false,
        ReturnStmt => true,
        RaiseStmt => true,
        // `continue`/`break` transfer control out of the enclosing block the same
        // way `return`/`raise` do -- an `if` arm that ends in one never reaches
        // the join, so its narrowed/None state must not merge there (read_pulses'
        // `if blocking and pulses is None: continue` narrows `pulses` on the
        // fall-through; the continue-arm's provable-None poisoned that join).
        ContinueStmt => true,
        BreakStmt => true,
        Block b => b.Statements.Any(AlwaysLeaves),
        // No `else` means the condition being false walks straight past the statement.
        IfStmt ifs => ifs.ElseBranch != null
                      && AlwaysLeaves(ifs.ThenBranch)
                      && ifs.ElifBranches.All(e => AlwaysLeaves(e.Body))
                      && AlwaysLeaves(ifs.ElseBranch),
        // A `match` with no arm for the subject falls through, so a wildcard is required.
        MatchStmt m => m.Branches.Any(IsCatchAll)
                       && m.Branches.All(c => AlwaysLeaves(c.Body)),
        // `while True:` is left only by a `return` or a `raise` -- unless a `break` leaves it.
        WhileStmt w => IsAlwaysTrue(w.Condition) && !HasOwnBreak(w.Body),
        WithStmt wi => AlwaysLeaves(wi.Body),
        // The handlers are what runs when the body does not finish, so both halves have to
        // leave; a `finally` that leaves settles it on its own.
        TryStmt t => (t.Finally != null && t.Finally.Any(AlwaysLeaves))
                     || (t.Body.Any(AlwaysLeaves)
                         && t.Handlers.All(h => h.Handler.Any(AlwaysLeaves))
                         && (t.ElseBody == null || t.ElseBody.Any(AlwaysLeaves))),
        // A `for` can run zero times, so its body proves nothing about the path around it.
        _ => false
    };

    /// A `case _:` or a bare capture pattern, either of which matches whatever is left.
    private static bool IsCatchAll(CaseBranch c) =>
        c.Guard == null && (c.Pattern == null || c.CaptureName.Length > 0);

    private static bool IsAlwaysTrue(Expression? cond) => cond switch
    {
        BooleanLiteral b => b.Value,
        IntegerLiteral n => n.Value != 0,
        _ => false
    };

    /// A `break` that leaves THIS loop: one inside a nested loop belongs to that one.
    private static bool HasOwnBreak(Statement? s) => s switch
    {
        null => false,
        BreakStmt => true,
        Block b => b.Statements.Any(HasOwnBreak),
        IfStmt ifs => HasOwnBreak(ifs.ThenBranch)
                      || ifs.ElifBranches.Any(e => HasOwnBreak(e.Body))
                      || HasOwnBreak(ifs.ElseBranch),
        MatchStmt m => m.Branches.Any(c => HasOwnBreak(c.Body)),
        WithStmt wi => HasOwnBreak(wi.Body),
        TryStmt t => t.Body.Any(HasOwnBreak)
                     || t.Handlers.Any(h => h.Handler.Any(HasOwnBreak))
                     || (t.ElseBody?.Any(HasOwnBreak) ?? false)
                     || (t.Finally?.Any(HasOwnBreak) ?? false),
        _ => false
    };

    /// Every FunctionDef a module declares, paired with the class that owns it, or null.
    private static IEnumerable<(FunctionDef Func, string? Owner)> FunctionsWithOwners(ProgramNode ast)
    {
        foreach (var fn in ast.Functions)
            yield return (fn, null);
        foreach (var st in ast.GlobalStatements)
            foreach (var pair in FunctionsOfClass(st))
                yield return pair;
    }

    private static IEnumerable<(FunctionDef Func, string? Owner)> FunctionsOfClass(Statement st)
    {
        if (st is not ClassDef cls || cls.Body is not Block body) yield break;
        foreach (var member in body.Statements)
        {
            if (member is FunctionDef fn) yield return (fn, cls.Name);
            // Nested classes declare methods of their own, and they reach the same registries.
            foreach (var pair in FunctionsOfClass(member)) yield return pair;
        }
    }

    // RFC 0001 F1-F3: register a method as an outlined shared subroutine. >= 2 fields ->
    // Model B SRAM slot (self pointer + BytearrayLoad offsets); else Model A (one param per
    // field). Used by both the explicit @outline branch and the F4 default (an outline-safe
    // undecorated method).
    private void RegisterOutlinedMethod(FunctionDef func, string classKey,
        List<(string Field, string Type, string SourceParam)> layout, string fullName,
        IReadOnlyDictionary<string, FunctionDef>? siblings = null)
    {
        var synthParams = new List<Param>();
        if (layout.Count >= 2) slotClasses.Add(classKey);
        // A single-field method that BOTH writes its field and returns a value cannot be
        // Model A: the field travels by value and the one return slot already carries the
        // returned expression, so the write has nowhere to come back through and is lost.
        // `@outline def bump(self) -> uint8: self.a = self.a + 1; return self.a` returned the
        // right number and left the instance holding the old one. Give the class a slot so the
        // body writes through a pointer instead.
        else if (siblings != null
                 && siblings.Values.Any(m => MethodHasReturnStmt(m)
                        && layout.Any(f => MethodMutatesField(m, f.Field, siblings))))
            slotClasses.Add(classKey);
        if (slotClasses.Contains(classKey))
        {
            synthParams.Add(new Param("self", "bytearray"));
            var offsets = new Dictionary<string, int>();
            int off = 0;
            foreach (var (fld, ty, _) in layout)
            {
                offsets[fld] = off;
                off += SlotFieldFootprint(classKey, fld, ty);
            }
            slotMethods.Add(fullName);
            slotMethodFieldOffsets[fullName] = offsets;
        }
        else
        {
            foreach (var (fld, ty, _) in layout)
            {
                var selfParam = new Param("self_" + fld, ty);
                // RFC 0009 phase 2/3: a union field arrives as payload + tag, so the
                // self_<field> parameter carries the field's member list (declared or
                // evidence) for ResolveOptionalParams to tag -- the reader as well as
                // the mutator reads `self.<field> is None` off that byte.
                if (IsUnionField(classKey, fld, out var selfUf))
                    selfParam.UnionMembers = selfUf != null
                        ? new List<string>(selfUf)
                        : EvidenceUnionMembers(classKey, fld);
                synthParams.Add(selfParam);
            }
        }
        for (int pi = 1; pi < func.Params.Count; ++pi)
            synthParams.Add(func.Params[pi]);

        // RFC 0001 (write-back): a single-field (Model A) method that mutates its field but
        // never returns a value loses the mutation, because the field is passed BY VALUE.
        // Rewrite the shared body to RETURN the (updated) field and record the field so the
        // call site copies it back to the instance. The caller routes mutators that have
        // explicit returns to force-inline instead, so here the body always falls through:
        // appending a single `return self.<field>` is sufficient.
        Block body = func.Body;
        string returnType = func.ReturnType;
        if (!slotClasses.Contains(classKey) && layout.Count == 1
            && MethodMutatesField(func, layout[0].Field, siblings) && !MethodHasReturnStmt(func))
        {
            var (field, ftype, _) = layout[0];
            body = new Block();
            body.Statements.AddRange(func.Body.Statements);
            body.Statements.Add(new ReturnStmt(new MemberAccessExpr(new VariableExpr("self"), field)));
            returnType = ftype;
            outlineWriteBack[fullName] = (field, DataTypeExtensions.StringToDataType(ftype));
            if (!zcaWriteBackFields.TryGetValue(classKey, out var wf))
                zcaWriteBackFields[classKey] = wf = new HashSet<string>();
            wf.Add(field);

            // RFC 0009 phase 2/3: the write-back field being a tagged union makes the
            // self_<field> parameter an Optional argument -- ResolveOptionalParams then
            // tags it like any declared one (payload + tag, the phase-2 argument-run
            // order) -- and the appended `return self.<field>` a tagged return, so the
            // caller's `inst_<field>$tag` stores stay in step with the payload the
            // plain write-back already carried.
            if (IsUnionField(classKey, field, out var wbDeclared))
            {
                List<string> wbMembers = wbDeclared != null
                    ? new List<string>(wbDeclared)
                    : EvidenceUnionMembers(classKey, field);
                synthParams[0].UnionMembers = wbMembers;
                functionReturnMembers[fullName] = wbMembers;
            }
        }

        // The stand-in carries the position of the method it stands for. It is not a synthetic
        // function: the user wrote it, and every diagnostic raised while lowering it is about
        // their `def`. Leaving the position at 0 did not just withhold the caret, it moved the
        // LINE: `UserError(msg, node)` falls back to `lastLine` when the node has no column, so
        // a missing return in an outlined method was reported at the last statement of the
        // body instead of at the `def` -- a line that reads as plausible and is wrong, which is
        // worse than no line at all.
        //
        // Position only. The other fields this stand-in does not copy (IsAsync, IsNaked,
        // IsExtern, the property flags) are a separate question and are left as they were.
        var synth = new FunctionDef(func.Name, synthParams, returnType, body, isInline: false)
        {
            // The `def` keyword, which is where Parser stamps a FunctionDef; see the decision
            // table above Located() in Parser.cs.
            Line = func.Line, Column = func.Column, Length = func.Length,
            // @naked travels with the body. An outlined method IS a subroutine that takes its
            // instance's fields as leading parameters, which is the same shape as a module-level
            // `@naked def f(n)` -- a form that has always worked. Dropping it here meant the
            // prologue the decorator exists to suppress was emitted anyway, and nothing said so:
            // a corrupted context switch out of a decorator that looked applied (PyMCU#229).
            //
            // @interrupt is NOT carried, and that is a decision rather than an omission: it is
            // refused earlier, on the method, because a vector entry into a body that reads
            // parameters no caller ever writes is worse than the dropped flag.
            IsNaked = func.IsNaked,
            // RFC 0009: `-> Union[...]`/`-> Optional[...]` on the user method rides the synth
            // into functionsToCompile, where DecideOptionalReturns reads it off Func -- without
            // it the shared body returned the payload bare and the call site got no tag.
            ReturnMembers = func.ReturnMembers,
            ReturnMembersInferred = func.ReturnMembersInferred,
        };
        compiledAsSubroutine.Add(func);
        functionsToCompile.Add(new FunctionEntry
            { Prefix = currentModulePrefix, Func = synth, SourceFile = currentSourceFile, SourcePath = currentSourcePath });

        outlinedMethods.Add(fullName);
        outlineFieldLayout[fullName] = layout;
        functionReturnTypes[fullName] = returnType;
        // The write-back rewrite appends `return self.<field>`: that body returns a value.
        if (!outlineWriteBack.ContainsKey(fullName) && ReturnsOnlyNone(func))
            noneReturningFunctions.Add(fullName);
        else
            noneReturningFunctions.Remove(fullName);
        if (!outlineWriteBack.ContainsKey(fullName) && SelfFieldsReturned(func) is { } rf)
            outlinedSelfFieldReturns[fullName] = rf;
        functionParams[fullName] = synthParams.Select(p => p.Name).ToList();
        // The leading self-derived parameters ("self" or one self_<field> per layout
        // field) are not user arguments: a call site's arg index for param p is
        // p minus this count, which the union-parameter scan needs to line them up.
        functionParamSelfCount[fullName] = synthParams.Count - (func.Params.Count - 1);
        // Aligned with synthParams (the leading self_<field> ones included), so the call site
        // can index them by position when an argument is omitted.
        functionParamDefaults[fullName] = synthParams.Select(p => p.DefaultValue).ToList();
        functionParamTypes[fullName] = synthParams.Select(p => ParamStorageType(synth, p.Type)).ToList();
        NoteStrParamSlots(fullName, synth, synthParams);
        RecordSlotDef(fullName, func);
    }

    // RFC 0001 F4: is a method safe to outline (compile once, share) instead of force-inline?
    // Safe iff its body touches `self` only as `self.<field>` where <field> is a derivable data
    // field -- never `self.<method>()` and never bare `self` (passed as a value). Any unrecognized
    // node makes it UNSAFE: outlining must be provably correct, otherwise we keep the existing
    // force-inline behavior (zero regression). Methods with user params besides self stay safe
    // (those params become trailing params of the shared body).
    /// <summary>
    /// The fields a class assigns a compile-time table to: a list of constructions, a
    /// comprehension of them, a dict or a set literal. Read off the class body, because the
    /// bindings that record them do not exist yet when the outline decision is made.
    /// </summary>
    private static HashSet<string> CompileTimeTableFields(Block classBody)
    {
        var fields = new HashSet<string>();

        // The shared walk does not descend into defs; method bodies are the whole
        // point of the scan, so they are walked explicitly. It also reaches the
        // arms the old recursion skipped: try/with/match bodies.
        foreach (var st in TypeInference.WalkStatements(classBody))
        {
            switch (st)
            {
                case FunctionDef fd:
                    foreach (var inner in TypeInference.WalkStatements(fd.Body))
                        if (inner is AssignStmt { Target: MemberAccessExpr
                                { Object: VariableExpr { Name: "self" } } m2 } a2
                            && a2.Value is DictExpr or SetExpr or ListExpr or ListCompExpr)
                            fields.Add(m2.Member);
                    break;
                case AssignStmt { Target: MemberAccessExpr { Object: VariableExpr { Name: "self" } } m } a
                    when a.Value is DictExpr or SetExpr or ListExpr or ListCompExpr:
                    fields.Add(m.Member);
                    break;
            }
        }

        return fields;
    }

    /// The fields of <paramref name="classKey"/> that are known to hold another instance.
    /// Null when there are none, so the common class costs nothing.
    private HashSet<string>? InstanceFieldsOf(string classKey)
    {
        HashSet<string>? found = null;
        string prefix = classKey + "|";
        foreach (var key in fieldClasses.Keys)
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                (found ??= new HashSet<string>(StringComparer.Ordinal)).Add(key[prefix.Length..]);
        return found;
    }

    private bool IsOutlineSafe(FunctionDef method,
        List<(string Field, string Type, string SourceParam)> layout,
        HashSet<string>? compileTimeFields = null,
        HashSet<string>? instanceFields = null)
    {
        if (layout.Count == 0) return false;

        // Descriptor protocol (#419): `__get__`/`__set__` receive `obj` as the owning
        // instance, whose class is only known at the call site. A body compiled once
        // would keep the typing-only placeholder (`I2CDeviceDriver`) and refuse a genuine
        // read of `obj.i2c_device`. Expand at each site so the rewrite can substitute.
        if (method.Name is "__get__" or "__set__") return false;

        // `*args` and `**kwargs` stand for what the CALL SITE wrote beyond the declaration,
        // so a body compiled once and shared between call sites has nothing to bind them to.
        // The method has to be expanded where it is called (#368).
        if (method.Params.Any(p => p.IsVarArg || p.IsKwArg)) return false;

        // A method returning a tuple has nothing to return THROUGH: the outlined body is a
        // real subroutine and a tuple is a compile-time sequence with no ABI slot. Refusing
        // the outline keeps the force-inline path, which is the only one that can bind the
        // caller's unpack targets (`c, r, g, b = self._read_4u16(reg)` in adafruit_tcs34725).
        // Without this the shared body was emitted anyway and its `return (a, b)` was refused
        // at the `def`, far from the call that needed the values.
        if (TupleType.ElementTypes(method.ReturnType).Count > 0) return false;

        // A field whose type is not a scalar is another ZCA instance (e.g. a Pin
        // stored as `self.pin`). An outlined body shares one copy across instances
        // by passing each field as a runtime parameter, but a ZCA field is
        // compile-time per-instance (a Pin is just its const pin name, no runtime
        // value) — it cannot be passed as a parameter. Such methods must stay
        // force-inlined so `self.pin.<method>()` resolves at each call site.
        var scalarTypes = new HashSet<string>
            { "uint8", "int8", "uint16", "int16", "uint32", "int32", "float", "bool" };
        if (layout.Any(f => !scalarTypes.Contains(f.Type))) return false;

        // A return that is another instance has no ABI slot in a shared body.
        // get_pin() -> DigitalInOut must expand at the call site so the result
        // keeps that class when it is passed straight into Character_LCD.__init__.
        // ClassKeyFromAnnotation is empty when the returned class has not been
        // scanned yet (mcp23008.get_pin is visited before digital_inout.py);
        // a capitalized or dotted name that is not a known scalar is still a class.
        string rt = (method.ReturnType ?? "").Trim().Trim('"');
        var returnScalars = new HashSet<string>(scalarTypes)
            { "int", "str", "bytes", "bytearray", "memoryview", "None", "void", "" };
        // RFC 0009: a `-> Union[...]`/`-> Optional[...]` return is payload-plus-tag over scalar
        // members -- the shared body's ABI already carries it via functionReturnMembers, so it
        // is as returnable as a bare scalar. The capital-U spelling used to read as a class
        // name here, which kept every union-returning method force-inline; on a slot-class
        // receiver (`arr[i].m()`) that left the call site emitting a `call` to a function that
        // was never generated.
        if (method.ReturnMembers is { Count: > 0 } rmSafe)
        {
            if (rmSafe.Any(m => m != "None" && m != "int" && !scalarTypes.Contains(m)))
                return false;
        }
        else if (!returnScalars.Contains(rt))
        {
            int nameStart = rt.LastIndexOf('.') + 1;
            if (ClassKeyFromAnnotation(rt) != null
                || (nameStart < rt.Length && char.IsUpper(rt[nameStart])))
                return false;
        }

        // A PARAMETER that is another instance cannot be passed either, for the same reason a
        // ZCA field cannot: the instance is compile-time per-instance, not a runtime value a
        // shared body can receive. `def read(self, o: C) -> uint8: return self.n + o.n` was
        // outlined anyway, `self` arrived and `o` did not, and the method answered with the
        // other operand missing -- 7 where 8 was right. Free functions with an instance
        // parameter were already routed to expansion (#71, #72); methods were left out.
        for (int pi = 1; pi < method.Params.Count; ++pi)
        {
            string pt = method.Params[pi].Type;
            if (string.IsNullOrEmpty(pt) || scalarTypes.Contains(pt)) continue;
            // Dotted annotations (`digitalio.DigitalInOut`) are not keys of
            // classFieldLayout; ClassKeyFromAnnotation is the same lookup VisitFunction
            // uses, so Character_LCD.__init__ is not outlined as a shared body.
            if (ClassKeyFromAnnotation(pt) != null) return false;
        }

        var fields = new HashSet<string>(layout.Select(f => f.Field));
        bool safe = true;

        void E(Expression? e)
        {
            if (!safe || e == null) return;
            switch (e)
            {
                case MemberAccessExpr ma when ma.Object is VariableExpr sv && sv.Name == "self":
                    if (!fields.Contains(ma.Member)) safe = false; // self.method() or non-field
                    // A field holding a compile-time table has no run-time value to pass, so a
                    // shared body cannot reach it.
                    if (compileTimeFields != null && compileTimeFields.Contains(ma.Member)) safe = false;
                    // Nor can a field that holds another INSTANCE. `self.field.<anything>` is
                    // already refused below, and a BARE read of the same field was not: the
                    // layout types such a field uint8 because __init__ constructs it, so the
                    // scalar check above cannot see it either. An outlined body then received
                    // the field as a number and `self` did not exist in it at all -- its
                    // parameter is `self__field` -- so anything the body later did WITH the
                    // instance had no receiver to resolve against (#385).
                    if (instanceFields != null && instanceFields.Contains(ma.Member)) safe = false;
                    return; // do NOT descend into the `self` leaf -- it is a field access
                // `self.<field>.<anything>` -- a member reached THROUGH a field, so the field is
                // another instance, not the scalar an outlined body would take as a parameter.
                // The layout types such a field uint8 when it is constructed in __init__, so the
                // scalar check above cannot catch it; without this the body compiled standalone
                // and `self._pin.value()` mangled into a call on the field's own name.
                case MemberAccessExpr { Object: MemberAccessExpr { Object: VariableExpr { Name: "self" } } }:
                    safe = false; return;
                case MemberAccessExpr ma2: E(ma2.Object); return;
                case VariableExpr ve: if (ve.Name == "self") safe = false; return; // bare self
                case BinaryExpr b: E(b.Left); E(b.Right); return;
                case UnaryExpr u: E(u.Operand); return;
                // self.method(args): a sibling-method call. Allowed in an outlined body —
                // it lowers to a call that forwards this method's own self (field params
                // or slot pointer). Validate only the args, not the self.<method> callee.
                // LIMITATION: the outlined body is compiled once with `self` bound to the
                // DEFINING class, so this self-call binds statically to that class's version.
                // If the sibling is overridden in a subclass, virtual dispatch does NOT happen
                // (Shape.total() calling self.unit() always runs Shape.unit). See the codegen
                // backlog (virtual-dispatch-via-outlined-self-call) -- fixing it needs a
                // post-scan, override-aware force-inline of just the virtual cases, preserving
                // shared outlining for non-overridden sibling calls.
                case CallExpr { Callee: MemberAccessExpr { Object: VariableExpr { Name: "self" } } } selfCall:
                    foreach (var a in selfCall.Args) E(a);
                    return;
                // Base.method(self, args): the unbound spelling of a base-class call, the same
                // construct `super().method(args)` spells. Its leading `self` is the RECEIVER,
                // not a value being passed: the base body reaches the very fields this method
                // already has, exactly as it does through super(). Reading it as a bare self
                // passed by value is what refused outlining for this spelling alone, so it was
                // force-inlined at every call site while super() emitted one shared subroutine.
                // For a base method with control flow that was worth up to 130% of program size
                // (PyMCU#160). Validate the remaining arguments, as the self-call case does.
                case CallExpr { Callee: MemberAccessExpr { Object: VariableExpr baseVe } } baseCall
                        when classNames.Contains(baseVe.Name)
                             && baseCall.Args.Count > 0
                             && baseCall.Args[0] is VariableExpr { Name: "self" }:
                    for (int ai = 1; ai < baseCall.Args.Count; ++ai) E(baseCall.Args[ai]);
                    return;
                case CallExpr c: E(c.Callee); foreach (var a in c.Args) E(a); return;
                case KeywordArgExpr kw: E(kw.Value); return;
                case IndexExpr ix: E(ix.Target); E(ix.Index); return;
                case TernaryExpr t: E(t.Condition); E(t.TrueVal); E(t.FalseVal); return;
                case TupleExpr tu: foreach (var el in tu.Elements) E(el); return;
                case ListExpr le: foreach (var el in le.Elements) E(el); return;
                case IntegerLiteral: case FloatLiteral: case BooleanLiteral:
                case StringLiteral: return;
                default: safe = false; return; // conservative: unknown node -> not outline-safe
            }
        }

        // The shared walk yields every nested statement; the constructs this check
        // never learned (for/try/with/match/nested def/class) keep the conservative
        // answer the old default gave them: not outline-safe.
        foreach (var st in TypeInference.WalkStatements(method.Body.Statements))
        {
            if (!safe) break;
            switch (st)
            {
                case VarDecl vd: E(vd.Init); break; // typed local decl: `x: T = expr`
                case AnnAssign a: E(a.Value); break;
                case AssignStmt asg: E(asg.Target); E(asg.Value); break;
                case AugAssignStmt aug: E(aug.Target); E(aug.Value); break;
                // `return a, b` is a tuple the shared subroutine cannot carry back -- the
                // declared-return check above says the same for the annotated spelling.
                case ReturnStmt r when r.Value is TupleExpr: safe = false; break;
                case ReturnStmt r: E(r.Value); break;
                case ExprStmt ex: E(ex.Expr); break;
                case IfStmt iff:
                    E(iff.Condition);
                    foreach (var br in iff.ElifBranches) E(br.Condition);
                    break;
                case WhileStmt wh: E(wh.Condition); break;
                case BreakStmt: case ContinueStmt: case PassStmt: break;
                case Block: break;
                // A raise carries no self access of its own -- the SignalError it lowers to
                // propagates out of the shared body through the ordinary T-flag protocol, the
                // same way it escapes any other called function (CanFailAnalyzer marks the
                // subroutine CanFail and every caller emits BranchOnError). busio.I2C.writeto
                // is the shape that needs this: a NACK raises OSError, and a per-site expansion
                // of the check multiplied a display driver by three. Only the dynamic-message
                // expression can hold a `self` reference, so it is the one part to validate.
                // The exception is `raise CompileError(...)`: a refusal stub that exists to
                // fire at LOWERING time, and outlined bodies are lowered whether or not anyone
                // calls them -- a safe one would refuse every program that merely imports the
                // module (busio.UART.read is one). It stays inline-only, dormant until called.
                case RaiseStmt r when r.ErrorType == "CompileError": safe = false; break;
                case RaiseStmt r: E(r.MessageExpr); break;
                default: safe = false; break; // conservative
            }
        }

        return safe;
    }

    // True if the method assigns `self.<field>` anywhere (a plain or augmented assignment).
    // Such a method mutates instance state; if its single field is passed by value it loses
    // the mutation unless we write it back (see RegisterOutlinedMethod).
    // A fixed-array parameter type like `uint8[4]` / `int16[10]`: a scalar element type followed
    // by a bracketed size. `const[...]`, `ptr[...]`, `list[...]`, `bytearray` and class names are
    // NOT this shape. Such a type is valid for a local/field but not for a by-value parameter.
    private static bool IsFixedArrayParamType(string? type)
    {
        if (string.IsNullOrEmpty(type)) return false;
        int lb = type.IndexOf('[');
        if (lb <= 0 || !type.EndsWith("]")) return false;
        string elem = type.Substring(0, lb);
        if (elem is not ("uint8" or "int8" or "uint16" or "int16" or "uint32" or "int32"
                         or "float" or "bool")) return false;
        string inner = type.Substring(lb + 1, type.Length - lb - 2);
        return inner.Length > 0 && inner.All(char.IsDigit);
    }

    /// <summary>Same question as MethodMutatesField, for callers outside the class scan.</summary>
    private static bool MethodMutatesFieldPublic(FunctionDef method, string field)
        => MethodMutatesField(method, field);

    private static bool MethodMutatesFieldPublic(FunctionDef method, string field,
        IReadOnlyDictionary<string, FunctionDef>? siblings)
        => MethodMutatesField(method, field, siblings);

    // The mutation may also be indirect: `bump()` writing nothing itself but calling
    // `self.inc()`, which does. The field travels by value, so an indirect mutator needs the
    // same write-back as a direct one -- without it the sibling updated a copy and the
    // increment vanished. `siblings` maps the enclosing class body's method names to their
    // ASTs; a self-call naming a method that is not there (inherited, or defined elsewhere)
    // counts as mutating, since assuming otherwise would silently drop a write.
    private static bool MethodMutatesField(FunctionDef method, string field,
        IReadOnlyDictionary<string, FunctionDef>? siblings = null)
    {
        static bool IsSelfField(Expression e, string fld) =>
            e is MemberAccessExpr ma && ma.Member == fld
            && ma.Object is VariableExpr sv && sv.Name == "self";

        var visiting = new HashSet<string>();
        bool Walk(FunctionDef fn)
        {
            bool found = false;

            void E(Expression? e)
            {
                if (found || e == null) return;
                switch (e)
                {
                    case CallExpr { Callee: MemberAccessExpr { Object: VariableExpr { Name: "self" } } sc } selfCall:
                        foreach (var a in selfCall.Args) E(a);
                        if (found) return;
                        if (siblings == null || !siblings.TryGetValue(sc.Member, out var sib))
                        {
                            found = true;   // unknown sibling: assume it writes the field
                            return;
                        }
                        if (visiting.Add(sc.Member))
                        {
                            found = Walk(sib);
                            visiting.Remove(sc.Member);
                        }
                        return;
                    case CallExpr c: E(c.Callee); foreach (var a in c.Args) E(a); return;
                    case MemberAccessExpr ma: E(ma.Object); return;
                    case BinaryExpr b: E(b.Left); E(b.Right); return;
                    case UnaryExpr u: E(u.Operand); return;
                    case KeywordArgExpr kw: E(kw.Value); return;
                    case IndexExpr ix: E(ix.Target); E(ix.Index); return;
                    case TernaryExpr t: E(t.Condition); E(t.TrueVal); E(t.FalseVal); return;
                    case TupleExpr tu: foreach (var el in tu.Elements) E(el); return;
                    case ListExpr le: foreach (var el in le.Elements) E(el); return;
                }
            }

            // The shared walk reaches the arms the old recursion skipped: a
            // `self.field = ...` inside try/with/match is the same write.
            foreach (var st in TypeInference.WalkStatements(fn.Body.Statements))
            {
                if (found) break;
                switch (st)
                {
                    case AssignStmt asg when IsSelfField(asg.Target, field): found = true; break;
                    case AugAssignStmt aug when IsSelfField(aug.Target, field): found = true; break;
                    case AssignStmt asg2: E(asg2.Value); break;
                    case AugAssignStmt aug2: E(aug2.Value); break;
                    case VarDecl vd: E(vd.Init); break;
                    case AnnAssign an: E(an.Value); break;
                    case ReturnStmt r: E(r.Value); break;
                    case ExprStmt ex: E(ex.Expr); break;
                    case IfStmt iff:
                        E(iff.Condition);
                        foreach (var br in iff.ElifBranches) E(br.Condition);
                        break;
                    case WhileStmt wh: E(wh.Condition); break;
                    case ForStmt fr:
                        E(fr.Iterable); E(fr.RangeStart); E(fr.RangeStop); E(fr.RangeStep);
                        break;
                    case WithStmt wi: E(wi.ContextExpr); break;
                    case MatchStmt m:
                        E(m.Target);
                        foreach (var br in m.Branches) { E(br.Pattern); E(br.Guard); }
                        break;
                }
            }
            return found;
        }

        return Walk(method);
    }

    /// <summary>
    /// True when the function grows a buffer that is not one of its own parameters, by calling
    /// <c>.extend()</c> on a name from an enclosing scope (PyMCU#362).
    ///
    /// How many bytes such a call adds decides that buffer's SIZE, and a buffer's size is fixed
    /// while compiling: there is no allocator to grow one at run time. So the body only has
    /// meaning expanded where the number of bytes is known, which is the same property a ZCA
    /// parameter and a variadic parameter have, and it is registered the same way.
    ///
    /// A receiver that IS a parameter is excluded: the buffer then belongs to the caller, the
    /// call reaches it by reference, and the size question is the caller's.
    /// </summary>
    private static bool FunctionGrowsAnOuterBuffer(FunctionDef func)
    {
        var ownNames = new HashSet<string>(func.Params.Select(p => p.Name), StringComparer.Ordinal);
        bool found = false;

        void E(Expression? e)
        {
            if (found || e == null) return;
            switch (e)
            {
                case CallExpr { Callee: MemberAccessExpr { Member: "extend", Object: VariableExpr bv } }
                    when !ownNames.Contains(bv.Name):
                    found = true;
                    return;
                case CallExpr c: E(c.Callee); foreach (var a in c.Args) E(a); return;
                case MemberAccessExpr ma: E(ma.Object); return;
                case BinaryExpr b: E(b.Left); E(b.Right); return;
                case UnaryExpr u: E(u.Operand); return;
                case KeywordArgExpr kw: E(kw.Value); return;
                case IndexExpr ix: E(ix.Target); E(ix.Index); return;
                case TernaryExpr t: E(t.Condition); E(t.TrueVal); E(t.FalseVal); return;
                case TupleExpr tu: foreach (var el in tu.Elements) E(el); return;
                case ListExpr le: foreach (var el in le.Elements) E(el); return;
            }
        }

        // The shared walk reaches the arms the old recursion skipped: an extend
        // call inside try/with/match still grows the buffer.
        foreach (var st in TypeInference.WalkStatements(func.Body.Statements))
        {
            if (found) break;
            switch (st)
            {
                case AssignStmt asg: E(asg.Value); break;
                case AugAssignStmt aug: E(aug.Value); break;
                case VarDecl vd: E(vd.Init); break;
                case AnnAssign an: E(an.Value); break;
                case ReturnStmt r: E(r.Value); break;
                case ExprStmt ex: E(ex.Expr); break;
                case IfStmt iff:
                    E(iff.Condition);
                    foreach (var br in iff.ElifBranches) E(br.Condition);
                    break;
                case WhileStmt wh: E(wh.Condition); break;
                case ForStmt fr:
                    E(fr.Iterable); E(fr.RangeStart); E(fr.RangeStop); E(fr.RangeStep);
                    break;
                case WithStmt wi: E(wi.ContextExpr); break;
                case MatchStmt m:
                    E(m.Target);
                    foreach (var br in m.Branches) { E(br.Pattern); E(br.Guard); }
                    break;
            }
        }
        return found;
    }

    // True if the method contains any return statement (value-returning or bare). Write-back
    // via return is applied only to fall-through void mutators; a mutator with explicit
    // returns can't carry both a value and the field in one return slot, so it is force-inlined.
    private static bool MethodHasReturnStmt(FunctionDef method)
    {
        // The shared walk reaches the arms the old recursion skipped: a return
        // inside try/with/match is still a return.
        return TypeInference.WalkStatements(method.Body.Statements).Any(s => s is ReturnStmt);
    }

    // The single sequence name an outlined function's returns agree on, or null:
    // `return tuple(v)` / `return list(v)` / `return v` all answer "v". A name the
    // body assigns is a local the caller cannot resolve, and returns naming
    // different sequences have no one answer -- both refuse by answering null.
    /// <summary>
    /// True when every value a function returns is a `chr(...)` call, so the byte it hands
    /// back stands for a CHARACTER and not for a number. A char IS its byte on this target,
    /// so the value cannot say which it is, and the one site that ever wrote a character
    /// recognised the `chr(...)` CALL by its syntax -- which a `return` hides, so the caller
    /// printed the code point (#436). At least one return with a value, none without one,
    /// and no path that falls off the end: on such a path the function hands back whatever
    /// the return register held, which is not a character.
    /// </summary>
    // `def ret(): return base > k`: every value the function hands back is a truth value,
    // so the call prints True/False as CPython does. Recorded beside the chars for the same
    // reason: a caller is often lowered before the callee's body (#386).
    private static bool ReturnsOnlyBools(FunctionDef func)
    {
        if (func.ReturnType == "bool") return true;
        if (func.IsAsync || func.ReturnMembers != null) return false;
        var returns = TypeInference.WalkStatements(func.Body).OfType<ReturnStmt>().ToList();
        if (returns.Count == 0 || !AlwaysLeaves(func.Body)) return false;
        return returns.All(r => r.Value != null && IsBoolShaped(r.Value));
    }

    /// <summary>
    /// A value that is a Python bool by its shape alone: True/False, a comparison, `not`, a
    /// truth builtin, or `and`/`or` of such values (which return one of their bool operands).
    /// Decidable before any lowering, which is what a scan needs.
    /// </summary>
    internal static bool IsBoolShaped(Expression e) => e switch
    {
        BooleanLiteral => true,
        UnaryExpr { Op: PyMCU.Frontend.UnaryOp.Not } => true,
        BinaryExpr { Op: PyMCU.Frontend.BinaryOp.And or PyMCU.Frontend.BinaryOp.Or } lo =>
            IsBoolShaped(lo.Left) && IsBoolShaped(lo.Right),
        BinaryExpr { Op: var op } => op is PyMCU.Frontend.BinaryOp.Equal
            or PyMCU.Frontend.BinaryOp.NotEqual or PyMCU.Frontend.BinaryOp.Less
            or PyMCU.Frontend.BinaryOp.LessEq or PyMCU.Frontend.BinaryOp.Greater
            or PyMCU.Frontend.BinaryOp.GreaterEq or PyMCU.Frontend.BinaryOp.Is
            or PyMCU.Frontend.BinaryOp.IsNot or PyMCU.Frontend.BinaryOp.In
            or PyMCU.Frontend.BinaryOp.NotIn,
        CallExpr { Callee: VariableExpr { Name: var fn } } => IsTruthBuiltin(fn),
        _ => false,
    };

    internal static bool IsTruthBuiltin(string name) =>
        name is "bool" or "isinstance" or "issubclass" or "all" or "any" or "hasattr"
            or "callable";

    private static bool ReturnsOnlyChars(FunctionDef func)
    {
        var returns = TypeInference.WalkStatements(func.Body).OfType<ReturnStmt>().ToList();
        if (returns.Count == 0 || !AlwaysLeaves(func.Body)) return false;
        return returns.All(r =>
            r.Value is CallExpr { Callee: VariableExpr { Name: "chr" }, Args.Count: 1 });
    }

    /// <summary>
    /// True when a function hands back None on every path it can take: each `return` is
    /// bare, `return None`, or returns a local the body binds to nothing but None, and
    /// reaching the end is the implicit `return None`. Only an undeclared or None-declared
    /// result qualifies -- a declared width is a promise the refusals elsewhere enforce --
    /// and an async body hands back a coroutine, not its return value.
    /// </summary>
    private static bool ReturnsOnlyNone(FunctionDef func)
    {
        if (func.IsAsync || func.IsExtern || func.ReturnMembers != null) return false;
        if (func.ReturnType is not ("" or "void" or "None")) return false;

        var noneOnly = new HashSet<string>();
        var otherwise = new HashSet<string>(func.Params.Select(p => p.Name));
        bool opaque = false;
        foreach (var s in TypeInference.WalkStatements(func.Body))
        {
            switch (s)
            {
                case AssignStmt { Target: VariableExpr av } a:
                    (a.Value is NoneLiteral ? noneOnly : otherwise).Add(av.Name);
                    break;
                case AugAssignStmt { Target: VariableExpr gv }: otherwise.Add(gv.Name); break;
                case AnnAssign an: otherwise.Add(an.Target); break;
                case VarDecl vd:
                    (vd.Init is NoneLiteral ? noneOnly : otherwise).Add(vd.Name);
                    break;
                case TupleUnpackStmt tu: otherwise.UnionWith(tu.Targets); break;
                case ForStmt f:
                    otherwise.Add(f.VarName);
                    if (!string.IsNullOrEmpty(f.Var2Name)) otherwise.Add(f.Var2Name);
                    break;
                case WithStmt w when !string.IsNullOrEmpty(w.AsName): otherwise.Add(w.AsName); break;
                case GlobalStmt g: otherwise.UnionWith(g.Names); break;
                // `except E as x`, a match capture and a nonlocal rebinding are bindings this
                // walk does not follow, so a returned NAME stops qualifying; a literal does not.
                case NonlocalStmt or TryStmt or MatchStmt: noneOnly.Clear(); opaque = true; break;
            }
        }
        var bodyStmts = func.Body.Statements;
        foreach (var e in TypeInference.WalkExpressions(bodyStmts))
            if (e is WalrusExpr we) otherwise.Add(we.VarName);
        return TypeInference.WalkStatements(func.Body).OfType<ReturnStmt>().All(r =>
            r.Value is null or NoneLiteral
            || !opaque && r.Value is VariableExpr rv
               && noneOnly.Contains(rv.Name) && !otherwise.Contains(rv.Name));
    }

    /// <summary>
    /// The fields a method hands back when every `return` is `self.<field>`, bare, or
    /// `return None`, or null for any other shape. An outlined method receives its fields
    /// by value, so whether such a call returns None is a fact about the RECEIVER's fields,
    /// which only the call site knows.
    /// </summary>
    private static List<string>? SelfFieldsReturned(FunctionDef func)
    {
        if (func.IsAsync || func.Params.Count == 0 || func.ReturnMembers != null) return null;
        if (func.ReturnType is not ("" or "void" or "None")) return null;
        string self = func.Params[0].Name;
        var fields = new List<string>();
        foreach (var r in TypeInference.WalkStatements(func.Body).OfType<ReturnStmt>())
        {
            if (r.Value is null or NoneLiteral) continue;
            if (r.Value is not MemberAccessExpr { Object: VariableExpr sv } ma || sv.Name != self)
                return null;
            fields.Add(ma.Member);
        }
        return fields.Count > 0 ? fields : null;
    }

    // True when every `return` of `func` hands back one local the body binds to a list
    // (`v = [...]`, `v: list[T] = ...`). The binding must be the one live at the return:
    // `x = [1, 2]; x = 5; return x` hands back a scalar even though a list literal
    // touches the name earlier -- the scalar rebind decides what `return x` emits.
    private static bool ReturnsALocalList(FunctionDef func)
    {
        string? name = null;
        foreach (var r in TypeInference.WalkStatements(func.Body).OfType<ReturnStmt>())
        {
            if (r.Value is not VariableExpr v) return false;
            if (name == null) name = v.Name;
            else if (name != v.Name) return false;
        }
        if (name == null) return false;
        // Track the shape of the last binding in program order, the same view the
        // generator's sequential visit takes. A return reached while the name is
        // not list-shaped answers a scalar, not a list.
        bool listShaped = false;
        foreach (var s in TypeInference.WalkStatements(func.Body))
        {
            switch (s)
            {
                case AnnAssign a when a.Target == name:
                    listShaped = a.Annotation.StartsWith("list");
                    break;
                case VarDecl d when d.Name == name:
                    listShaped = d.VarType.StartsWith("list");
                    break;
                case AssignStmt { Target: VariableExpr t } a when t.Name == name:
                    listShaped = a.Value is ListExpr;
                    break;
                case ReturnStmt:
                    if (!listShaped) return false;
                    break;
            }
        }
        return listShaped;
    }

    private static string? SeqNameReturnedBy(FunctionDef func)
    {
        var assigned = new HashSet<string>();
        var receivers = new HashSet<(string, string)>();
        CollectMutatedNames(func.Body, assigned, receivers);
        string? name = null;
        foreach (var r in TypeInference.WalkStatements(func.Body).OfType<ReturnStmt>())
        {
            Expression? e = r.Value;
            if (e is CallExpr { Callee: VariableExpr { Name: "tuple" or "list" }, Args: [var inner] })
                e = inner;
            if (e is not VariableExpr v || assigned.Contains(v.Name)) continue;
            if (name == null) name = v.Name;
            else if (name != v.Name) return null;
        }
        return name;
    }

    // Registers a nested class (a class defined in the body of another class) so it
    // can be constructed with zero-cost ZCA inlining just like a top-level class.
    // Mirrors the per-method registration done for top-level classes, prefixing
    // symbols with the enclosing class path. Recurses for further nesting. Bases
    // (inheritance) on nested classes are not handled here -- none use it today.
    /// <summary>
    /// True when a parameter type is not one the backend can pass in registers: the shape an
    /// ISR handler takes (`def on_irq(pin: Pin)`). An UNANNOTATED parameter counts here, which
    /// is what keeps decorator-style stdlib helpers (`def inline(f)`) out of normal lowering.
    /// </summary>
    private static bool IsZcaHandlerParamType(string type)
        => (DataTypeExtensions.StringToDataType(type) == DataType.UNKNOWN
            // StringToDataType now answers GC_REF for `list[...]` -- true, but a
            // list parameter still has no subroutine ABI: the body binds the
            // caller's concrete element type, so it expands at the call site the
            // same way it always has. A bare `list` parameter is the same shape
            // with the element type arriving with the argument rather than the
            // annotation.
            || (type.StartsWith("list[") && type.EndsWith("]"))
            || type == "list")
           && type != "bytearray"
           && !type.StartsWith("ptr")
           && type != "const[str]" && type != "str";

    /// <summary>
    /// True when a parameter annotation NAMES a class, so the argument is a ZCA instance.
    /// Narrower than <see cref="IsZcaHandlerParamType"/> on purpose: an unannotated parameter
    /// is not an instance, and force-inlining those would swallow the stdlib's decorator
    /// helpers (`def inline(f)`, `def used(f)`) whose bodies must never be expanded.
    /// </summary>
    private static bool IsZcaInstanceParamType(string type)
        => !string.IsNullOrEmpty(type) && IsZcaHandlerParamType(type);

    /// <summary>
    /// Refuses `@used` (and `@export_c`) on a function that has no subroutine to point at.
    ///
    /// The two decorators mean "emit this with external linkage even though no Python code
    /// calls it": that is the whole reason to write one. A function registered for call-site
    /// EXPANSION has no call site here, so it was expanded nowhere and emitted nowhere, and
    /// the compiler said `[BUILD_OK]` and exited 0 with the function simply absent from the
    /// IR. Every reason a function gets registered for expansion reached this, `@inline`
    /// included: the loss was in the registration, not in any one of the reasons.
    ///
    /// It surfaces as someone else's words much later -- as `undefined symbol` out of the
    /// linker that was told the symbol would be there (#365). Refused here instead, at the
    /// definition, naming which of the two facts about the function has to give.
    /// </summary>
    private void RefuseExpansionOnlyUnderUsed(FunctionDef func, string reason)
    {
        if (!func.IsExportC) return;
        throw UserError(
            $"function '{func.Name}' is marked @used, which asks for a symbol with external "
            + $"linkage, but {reason}, so it can only be compiled where it is called and there "
            + "is no subroutine to export. Drop @used, or give the function a form that "
            + "compiles on its own (scalar, 'bytearray' or 'ptr' parameters only)", func);
    }

    /// <summary>
    /// Registers a function for call-site expansion (what `@inline` means). Shared by the
    /// explicit `@inline` decorator and by functions that take a class instance, which have
    /// no other lowering.
    /// </summary>
    /// Notes the current module's file against every function it defines, including methods
    /// of its classes and of classes nested inside them. Reads nothing and decides nothing;
    /// it exists so that <see cref="EmitInlineFunctionCall"/> can restore the callee's file
    /// while it expands that callee's body.
    private void RecordSourcePaths(ProgramNode ast)
    {
        foreach (var func in ast.Functions)
            functionSourcePath[func] = currentSourcePath;

        void Walk(ClassDef cls)
        {
            if (cls.Body is not Block block) return;
            foreach (var st in block.Statements)
            {
                if (st is FunctionDef m) functionSourcePath[m] = currentSourcePath;
                else if (st is ClassDef inner) Walk(inner);
            }
        }

        // Classes are ordinary top-level statements, not a separate list.
        foreach (var st in ast.GlobalStatements)
            if (st is ClassDef cls) Walk(cls);
    }

    private void RegisterInlineFunction(FunctionDef func, string fullName, ModuleScope? scope)
    {
        // `|| overloadedFunctions.Contains` so that once a name is overloaded (its
        // bare key removed below), a later same-named overload registers under its
        // suffixed key instead of re-occupying the vacated bare key, where it would
        // be invisible to suffix-based overload resolution.
        if (inlineFunctions.ContainsKey(fullName) || overloadedFunctions.Contains(fullName))
        {
            if (!overloadedFunctions.Contains(fullName))
            {
                var existing = inlineFunctions[fullName];
                string existingSfx = BuildOverloadSuffix(existing.Params);
                inlineFunctions[fullName + "___" + existingSfx] = existing;
                inlineFunctions.Remove(fullName);
                overloadedFunctions.Add(fullName);
            }

            string newSfx = BuildOverloadSuffix(func.Params);
            inlineFunctions[fullName + "___" + newSfx] = func;
        }
        else
        {
            inlineFunctions[fullName] = func;
            if (scope != null) scope.InlineFunctions[func.Name] = func;
        }
    }

    private void ScanNestedClassMembers(ClassDef nested, string outerPrefix)
    {
        if (nested.Bases.Contains("Enum") || nested.Bases.Contains("IntEnum")) return;
        if (nested.Body is not Block block) return;

        classNames.Add(nested.Name);
        if (nested.IsValue) valueClasses.Add(nested.Name);

        var oldPrefix = currentModulePrefix;
        var classPrefix = outerPrefix + nested.Name + "_";
        currentModulePrefix = classPrefix;

        string classKey = classPrefix.Substring(0, classPrefix.Length - 1);
        if (!classDirectMethods.ContainsKey(classKey))
            classDirectMethods[classKey] = new HashSet<string>();
        if (nested.Bases.Any(IsProtocolBaseName))
            protocolClasses.Add(classKey);

        // Nested classes need the same field widths as top-level classes (#443).
        // Otherwise a computed constructor value can fall back to a byte-sized field.
        classFieldLayout[classKey] = DeriveFieldLayout(block, classKey);
        if (classFieldLayout[classKey].Count == 1)
            zcaFactoryClasses[classKey] = classFieldLayout[classKey][0].Type;

        foreach (var inner in block.Statements)
        {
            if (inner is FunctionDef func)
            {
                classDirectMethods[classKey].Add(func.Name);

                RefuseUnsupportedMethodDecorators(func, nested.Name);

                string fullName = currentModulePrefix + func.Name;
                functionReturnTypes[fullName] = func.ReturnType;
                if (ReturnsOnlyNone(func)) noneReturningFunctions.Add(fullName);
                var @params = new List<string>();
                var paramTypes = new List<DataType>();
                foreach (var p in func.Params)
                {
                    @params.Add(p.Name);
                    paramTypes.Add(DataTypeExtensions.StringToDataType(p.Type));
                }

                functionParams[fullName] = @params;
                functionParamTypes[fullName] = paramTypes;
                functionParamDeclared[fullName] = func.Params.Select(p => p.Type).ToList();

                if (func.IsPropertySetter)
                {
                    string setterKey = fullName + "___setter";
                    inlineFunctions[setterKey] = func;
                    propertySetters[classKey + "." + func.PropertyName] = setterKey;
                }
                else if (func.IsInline)
                {
                    // See the top-level class path: once overloaded, register under the
                    // suffixed key rather than re-occupying the vacated bare key.
                    if (overloadedFunctions.Contains(fullName))
                    {
                        inlineFunctions[fullName + "___" + BuildOverloadSuffix(func.Params)] = func;
                    }
                    else if (!inlineFunctions.TryAdd(fullName, func))
                    {
                        var existing = inlineFunctions[fullName];
                        if (existing?.Params != null)
                        {
                            var existingSfx = BuildOverloadSuffix(existing.Params);
                            inlineFunctions[fullName + "___" + existingSfx] = existing;
                        }

                        inlineFunctions.Remove(fullName);
                        overloadedFunctions.Add(fullName);
                        inlineFunctions[fullName + "___" + BuildOverloadSuffix(func.Params)] = func;
                    }
                }
                else
                {
                    functionsToCompile.Add(new FunctionEntry
                        { Prefix = currentModulePrefix, Func = func, SourceFile = currentSourceFile, SourcePath = currentSourcePath });
                    instanceMethodDefs[fullName] = func;
                }

                if (!func.IsPropertySetter)
                {
                    methodInstanceTypes[fullName] =
                        currentModulePrefix.Substring(0, currentModulePrefix.Length - 1);
                }
            }
            else if (inner is ClassDef deeper)
            {
                ScanNestedClassMembers(deeper, classPrefix);
            }
        }

        currentModulePrefix = oldPrefix;
    }

    // Returns the set of parameter names (excluding "self") that are accessed with a
    // variable (non-constant) index inside the given inline function body, including
    // parameters that are forwarded to nested inline function calls whose own parameters
    // are also variable-indexed.
    /// <param name="selfClass">
    /// The class the scanned function belongs to, so a nested `self.m(...)` can be followed.
    ///
    /// Without it this stops at ONE level: the branch below resolves `ic.Callee is VariableExpr`
    /// only, and its own comment said "non-method". So `a` -> `self._b` was propagated by the
    /// caller's scan while `_b` -> `self._c` was not, and a buffer handed down two levels lost
    /// its runtime-indexed-ness at the second (#246). The CYW43 helpers nest exactly that way.
    /// </param>
    private HashSet<string> GetInlineVarIndexedParams(FunctionDef func, HashSet<FunctionDef> visiting,
                                                      string? selfClass = null)
    {
        if (!visiting.Add(func)) return new HashSet<string>(); // cycle guard

        var paramNames = new HashSet<string>(func.Params.Select(p => p.Name));
        var result = new HashSet<string>();

        // bytearray-typed parameters always require SRAM — they are indexable buffers passed
        // by pointer.  Marking them here avoids having to follow the full inline call chain to
        // find a variable-index subscript.
        foreach (var p in func.Params)
            if (p.Type == "bytearray") result.Add(p.Name);

        void ScanIExpr(Expression? expr)
        {
            if (expr == null) return;
            if (expr is IndexExpr idx && idx.Target is VariableExpr ve && !(idx.Index is IntegerLiteral))
            {
                if (paramNames.Contains(ve.Name))
                    result.Add(ve.Name);
            }

            if (expr is CallExpr ic)
            {
                ScanIExpr(ic.Callee);
                foreach (var a in ic.Args) ScanIExpr(a);

                // Resolve the callee to an inline function and propagate. Both shapes: a
                // direct call, and `self.m(...)` when the enclosing class is known -- a method
                // is keyed `Class_method`, so the class plus the member name is the key.
                string nestedKey = "";
                if (ic.Callee is VariableExpr icVe)
                    nestedKey = ResolveCallee(icVe.Name);
                else if (!string.IsNullOrEmpty(selfClass)
                         && ic.Callee is MemberAccessExpr { Object: VariableExpr { Name: "self" } } icSelf)
                    nestedKey = selfClass + "_" + icSelf.Member;

                if (!string.IsNullOrEmpty(nestedKey) && inlineFunctions.TryGetValue(nestedKey, out var nested))
                {
                    var nestedVarIdx = GetInlineVarIndexedParams(nested, visiting, selfClass);
                    if (nestedVarIdx.Count > 0)
                    {
                        int argPos = 0;
                        foreach (var np in nested.Params)
                        {
                            if (np.Name == "self") continue;
                            if (nestedVarIdx.Contains(np.Name) && argPos < ic.Args.Count)
                            {
                                if (ic.Args[argPos] is VariableExpr argVe && paramNames.Contains(argVe.Name))
                                    result.Add(argVe.Name);
                            }
                            argPos++;
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(nestedKey) && overloadedFunctions.Contains(nestedKey))
                {
                    // Overloaded direct call: union results from all variants.
                    foreach (var kv in inlineFunctions)
                    {
                        if (!kv.Key.StartsWith(nestedKey + "___")) continue;
                        var oVarIdx = GetInlineVarIndexedParams(kv.Value, visiting);
                        if (oVarIdx.Count == 0) continue;
                        int oArgPos = 0;
                        foreach (var np in kv.Value.Params)
                        {
                            if (np.Name == "self") continue;
                            if (oVarIdx.Contains(np.Name) && oArgPos < ic.Args.Count)
                            {
                                if (ic.Args[oArgPos] is VariableExpr argVe && paramNames.Contains(argVe.Name))
                                    result.Add(argVe.Name);
                            }
                            oArgPos++;
                        }
                    }
                }
            }

            if (expr is BinaryExpr ib) { ScanIExpr(ib.Left); ScanIExpr(ib.Right); }
            if (expr is UnaryExpr iu) ScanIExpr(iu.Operand);
            if (expr is MemberAccessExpr ima) ScanIExpr(ima.Object);
        }

        // The shared walk reaches the arms the old recursion skipped: a param
        // subscripted inside try/with/match/for or in a `v: T = p[i]` initializer
        // still marks the parameter variable-indexed.
        foreach (var s in TypeInference.WalkStatements(func.Body.Statements))
        {
            switch (s)
            {
                case AssignStmt ia: ScanIExpr(ia.Target); ScanIExpr(ia.Value); break;
                case AnnAssign ia2: ScanIExpr(ia2.Value); break;
                case VarDecl ivd: ScanIExpr(ivd.Init); break;
                case ReturnStmt ir: ScanIExpr(ir.Value); break;
                case ExprStmt ie: ScanIExpr(ie.Expr); break;
                case IfStmt iif:
                    ScanIExpr(iif.Condition);
                    foreach (var b in iif.ElifBranches) ScanIExpr(b.Condition);
                    break;
                case WhileStmt iwh: ScanIExpr(iwh.Condition); break;
                case ForStmt ifr:
                    ScanIExpr(ifr.Iterable); ScanIExpr(ifr.RangeStart);
                    ScanIExpr(ifr.RangeStop); ScanIExpr(ifr.RangeStep);
                    break;
                case WithStmt iwi: ScanIExpr(iwi.ContextExpr); break;
                case MatchStmt im:
                    ScanIExpr(im.Target);
                    foreach (var br in im.Branches) { ScanIExpr(br.Pattern); ScanIExpr(br.Guard); }
                    break;
                case AugAssignStmt iaug: ScanIExpr(iaug.Target); ScanIExpr(iaug.Value); break;
                case TupleUnpackStmt itu: ScanIExpr(itu.Value); break;
                case AssertStmt ias: ScanIExpr(ias.Condition); break;
            }
        }

        visiting.Remove(func);
        return result;
    }

    /// <summary>
    /// True when <paramref name="name"/> appears as the target of a subscript anywhere in
    /// <paramref name="body"/> (`name[...]`, read or written), skipping any nested function
    /// that rebinds the name as its own parameter.
    /// </summary>
    /// <remarks>
    /// Used to decide that an unannotated parameter is a buffer rather than a register. A
    /// shape this walk does not reach simply infers nothing, which leaves the parameter
    /// exactly as it was before -- so a gap here costs a diagnostic, never a wrong answer.
    /// </remarks>
    private static bool IsSubscriptedInBody(Statement? body, string name)
    {
        bool found = false;

        void Expr(Expression? e)
        {
            if (e == null || found) return;
            switch (e)
            {
                case IndexExpr idx:
                    if (idx.Target is VariableExpr tv && tv.Name == name) { found = true; return; }
                    Expr(idx.Target); Expr(idx.Index); return;
                case SliceExpr sl: Expr(sl.Start); Expr(sl.Stop); Expr(sl.Step); return;
                case BinaryExpr b: Expr(b.Left); Expr(b.Right); return;
                case UnaryExpr u: Expr(u.Operand); return;
                case TernaryExpr t: Expr(t.Condition); Expr(t.TrueVal); Expr(t.FalseVal); return;
                case WalrusExpr w: Expr(w.Value); return;
                case AwaitExpr aw: Expr(aw.Operand); return;
                case YieldExpr y: Expr(y.Value); return;
                case MemberAccessExpr m: Expr(m.Object); return;
                case StarArgExpr st: Expr(st.Value); return;
                case DoubleStarArgExpr dst: Expr(dst.Value); return;
                case KeywordArgExpr kw: Expr(kw.Value); return;
                case CallExpr c:
                    Expr(c.Callee);
                    foreach (var a in c.Args) Expr(a);
                    return;
                case ListExpr le: foreach (var el in le.Elements) Expr(el); return;
                case TupleExpr te: foreach (var el in te.Elements) Expr(el); return;
                case SetExpr se: foreach (var el in se.Elements) Expr(el); return;
                case DictExpr de:
                    foreach (var (k, v) in de.Entries) { Expr(k); Expr(v); }
                    return;
                case FStringExpr fs: foreach (var part in fs.Parts) Expr(part.Expr); return;
                case ListCompExpr lc:
                    Expr(lc.Element); Expr(lc.Iterable); Expr(lc.Iterable2); Expr(lc.Filter);
                    return;
                case GeneratorExpr gx:
                    Expr(gx.Element); Expr(gx.Iterable); Expr(gx.Iterable2); Expr(gx.Filter);
                    return;
                default: return;
            }
        }

        void Handle(Statement s)
        {
            if (found) return;
            switch (s)
            {
                case ExprStmt es: Expr(es.Expr); return;
                case AssignStmt a: Expr(a.Target); Expr(a.Value); return;
                case AugAssignStmt ag: Expr(ag.Target); Expr(ag.Value); return;
                case AnnAssign an: Expr(an.Value); return;
                case VarDecl vd: Expr(vd.Init); return;
                case ReturnStmt r: Expr(r.Value); return;
                case TupleUnpackStmt tu: Expr(tu.Value); return;
                case AssertStmt asrt: Expr(asrt.Condition); return;
                case IfStmt i:
                    Expr(i.Condition);
                    foreach (var (cond, _) in i.ElifBranches) Expr(cond);
                    return;
                case WhileStmt w: Expr(w.Condition); return;
                case ForStmt f:
                    Expr(f.RangeStart); Expr(f.RangeStop); Expr(f.RangeStep); Expr(f.Iterable);
                    return;
                case WithStmt wi: Expr(wi.ContextExpr); return;
                case MatchStmt m:
                    Expr(m.Target);
                    foreach (var br in m.Branches) { Expr(br.Pattern); Expr(br.Guard); }
                    return;
                case FunctionDef nested:
                    // A nested def that takes the same parameter name owns it from here in.
                    // The shared walk does not descend into nested defs, so its body is
                    // walked explicitly here, under the same rule.
                    if (nested.Params.Any(p => p.Name == name)) return;
                    foreach (var ns in TypeInference.WalkStatements(nested.Body))
                        Handle(ns);
                    return;
                default: return;
            }
        }

        foreach (var s in TypeInference.WalkStatements(body))
            Handle(s);
        return found;
    }

    /// <param name="selfClass">
    /// The class whose method is being scanned, so `self.m(...)` inside it can be resolved.
    ///
    /// Without it `localVarTypes` never has an entry for `self`: that table is filled by one
    /// thing, a local assigned a CONSTRUCTOR CALL, and `self` is a parameter. So the method-call
    /// branch below missed, `resolvedFunc` stayed null, and the propagation that hands a
    /// callee's runtime-indexed parameter back to the caller's array never ran -- which is why
    /// `r._emit(b, 2)` from a plain function worked and `self._emit(b, 2)` from inside a method
    /// did not, for the same callee and the same array (#246).
    /// </param>
    private void ScanForVariableIndexedArrays(List<Statement> stmts, string prefix,
                                              string? selfClass = null,
                                              string? listDeclPrefix = null)
    {
        var localArrays = new HashSet<string>();
        var declaredGlobalHere = TypeInference.WalkStatements(stmts).OfType<GlobalStmt>()
            .SelectMany(g => g.Names).ToHashSet();

        // Pre-scan: collect local variable → class name for constructor calls, so we can
        // resolve method calls to inline functions without needing instanceClasses (which
        // is not yet populated at this point in compilation).
        var localVarTypes = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(selfClass)) localVarTypes[prefix + "self"] = selfClass;

        void CollectArrayDecls(Statement stmt)
        {
            if (stmt is AnnAssign ann)
            {
                if (ann.Annotation.StartsWith("list[") && ann.Annotation.EndsWith("]"))
                {
                    string elemTypeName = ann.Annotation.Substring(5, ann.Annotation.Length - 6);
                    DataType elemDt = DataTypeExtensions.StringToDataType(elemTypeName);
                    // The key must be the one EmitListAnnAssign settles on: a module-level
                    // list is a GLOBAL filed bare (`xs`), not under `main.xs`. Filing the
                    // prefixed name here left a phantom entry that ResolveListVarQualified
                    // prefers over the real slot -- len(xs) then dereferenced a pointer
                    // nobody writes and read 0 while xs[i]/xs.append() used the right one
                    // (they resolve through ResolveNameKey, which never consults this
                    // table). The condition mirrors the emitter's: outside an inline
                    // expansion, a name mutableGlobals holds is module storage.
                    // An inlined callee's arrays are scanned under the CALLER's prefix, but its
                    // `x: list[T]` is filed by the emitter under the expansion's own prefix:
                    // under the caller's it became a phantom `main.x` that a caller-side `x`
                    // resolved to, so `x = f(); print(x)` read an empty list.
                    string listDeclKey = (listDeclPrefix ?? prefix) + ann.Target;
                    if (listDeclPrefix == null
                        && !string.IsNullOrEmpty(currentFunction)
                        && string.IsNullOrEmpty(currentInlinePrefix)
                        && (currentFunction == "main" || declaredGlobalHere.Contains(ann.Target))
                        && mutableGlobals.ContainsKey(currentModulePrefix + ann.Target))
                        listDeclKey = currentModulePrefix + ann.Target;
                    listVarElemTypes[listDeclKey] = elemDt;
                    if (elemTypeName.StartsWith("list[") && elemTypeName.EndsWith("]"))
                        listInnerElemTypes[listDeclKey] = DataTypeExtensions.StringToDataType(
                            elemTypeName.Substring(5, elemTypeName.Length - 6));
                    // list variables are NOT fixed-size arrays; do not add to localArrays
                }
                else
                {
                    int br = ann.Annotation.IndexOf('[');
                    int cl = ann.Annotation.LastIndexOf(']');
                    if (br != -1 && cl != -1)
                    {
                        string inner = ann.Annotation.Substring(br + 1, cl - br - 1);
                        if (!string.IsNullOrEmpty(inner) && inner.All(char.IsDigit))
                            localArrays.Add(prefix + ann.Target);
                    }

                    if (ann.Annotation == "bytearray")
                        localArrays.Add(prefix + ann.Target);
                }
            }
            else if (stmt is VarDecl vd)
            {
                if (vd.VarType == "bytearray")
                    localArrays.Add(prefix + vd.Name);
            }
        }

        // The shared walk reaches decls the old recursion skipped: a `b: uint8[16]`
        // or `b: bytearray` inside for/try/with/match is still an array decl.
        foreach (var s in TypeInference.WalkStatements(stmts)) CollectArrayDecls(s);

        // Pre-scan statements to collect variable → class name from constructor calls.
        void CollectLocalTypes(Statement stmt)
        {
            void TryRecordType(string varName, Expression? value)
            {
                if (value is not CallExpr ctorCall) return;
                string cls = "";
                if (ctorCall.Callee is VariableExpr cv)
                    cls = ResolveCallee(cv.Name);
                else if (ctorCall.Callee is MemberAccessExpr cm && cm.Object is VariableExpr modVe
                         && modules.ContainsKey(modVe.Name))
                    cls = modVe.Name.Replace('.', '_') + "_" + cm.Member;
                if (!string.IsNullOrEmpty(cls) &&
                    (inlineFunctions.ContainsKey(cls + "___init__") ||
                     overloadedFunctions.Contains(cls + "___init__")))
                    localVarTypes[prefix + varName] = cls;
            }

            if (stmt is AssignStmt asn && asn.Target is VariableExpr asv)
                TryRecordType(asv.Name, asn.Value);
            else if (stmt is AnnAssign aan && aan.Value != null)
                TryRecordType(aan.Target, aan.Value);
        }

        foreach (var s in TypeInference.WalkStatements(stmts)) CollectLocalTypes(s);

        void ScanExpr(Expression? expr)
        {
            if (expr == null) return;
            if (expr is IndexExpr idx)
            {
                if (idx.Target is VariableExpr ve)
                {
                    string q = prefix + ve.Name;
                    if (localArrays.Contains(q) && !(idx.Index is IntegerLiteral))
                    {
                        arraysWithVariableIndex.Add(q);
                    }
                }

                ScanExpr(idx.Target);
                ScanExpr(idx.Index);
            }
            else if (expr is CallExpr call)
            {
                ScanExpr(call.Callee);
                foreach (var arg in call.Args) ScanExpr(arg);

                // Propagate variable-index array info from inline function parameters
                // to the actual arguments at this call site.
                FunctionDef? resolvedFunc = null;
                string overloadedKey = "";   // non-empty when the call targets an overloaded inline
                // The class of the RECEIVER, kept so a nested `self.m(...)` inside the callee
                // can be followed too. Null for a plain function call, which has no self.
                string? resolvedSelfClass = null;

                if (call.Callee is MemberAccessExpr memAcc && memAcc.Object is VariableExpr objVe)
                {
                    // Method call: resolve object type via pre-collected localVarTypes.
                    string objKey = prefix + objVe.Name;
                    if (localVarTypes.TryGetValue(objKey, out string cls))
                    {
                        string methodKey = cls + "_" + memAcc.Member;
                        resolvedSelfClass = cls;
                        if (!inlineFunctions.TryGetValue(methodKey, out resolvedFunc) &&
                            overloadedFunctions.Contains(methodKey))
                            overloadedKey = methodKey;
                    }
                }
                else if (call.Callee is VariableExpr callVe)
                {
                    // Direct function call.
                    string resolvedCallee = ResolveCallee(callVe.Name);
                    if (!inlineFunctions.TryGetValue(resolvedCallee, out resolvedFunc) &&
                        overloadedFunctions.Contains(resolvedCallee))
                        overloadedKey = resolvedCallee;

                    // For non-inline functions: if an argument is a local array passed to a
                    // bytearray parameter (UINT16 pointer type), mark it as needing SRAM storage
                    // so it is allocated contiguously and not constant-folded away.
                    if (resolvedFunc == null && string.IsNullOrEmpty(overloadedKey) &&
                        functionParamTypes.TryGetValue(resolvedCallee, out var calleeParamTypes))
                    {
                        for (int ai = 0; ai < call.Args.Count && ai < calleeParamTypes.Count; ai++)
                        {
                            if (calleeParamTypes[ai] == DataType.UINT16 && call.Args[ai] is VariableExpr argVe2)
                            {
                                string actualName = prefix + argVe2.Name;
                                if (localArrays.Contains(actualName))
                                    arraysWithVariableIndex.Add(actualName);
                            }
                        }
                    }
                }

                // Single non-overloaded inline function.
                if (resolvedFunc != null)
                {
                    var varIdxParams = GetInlineVarIndexedParams(resolvedFunc, new HashSet<FunctionDef>(), resolvedSelfClass);
                    if (varIdxParams.Count > 0)
                    {
                        int argPos = 0;
                        foreach (var param in resolvedFunc.Params)
                        {
                            if (param.Name == "self") continue;
                            if (varIdxParams.Contains(param.Name) && argPos < call.Args.Count)
                            {
                                if (call.Args[argPos] is VariableExpr argVe)
                                {
                                    string actualName = prefix + argVe.Name;
                                    if (localArrays.Contains(actualName))
                                        arraysWithVariableIndex.Add(actualName);
                                }
                            }
                            argPos++;
                        }
                    }
                }

                // Overloaded inline functions: union var-index info across ALL variants whose
                // non-self parameter count matches the call argument count.  We cannot pick a
                // single overload without full type inference, so we take the conservative
                // (false-positive-safe) approach of marking an argument if ANY overload
                // indicates that position needs SRAM.
                if (!string.IsNullOrEmpty(overloadedKey))
                {
                    int argCount = call.Args.Count;
                    foreach (var kv in inlineFunctions)
                    {
                        if (!kv.Key.StartsWith(overloadedKey + "___")) continue;
                        if (kv.Value.Params.Count(p => p.Name != "self") != argCount) continue;
                        var varIdxP = GetInlineVarIndexedParams(kv.Value, new HashSet<FunctionDef>(), resolvedSelfClass);
                        if (varIdxP.Count == 0) continue;
                        int ap = 0;
                        foreach (var param in kv.Value.Params)
                        {
                            if (param.Name == "self") continue;
                            if (varIdxP.Contains(param.Name) && ap < call.Args.Count)
                            {
                                if (call.Args[ap] is VariableExpr argVe)
                                {
                                    string actualName = prefix + argVe.Name;
                                    if (localArrays.Contains(actualName))
                                        arraysWithVariableIndex.Add(actualName);
                                }
                            }
                            ap++;
                        }
                    }
                }
            }
            else if (expr is BinaryExpr bin)
            {
                ScanExpr(bin.Left);
                ScanExpr(bin.Right);
            }
            else if (expr is UnaryExpr un)
            {
                ScanExpr(un.Operand);
            }
        }

        void ScanStmt(Statement stmt)
        {
            switch (stmt)
            {
                case AssignStmt assign:
                    ScanExpr(assign.Target);
                    ScanExpr(assign.Value);
                    break;
                case AnnAssign ann:
                    ScanExpr(ann.Value);
                    break;
                case ReturnStmt ret:
                    ScanExpr(ret.Value);
                    break;
                case ExprStmt exprStmt:
                    ScanExpr(exprStmt.Expr);
                    break;
                case IfStmt ifStmt:
                    ScanExpr(ifStmt.Condition);
                    foreach (var branch in ifStmt.ElifBranches)
                        ScanExpr(branch.Condition);
                    break;
                case WhileStmt wh:
                    ScanExpr(wh.Condition);
                    break;
                case AugAssignStmt aug:
                    ScanExpr(aug.Target);
                    ScanExpr(aug.Value);
                    break;
                case VarDecl vd:
                    // `v: T = arr[idx]` declares v with a runtime-indexed read in its initializer.
                    // Without scanning it, arr is never marked variable-indexed and the read demands
                    // a constant subscript -- yet `v: T = 0; v = arr[idx]` works. Scan the init.
                    ScanExpr(vd.Init);
                    break;
                case ForStmt fr:
                    ScanExpr(fr.RangeStart); ScanExpr(fr.RangeStop); ScanExpr(fr.RangeStep);
                    ScanExpr(fr.Iterable);
                    break;
                case WithStmt wi:
                    ScanExpr(wi.ContextExpr);
                    break;
                case MatchStmt m:
                    ScanExpr(m.Target);
                    foreach (var br in m.Branches) { ScanExpr(br.Pattern); ScanExpr(br.Guard); }
                    break;
                case TupleUnpackStmt tu:
                    ScanExpr(tu.Value);
                    break;
            }
        }

        // The shared walk reaches the arms the old recursion skipped: a runtime
        // subscript inside try/with/match still marks the array variable-indexed.
        foreach (var s in TypeInference.WalkStatements(stmts)) ScanStmt(s);
    }

    /// <summary>
    /// Collect the names bound to `[]` that the same function later mutates
    /// with `.append(...)` -- the shape an unmodified CircuitPython library
    /// writes (`received = []`, then `received.append(pulse)`). A name in the
    /// set tells the `x = []` binding site to emit a real heap object with the
    /// element type left pending, instead of a compile-time empty sequence.
    /// A NON-empty literal the function appends to (`value = [a, b, c]`, then
    /// `value.append(d)` under a run-time condition, adafruit_pixelbuf's
    /// `_getitem`) is collected too: its length is not a compile-time fact
    /// either, so the binding site materializes it as a heap list.
    /// `prefix` is the qualification the emitted body will spell its locals
    /// with (`fullName.` for an outlined function, `inlineN.callee.` for an
    /// expansion), so the site and the prescan agree on the key.
    /// </summary>
    /// <summary>
    /// A condition decided by what the scan can already see: literals, `not`/`and`/`or`, a
    /// name or an instance field (`self._has_white`) bound to a compile-time constant under
    /// this expansion's prefix. Null when it cannot tell, which keeps every arm reachable.
    /// </summary>
    private bool? FoldAtScan(Expression? e, string prefix, HashSet<string> locallyBound)
    {
        bool? ConstName(string key)
        {
            for (int hop = 0; hop < 20 && key != null; hop++)
            {
                if (!killedConstants.Contains(key) && constantVariables.TryGetValue(key, out int c))
                    return c != 0;
                if (!variableAliases.TryGetValue(key, out key!)) break;
            }
            return null;
        }
        bool? Field(string obj, string member)
        {
            string? key = prefix + obj;
            for (int hop = 0; hop < 20 && key != null; hop++)
            {
                string fk = key + "_" + member;
                if (!killedConstants.Contains(fk) && constantVariables.TryGetValue(fk, out int c))
                    return c != 0;
                if (!variableAliases.TryGetValue(key, out key)) break;
            }
            return null;
        }
        switch (e)
        {
            case BooleanLiteral b: return b.Value;
            case IntegerLiteral i: return i.Value != 0;
            case NoneLiteral: return false;
            case UnaryExpr { Op: Frontend.UnaryOp.Not } u:
                return FoldAtScan(u.Operand, prefix, locallyBound) is { } inner ? !inner : null;
            case BinaryExpr { Op: Frontend.BinaryOp.And } a:
            {
                var l = FoldAtScan(a.Left, prefix, locallyBound);
                if (l == false) return false;
                var r = FoldAtScan(a.Right, prefix, locallyBound);
                return l == true ? r : (r == false ? false : null);
            }
            case BinaryExpr { Op: Frontend.BinaryOp.Or } o:
            {
                var l = FoldAtScan(o.Left, prefix, locallyBound);
                if (l == true) return true;
                var r = FoldAtScan(o.Right, prefix, locallyBound);
                return l == false ? r : (r == true ? true : null);
            }
            case VariableExpr v:
            {
                if (ConstName(prefix + v.Name) is { } cq) return cq;
                // A name the scanned body binds locally shadows every module-scope
                // binding the bare spelling would fold on; the local's value is not
                // known yet (the body has not lowered), so the arm stays undecided.
                // The lowering makes the same check through variableTypes -- already
                // populated by then -- in TryConstIntTruthiness.
                if (locallyBound.Contains(v.Name)) return null;
                if (ConstName(currentModulePrefix + v.Name) is { } cm) return cm;
                if (constantVariables.TryGetValue(v.Name, out int cv)) return cv != 0;
                if (localConstantValues.TryGetValue(v.Name, out cv) && !ForeignFlowRead(v.Name))
                    return cv != 0;
                if (floatConstantVariables.TryGetValue(v.Name, out double fv)) return fv != 0.0;
                if (globals.TryGetValue(v.Name, out var sym) && !sym.IsMemoryAddress)
                    return sym.Value != 0;
                return null;
            }
            case MemberAccessExpr { Object: VariableExpr ov } m:
                return Field(ov.Name, m.Member);
            default: return null;
        }
    }

    private void ScanPromotableEmptyLists(List<Statement> stmts, string prefix)
    {
        var boundEmpty = new HashSet<string>();
        var appended = new HashSet<string>();

        void ScanExpr(Expression? expr)
        {
            switch (expr)
            {
                case null: break;
                case CallExpr call:
                    if (call.Callee is MemberAccessExpr { Member: "append", Object: VariableExpr apv })
                        appended.Add(apv.Name);
                    ScanExpr(call.Callee);
                    foreach (var arg in call.Args) ScanExpr(arg);
                    break;
                case MemberAccessExpr mem:
                    ScanExpr(mem.Object);
                    break;
                case IndexExpr idx:
                    ScanExpr(idx.Target);
                    ScanExpr(idx.Index);
                    break;
                case BinaryExpr bin:
                    ScanExpr(bin.Left);
                    ScanExpr(bin.Right);
                    break;
                case UnaryExpr un:
                    ScanExpr(un.Operand);
                    break;
                case TernaryExpr ter:
                    ScanExpr(ter.Condition);
                    ScanExpr(ter.TrueVal);
                    ScanExpr(ter.FalseVal);
                    break;
                case ListExpr le:
                    foreach (var e in le.Elements) ScanExpr(e);
                    break;
                case TupleExpr te:
                    foreach (var e in te.Elements) ScanExpr(e);
                    break;
                case SetExpr se:
                    foreach (var e in se.Elements) ScanExpr(e);
                    break;
                case DictExpr de:
                    foreach (var (k, v) in de.Entries) { ScanExpr(k); ScanExpr(v); }
                    break;
            }
        }

        // Names this body binds locally: an assignment, declaration, loop or
        // unpack target, `with ... as` or walrus makes the spelling a local for
        // the body's whole extent, so a same-spelled module constant must not
        // decide an `if`'s reachability -- the local's value is not recorded
        // until the body lowers.
        var declaredGlobals = TypeInference.WalkStatements(stmts).OfType<GlobalStmt>()
            .SelectMany(g => g.Names).ToHashSet();
        var locallyBound = new HashSet<string>();
        void CollectBound(Expression? e)
        {
            switch (e)
            {
                case null: break;
                case VariableExpr t: locallyBound.Add(t.Name); break;
                case TupleExpr t: foreach (var el in t.Elements) CollectBound(el); break;
                default: break;
            }
        }
        void CollectBoundInExpr(Expression? e)
        {
            switch (e)
            {
                case null: break;
                case WalrusExpr w: locallyBound.Add(w.VarName); CollectBoundInExpr(w.Value); break;
                case CallExpr call:
                    CollectBoundInExpr(call.Callee);
                    foreach (var a in call.Args) CollectBoundInExpr(a);
                    break;
                case MemberAccessExpr mem: CollectBoundInExpr(mem.Object); break;
                case IndexExpr idx: CollectBoundInExpr(idx.Target); CollectBoundInExpr(idx.Index); break;
                case BinaryExpr bin: CollectBoundInExpr(bin.Left); CollectBoundInExpr(bin.Right); break;
                case UnaryExpr un: CollectBoundInExpr(un.Operand); break;
                case TernaryExpr ter:
                    CollectBoundInExpr(ter.Condition); CollectBoundInExpr(ter.TrueVal); CollectBoundInExpr(ter.FalseVal);
                    break;
                case ListExpr le: foreach (var el in le.Elements) CollectBoundInExpr(el); break;
                case TupleExpr te: foreach (var el in te.Elements) CollectBoundInExpr(el); break;
                case SetExpr se: foreach (var el in se.Elements) CollectBoundInExpr(el); break;
                case DictExpr de: foreach (var (k, v) in de.Entries) { CollectBoundInExpr(k); CollectBoundInExpr(v); } break;
            }
        }
        foreach (var s in TypeInference.WalkStatements(stmts))
        {
            switch (s)
            {
                case AssignStmt asn: CollectBound(asn.Target); CollectBoundInExpr(asn.Value); break;
                case AnnAssign ann: locallyBound.Add(ann.Target); CollectBoundInExpr(ann.Value); break;
                case VarDecl vd: locallyBound.Add(vd.Name); CollectBoundInExpr(vd.Init); break;
                case AugAssignStmt aug: CollectBound(aug.Target); CollectBoundInExpr(aug.Value); break;
                case ForStmt fr:
                    locallyBound.Add(fr.VarName);
                    if (fr.Var2Name.Length != 0) locallyBound.Add(fr.Var2Name);
                    CollectBoundInExpr(fr.RangeStart); CollectBoundInExpr(fr.RangeStop);
                    CollectBoundInExpr(fr.RangeStep); CollectBoundInExpr(fr.Iterable);
                    break;
                case TupleUnpackStmt tu:
                    foreach (var t in tu.Targets) locallyBound.Add(t);
                    CollectBoundInExpr(tu.Value);
                    break;
                case WithStmt wi:
                    if (wi.AsName.Length != 0) locallyBound.Add(wi.AsName);
                    CollectBoundInExpr(wi.ContextExpr);
                    break;
                case ExprStmt es: CollectBoundInExpr(es.Expr); break;
                case ReturnStmt ret: CollectBoundInExpr(ret.Value); break;
                case IfStmt ifs:
                    CollectBoundInExpr(ifs.Condition);
                    foreach (var (elifCond, _) in ifs.ElifBranches) CollectBoundInExpr(elifCond);
                    break;
                case WhileStmt wh: CollectBoundInExpr(wh.Condition); break;
                case MatchStmt m: CollectBoundInExpr(m.Target); break;
            }
        }
        locallyBound.ExceptWith(declaredGlobals);

        // An arm a compile-time condition rules out is never lowered, and an append in it
        // must not decide the binding: pixelbuf's `_getitem` appends the white channel under
        // `if self._has_white`, which folds to False for an RGB strip, and promoting on that
        // dead append made every read pay for a heap list (+1540 B on a NeoPixel program).
        var dead = new HashSet<Statement>();
        foreach (var ifs in TypeInference.WalkStatements(stmts).OfType<IfStmt>())
        {
            var arms = new List<(Expression? Cond, Statement? Body)> { (ifs.Condition, ifs.ThenBranch) };
            arms.AddRange(ifs.ElifBranches.Select(e => ((Expression?)e.Item1, (Statement?)e.Item2)));
            arms.Add((null, ifs.ElseBranch));
            bool decided = false;   // an earlier arm folded True: every later arm is dead
            foreach (var (cond, body) in arms)
            {
                bool? v = cond == null ? true : FoldAtScan(cond, prefix, locallyBound);
                if (decided || v == false)
                {
                    foreach (var d in TypeInference.WalkStatements(body)) dead.Add(d);
                    continue;
                }
                if (v == true) decided = true;
                else if (cond != null) break;   // undecided: this arm and the rest may run
            }
        }

        foreach (var s in TypeInference.WalkStatements(stmts))
        {
            if (dead.Contains(s)) continue;
            switch (s)
            {
                case AssignStmt asn:
                    if (asn.Target is VariableExpr tv && asn.Value is ListExpr)
                        boundEmpty.Add(tv.Name);
                    ScanExpr(asn.Target);
                    ScanExpr(asn.Value);
                    break;
                case AnnAssign ann:
                    if (ann.Value is ListExpr { Elements.Count: 0 })
                        boundEmpty.Add(ann.Target);
                    ScanExpr(ann.Value);
                    break;
                case VarDecl vd:
                    if (vd.Init is ListExpr { Elements.Count: 0 })
                        boundEmpty.Add(vd.Name);
                    ScanExpr(vd.Init);
                    break;
                case ExprStmt es:
                    ScanExpr(es.Expr);
                    break;
                case ReturnStmt ret:
                    ScanExpr(ret.Value);
                    break;
                case IfStmt ifs:
                    ScanExpr(ifs.Condition);
                    foreach (var (cond, _) in ifs.ElifBranches) ScanExpr(cond);
                    break;
                case WhileStmt wh:
                    ScanExpr(wh.Condition);
                    break;
                case AugAssignStmt aug:
                    ScanExpr(aug.Target);
                    ScanExpr(aug.Value);
                    break;
                case ForStmt fr:
                    ScanExpr(fr.RangeStart); ScanExpr(fr.RangeStop); ScanExpr(fr.RangeStep);
                    ScanExpr(fr.Iterable);
                    break;
                case WithStmt wi:
                    ScanExpr(wi.ContextExpr);
                    break;
                case MatchStmt m:
                    ScanExpr(m.Target);
                    foreach (var br in m.Branches) { ScanExpr(br.Pattern); ScanExpr(br.Guard); }
                    break;
                case TupleUnpackStmt tu:
                    ScanExpr(tu.Value);
                    break;
            }
        }

        foreach (var name in boundEmpty)
        {
            if (!appended.Contains(name)) continue;
            string key = prefix + name;
            // The key must be the one the promotion emit settles on: a
            // module-level `x = []` is a GLOBAL filed bare (`x`), not under
            // `main.x` -- the same remap CollectArrayDecls and EmitListAnnAssign
            // apply. Filing the prefixed name here left a phantom entry that
            // ResolveListVarQualified prefers over the real slot, so len(x)
            // dereferenced a header nobody writes and read 0 while x.append()
            // and x[i] used the right one.
            // Only a name this body declares global: a function's local that shares a module
            // global's name is a local (the same rule EmitListAnnAssign applies).
            if (!string.IsNullOrEmpty(currentFunction)
                && string.IsNullOrEmpty(currentInlinePrefix)
                && (currentFunction == "main" || declaredGlobals.Contains(name))
                && mutableGlobals.ContainsKey(currentModulePrefix + name))
                key = currentModulePrefix + name;
            promotableEmptyLists.Add(key);
            // Seed the pending registration NOW, before the body lowers: an
            // `x = []` inside one arm of an `if` would otherwise introduce the
            // name where the pre-branch snapshot lacks it, and the all-agree
            // join at the merge would veto the element-type entry -- the
            // append after the `if` (adafruit_irremote's `received`) then met
            // the untyped-[] refusal all over. The slot exists for the whole
            // function once the promotion emits; the name may claim its
            // pending list-ness from the start. A declaration that already
            // filed the element type (`x: list[uint8] = []`) keeps it: the
            // type is not pending, it is declared.
            if (!listVarElemTypes.ContainsKey(key))
                listVarElemTypes[key] = DataType.UNKNOWN;
        }
    }
}

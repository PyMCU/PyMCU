using PyMCU.Common;
using PyMCU.Frontend;
using PyMCU.IR;

namespace PyMCU.IR.IRGenerator;

// SPDX-License-Identifier: MIT
// RFC 0006 direction, RFC 0009 enabler: bound-instance outlining.
//
// A force-inline instance method expands its whole body at every call site
// because its fields have no Model A/B ABI: `matrix.show()` on a driver with a
// buffer or a child instance pastes the loop, the writeto and the field reads
// into main once per call (44 expansions on the ht16k33 matrix demandant --
// about 15KB of flash on a 32KB part).
//
// When the receiver is a MODULE-LEVEL instance the fields already have global
// storage (`matrix__buffer`), so the body can compile once as an ordinary
// subroutine: `self` is bound to the instance's global name, user parameters
// become real ABI parameters, and each call site emits a plain CALL. This is
// also what lets a driver's `Optional[...]` parameter ride the tag byte the
// RFC 0009 section-10 ABI defines -- an expanded body has no parameter slots
// to put it in.
//
// Correctness rule: inside an expansion `self._auto_write` folds the CURRENT
// compile-time value, so a field mutated between two calls is seen per site.
// A shared body compiles once -- folding a mutable field there bakes the
// first call's answer into every later call. So a field any method (or module
// statement) can write is stripped from the constant tables for the duration
// of the visit and given real storage plus a preamble store of the value the
// constructor folded. Immutable fields keep folding exactly as they did.

public partial class IRGenerator
{
    // (method callee + "|" + instance name) -> synthesized subroutine name;
    // the empty string memoizes a refusal so a hot call site does not re-walk
    // the eligibility checks for every `matrix[...] = 1`.
    private readonly Dictionary<string, string> boundMethodSynths = new();
    // Fields a method can write, per class key: the set a bound body must read
    // from storage instead of folding. Computed once per class.
    private readonly Dictionary<string, HashSet<string>> classMutableFields = new();
    // `Copy(constant, inst_field)` for a mutable field whose constructor value
    // only ever lived in the constant tables: the subroutine reads storage, so
    // the value the expansion would have folded has to arrive in SRAM before
    // the program can call it. Emitted at the head of main.
    private readonly List<Instruction> boundFieldInitPreamble = new();
    // (callee|instance) -> eligibility decision, computed once per pair. Kept
    // separate from boundMethodSynths because synthesis must be LAZY: a pair
    // whose every call site passes a constant to a fold-sensitive parameter
    // keeps the expansion everywhere, and a synthesized-but-uncalled body would
    // still contribute its instructions to the program.
    private readonly Dictionary<string, (bool ok, string cls, HashSet<string> strip,
        HashSet<string> foldPs)> boundEligibility = new();

    /// <summary>
    /// The subroutine name for (<paramref name="callee"/>, <paramref name="instName"/>),
    /// synthesizing the bound body on first use, or null when the pair cannot share a
    /// body -- or THIS call site cannot share it -- and the call keeps the
    /// force-inline expansion.
    /// </summary>
    private string? BoundMethodCallee(string callee, string instName, FunctionDef func,
                                      IReadOnlyList<Expression> args)
    {
        // The receiver name reaching this point can be an inline frame's `self`
        // (`inline49__pixel_self` for `self.show()` inside an expansion): every site
        // then names a different "instance" and no body is ever shared. The alias
        // chain ends at the real instance -- `matrix` -- which is the name the
        // eligibility check and the memo key both want.
        for (int d = 0; d < 20 && variableAliases.TryGetValue(instName, out var root); ++d)
            instName = root;
        string memoKey = callee + "|" + instName;
        if (!boundEligibility.TryGetValue(memoKey, out var dec))
        {
            dec.ok = BoundMethodEligible(callee, instName, func, out dec.cls,
                                         out dec.strip, out dec.foldPs);
            boundEligibility[memoKey] = dec;
        }
        if (!dec.ok) return null;
        // A param the body uses as a subscript index on a real array field folds
        // to a constant-indexed ArrayLoad when the call site passes a literal --
        // the shape the tuple-field tests pin. Keep the expansion at just those
        // sites; a runtime argument emits the same load either way.
        if (dec.foldPs.Count > 0)
        {
            int pos = 0;
            foreach (var a in args)
            {
                string pname;
                Expression val;
                if (a is KeywordArgExpr kw) { pname = kw.Key; val = kw.Value; }
                else
                {
                    int pi = pos + 1;
                    pname = pi < func.Params.Count ? func.Params[pi].Name : "";
                    val = a;
                    ++pos;
                }
                if (pname.Length > 0 && dec.foldPs.Contains(pname)
                    && BoundCompileTimeConst(val))
                    return null;
            }
        }
        if (!boundMethodSynths.TryGetValue(memoKey, out var memo))
        {
            memo = SynthesizeBoundMethod(callee, instName, dec.cls, func, dec.strip) ?? "";
            boundMethodSynths[memoKey] = memo;
        }
        return memo.Length > 0 ? memo : null;
    }

    /// Whether (<paramref name="callee"/>, <paramref name="instName"/>) can share one
    /// compiled body. Everything here mirrors the reasons a shared subroutine cannot
    /// reproduce what a per-site expansion binds: an instance parameter (no ABI slot),
    /// a tuple or class return (nothing to return through), `*args`/`**kwargs` (known
    /// only at the call site), a bare `self` value, and a mutable field whose compile-
    /// time binding the body would need (a sequence to unroll, an instance to call on).
    private bool BoundMethodEligible(string callee, string instName, FunctionDef func,
                                     out string cls, out HashSet<string> stripFields,
                                     out HashSet<string> foldPs)
    {
        cls = "";
        stripFields = new HashSet<string>();
        foldPs = new HashSet<string>();
        // A whole instance name only: a `main.m` local, a parameter or a nested
        // field path has no global storage for the body to reach. The check is the
        // name's shape, not where the statement sits -- a module-level `while` binds
        // its names bare, which is exactly what the demandants do.
        if (instName.Length == 0 || instName.IndexOf('.') >= 0) return false;
        if (!instanceClasses.TryGetValue(instName, out var instCls) || instCls == null)
            return false;
        cls = !classFieldLayout.ContainsKey(instCls) && ResolveConcreteClass(instCls) is { } cc
            ? cc : instCls;

        // A slot class already outlines its methods as real subroutines with a `self`
        // pointer: `self.f` inside one stores through that pointer at a byte offset, an
        // ABI a flattened-global body cannot reproduce -- and one built while the
        // instance is still under construction has no slot to point at yet anyway
        // (d = I2C() synthesized `_bound_d__reset` mid-ctor, wrote `d__last`, and the
        // slot it should have stored stayed at its init value, #373). Keeping the
        // expansion is also free: the outlined body is shared already.
        if (slotClasses.Contains(cls)) return false;

        // Constructors build the instance -- they must keep running per site.
        // Descriptors receive `obj` per call site; __del__ is not a thing here.
        if (func.Name is "__init__" or "__del__" or "__get__" or "__set__") return false;
        if (FunctionHasYield(func) || func.IsInterrupt || func.IsExtern || func.IsNaked
            || func.IsExportC || func.IsPioProgram || func.Params.Count == 0)
            return false;
        if (!BoundParamUseSafe(func)) return false;

        // Every parameter after the receiver needs a real ABI slot: a scalar, a
        // buffer pointer, or a union whose members all fit a payload byte. A tuple
        // or instance parameter is bound per call site and cannot ride the ABI.
        for (int i = 1; i < func.Params.Count; ++i)
        {
            var p = func.Params[i];
            if (p.IsVarArg || p.IsKwArg) return false;
            if (p.UnionMembers is { } um)
            {
                if (!ParamUnionMembersTaggable(p)) return false;
                continue;
            }
            string t = p.Type ?? "";
            if (t == "bool" || t == "bytearray" || t == "bytes" || t == "memoryview"
                || t == "str" || t == "const[str]" || t == "ptr" || t.StartsWith("ptr[")
                || DataTypeExtensions.StringToDataType(t) != DataType.UNKNOWN)
                continue;
            return false;
        }

        // The return has to have an ABI slot too: scalar, void, or a taggable union.
        if (func.ReturnMembers is { } rm)
        {
            if (!rm.All(m => m == "None"
                    || (MemberDataType(m) != DataType.UNKNOWN
                        && m is not ("bytearray" or "bytes" or "str" or "const[str]")
                        && !m.Contains('['))))
                return false;
        }
        else
        {
            string rt = (func.ReturnType ?? "").Trim().Trim('"');
            if (TupleType.ElementTypes(rt).Count > 0) return false;
            if (rt is not ("" or "void" or "None" or "bool" or "bytearray" or "bytes"
                           or "memoryview" or "str" or "const[str]")
                && DataTypeExtensions.StringToDataType(rt) == DataType.UNKNOWN)
                return false;
        }

        if (!BoundBodySafe(func)) return false;

        // Fields the body touches through `self.` that can also be written
        // elsewhere must read storage, not a folded value -- the synthesis strips
        // those constants. A mutable field whose compile-time binding cannot be
        // re-materialized (a sequence to unroll, an instance to call on, a string)
        // keeps the expansion instead.
        var mutable = ClassMutableFields(cls);
        foreach (var mf in moduleInstanceMutableFields)
            if (mf.StartsWith(instName + "_", StringComparison.Ordinal))
                mutable.Add(mf[(instName.Length + 1)..]);
        var touched = TouchedSelfFields(func, cls, instName, out bool foldSafe,
                                        out foldPs);
        if (!foldSafe) return false;
        foreach (var f in touched)
        {
            if (!mutable.Contains(f)) continue;
            string flat = BoundFieldFlatName(instName, cls, f);
            if (constSequenceBindings.ContainsKey(flat) || instanceClasses.ContainsKey(flat)
                || strConstantVariables.ContainsKey(flat) || dictLiteralBindings.ContainsKey(flat))
                return false;
            stripFields.Add(f);
        }
        return true;
    }

    /// <summary>
    /// No use of the receiver as a VALUE -- `f(self)`, `return self`, `x = self` --
    /// and no construct a shared body cannot carry (yield, a CompileError stub,
    /// a nested def). `self.<anything>` is fine: it resolves to the instance's
    /// global storage through the self alias.
    /// </summary>
    private bool BoundBodySafe(FunctionDef func)
    {
        string selfName = func.Params[0].Name;
        bool safe = true;

        void E(Expression? e)
        {
            if (!safe || e == null) return;
            switch (e)
            {
                // `self.x` / `self.m(...)`: the member access IS the field or the
                // call receiver -- descend into the object only when the object is
                // itself composite (`self.a.b`).
                case MemberAccessExpr { Object: VariableExpr sv } ma when sv.Name == selfName:
                    return;
                case MemberAccessExpr ma2: E(ma2.Object); return;
                // `Base.m(self, args)`: the leading receiver is not a passed value.
                case CallExpr { Callee: MemberAccessExpr { Object: VariableExpr baseVe } } bc
                        when classNames.Contains(baseVe.Name)
                             && bc.Args.Count > 0
                             && bc.Args[0] is VariableExpr { Name: var rn } && rn == selfName:
                    for (int ai = 1; ai < bc.Args.Count; ++ai) E(bc.Args[ai]);
                    return;
                case CallExpr c: E(c.Callee); foreach (var a in c.Args) E(a); return;
                case VariableExpr ve: if (ve.Name == selfName) safe = false; return;
                case KeywordArgExpr kw: E(kw.Value); return;
                case IndexExpr ix: E(ix.Target); E(ix.Index); return;
                case BinaryExpr b: E(b.Left); E(b.Right); return;
                case UnaryExpr u: E(u.Operand); return;
                case TernaryExpr t: E(t.Condition); E(t.TrueVal); E(t.FalseVal); return;
                case TupleExpr tu: foreach (var el in tu.Elements) E(el); return;
                case ListExpr le: foreach (var el in le.Elements) E(el); return;
                case FStringExpr fs: foreach (var p in fs.Parts) E(p.Expr); return;
                case NoneLiteral: case IntegerLiteral: case FloatLiteral:
                case BooleanLiteral: case StringLiteral: return;
                default: return; // unknown expression node: no self inside that we know of
            }
        }

        foreach (var st in TypeInference.WalkStatements(func.Body.Statements))
        {
            if (!safe) break;
            switch (st)
            {
                case AssignStmt a: E(a.Target); E(a.Value); break;
                case AugAssignStmt aug: E(aug.Target); E(aug.Value); break;
                case AnnAssign an: E(an.Value); break;
                case VarDecl vd: E(vd.Init); break;
                case ExprStmt ex: E(ex.Expr); break;
                case ReturnStmt r: E(r.Value); break;
                case IfStmt iff:
                    E(iff.Condition);
                    foreach (var br in iff.ElifBranches) E(br.Condition);
                    break;
                case WhileStmt wh: E(wh.Condition); break;
                case ForStmt f:
                    E(f.Iterable); E(f.RangeStart); E(f.RangeStop); E(f.RangeStep);
                    break;
                case RaiseStmt r when r.ErrorType == "CompileError": safe = false; break;
                case RaiseStmt r: E(r.MessageExpr); break;
                case FunctionDef: case ClassDef: safe = false; break;
                default: break;
            }
        }
        return safe;
    }

    /// <summary>
    /// A real subroutine's parameter is a register/stack slot: it can be read,
    /// written and passed on, but it has no compile-time sequence for `len()` to
    /// measure, `for` to unroll or a slice to copy, and no members to reach. An
    /// unannotated `x = None` default is refused for the same reason a shared body
    /// cannot fold `x is None`: expansion binds None-ness per call site, a shared
    /// body compiles the check once. An annotated `Optional[...]` default is fine --
    /// it carries a tag or a proven member like any other real function.
    /// </summary>
    private static bool BoundParamUseSafe(FunctionDef func)
    {
        var pnames = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 1; i < func.Params.Count; ++i)
        {
            var p = func.Params[i];
            if (p.DefaultValue is NoneLiteral && p.UnionMembers == null) return false;
            pnames.Add(p.Name);
        }
        if (pnames.Count == 0) return true;

        bool safe = true;
        bool Param(Expression? e) => e is VariableExpr v && pnames.Contains(v.Name);
        void E(Expression? e)
        {
            if (!safe || e == null) return;
            switch (e)
            {
                // `len(p)`/`enumerate(p)`/`zip(.., p, ..)`: a sequence builtin needs
                // the parameter's compile-time length, which a register slot lacks.
                case CallExpr { Callee: VariableExpr cv } c
                        when SeqBuiltins.Contains(cv.Name)
                             && c.Args.Any(a => Param(a is KeywordArgExpr kw ? kw.Value : a)):
                    safe = false;
                    return;
                case CallExpr c: E(c.Callee); foreach (var a in c.Args) E(a); return;
                // `p[a:b]`: a slice copies from a named array's storage, not a slot.
                case IndexExpr { Index: SliceExpr, Target: VariableExpr itv } when pnames.Contains(itv.Name):
                    safe = false;
                    return;
                // `p.x`/`p.m(...)`: a member of a parameter -- the slot holds a
                // scalar or a pointer, never an instance with members to reach.
                case MemberAccessExpr { Object: VariableExpr mv } when pnames.Contains(mv.Name):
                    safe = false;
                    return;
                case MemberAccessExpr m2: E(m2.Object); return;
                case IndexExpr ix: E(ix.Target); E(ix.Index); return;
                case KeywordArgExpr kw: E(kw.Value); return;
                case BinaryExpr b: E(b.Left); E(b.Right); return;
                case UnaryExpr u: E(u.Operand); return;
                case TernaryExpr t: E(t.Condition); E(t.TrueVal); E(t.FalseVal); return;
                case TupleExpr tu: foreach (var el in tu.Elements) E(el); return;
                case ListExpr le: foreach (var el in le.Elements) E(el); return;
                case FStringExpr fs: foreach (var p2 in fs.Parts) E(p2.Expr); return;
                default: return;
            }
        }
        foreach (var st in TypeInference.WalkStatements(func.Body.Statements))
        {
            if (!safe) break;
            switch (st)
            {
                case AssignStmt a: E(a.Target); E(a.Value); break;
                case AugAssignStmt aug: E(aug.Target); E(aug.Value); break;
                case AnnAssign an: E(an.Value); break;
                case VarDecl vd: E(vd.Init); break;
                case ExprStmt ex: E(ex.Expr); break;
                case ReturnStmt r: E(r.Value); break;
                case IfStmt iff:
                    E(iff.Condition);
                    foreach (var br in iff.ElifBranches) E(br.Condition);
                    break;
                case WhileStmt wh: E(wh.Condition); break;
                case ForStmt f:
                    if (Param(f.Iterable)) safe = false;
                    E(f.Iterable); E(f.RangeStart); E(f.RangeStop); E(f.RangeStep);
                    break;
                case RaiseStmt r: E(r.MessageExpr); break;
                default: break;
            }
        }
        return safe;
    }

    // Sequence-consuming builtins whose argument must have a compile-time length
    // or element list -- none of which a parameter slot carries.
    private static readonly HashSet<string> SeqBuiltins = new(StringComparer.Ordinal)
    {
        "len", "enumerate", "reversed", "sorted", "zip", "sum", "min", "max",
        "list", "tuple", "set", "iter", "bytes", "bytearray", "memoryview",
        "map", "filter", "all", "any",
    };

    /// <summary>
    /// Every field name the body reaches through `self.` -- read, write, iterate or
    /// call a member of -- TRANSITIVELY: `self.m()` / `super().m()` /
    /// `Base.m(self, ...)` bodies are walked too, because a shared body compiles
    /// the callee's `self.f` reads once, exactly like its own. A mutable field
    /// reached only through a nested call would otherwise keep its folded value.
    /// Used to decide which folded values a shared body may keep: a field nobody
    /// touches folds or not without consequence, so it is left alone.
    /// </summary>
    private HashSet<string> TouchedSelfFields(FunctionDef func, string cls, string instName,
                                            out bool foldSafe, out HashSet<string> foldParams)
    {
        var touched = new HashSet<string>();
        var seen = new HashSet<FunctionDef>();
        var work = new Stack<(FunctionDef def, string defCls, bool top)>();
        bool safe = true;
        var foldPs = new HashSet<string>();
        work.Push((func, cls, true));
        while (work.Count > 0)
        {
            var (cur, curCls, top) = work.Pop();
            if (!seen.Add(cur)) continue;
            CollectTouchedFields(cur, curCls, instName, top, touched, work, ref safe, foldPs);
        }
        foldSafe = safe;
        foldParams = foldPs;
        return touched;
    }

    /// One method body's contribution to <see cref="TouchedSelfFields"/>: the
    /// `self.` members it reaches, plus the bodies of the methods it calls on
    /// `self` (direct, super() or the unbound spelling) pushed for their turn.
    /// While it walks, it also answers the OTHER question a shared body must get
    /// right: whether anything in the body only works because the expansion
    /// re-binds the call site's constants. Those uses set
    /// <paramref name="foldSafe"/> false and the caller keeps the expansion:
    ///
    ///  - `self.m[i]` / `x in self.m` where m is a compile-time collection (a
    ///    class dict, a tuple field, a string constant). Expansion folds the
    ///    argument or loop variable into the index and the lookup into a
    ///    constant; a shared body reads a runtime slot and emits a compare
    ///    chain -- correct value, wrong program shape (the unroll tests pin
    ///    the fold, and the flash it saves is the point).
    ///  - `self.m` handed across a boundary it cannot cross: `return self.m`,
    ///    `f(self.m)`, `x = self.m` where m holds an instance or a class-level
    ///    descriptor. The per-site machinery binds the instance's CLASS to the
    ///    result name; a shared body can only return a scalar.
    ///  - `with ...` inside the body: the bound name's class comes from the
    ///    `__enter__` result, resolved per site.
    private void CollectTouchedFields(FunctionDef func, string curCls, string instName,
        bool isTopLevel, HashSet<string> touched,
        Stack<(FunctionDef, string, bool)> work, ref bool foldSafe,
        HashSet<string> foldParams)
    {
        string selfName = func.Params.Count > 0 ? func.Params[0].Name : "self";
        // A local the nested T captures; ref/out parameters cannot be captured by
        // a local function. Copied back into <paramref name="foldSafe"/> at the end.
        bool safe = foldSafe;

        // Parameter names of THIS body appearing in an expression: used for
        // fold-sensitive params, which only exist on the top-level body (a nested
        // body's params bind inside the shared subroutine and are never folded
        // per call site).
        void CollectParamUses(Expression? e)
        {
            switch (e)
            {
                case null: return;
                case VariableExpr v:
                    for (int i = 1; i < func.Params.Count; ++i)
                        if (func.Params[i].Name == v.Name) { foldParams.Add(v.Name); break; }
                    return;
                case IndexExpr ix: CollectParamUses(ix.Target); CollectParamUses(ix.Index); return;
                case MemberAccessExpr m: CollectParamUses(m.Object); return;
                case CallExpr c: CollectParamUses(c.Callee); foreach (var a in c.Args) CollectParamUses(a); return;
                case KeywordArgExpr kw: CollectParamUses(kw.Value); return;
                case BinaryExpr b: CollectParamUses(b.Left); CollectParamUses(b.Right); return;
                case UnaryExpr u: CollectParamUses(u.Operand); return;
                case TernaryExpr t: CollectParamUses(t.Condition); CollectParamUses(t.TrueVal); CollectParamUses(t.FalseVal); return;
                case TupleExpr tu: foreach (var el in tu.Elements) CollectParamUses(el); return;
                case ListExpr le: foreach (var el in le.Elements) CollectParamUses(el); return;
                case FStringExpr fs: foreach (var p in fs.Parts) CollectParamUses(p.Expr); return;
                default: return;
            }
        }

        void T(Expression? e, bool leafValue = true)
        {
            if (e == null) return;
            if (e is MemberAccessExpr { Object: VariableExpr sv } ma && sv.Name == selfName)
            {
                touched.Add(ma.Member);
                // A @property/`__get__`-style member is a method in disguise: its
                // body's own `self.f` reads are this body's reads too.
                if (BoundLookupProperty(curCls, ma.Member) is { } propCall)
                    work.Push((propCall.def, propCall.cls, false));
                switch (BoundMemberKind(curCls, instName, ma.Member))
                {
                    // A class-level instance attribute is a descriptor or a plain
                    // object: reaching it goes through the __get__ rewrite, which
                    // binds the receiver per site. A shared body has nothing to
                    // dispatch on -- refuse in every position, store included.
                    case BoundMember.ClassAttrInstance:
                        safe = false;
                        return;
                    // A class-level collection (class dict/set/tuple) folded as a
                    // literal can only live in the constant tables; handing the
                    // name across an expression boundary needs the literal, not
                    // a slot. Subscripting it is the IndexExpr case below.
                    case BoundMember.ClassAttrCollection when leafValue:
                        safe = false;
                        return;
                }
                // An instance-valued field (`self._mgr`) flattens to a name only
                // the expansion can hand to another binder: returning it or
                // passing it as a value needs the class tag a scalar cannot
                // carry. As the object of `.f`/`.m()`/`[i]` it stays a name --
                // the flattened storage is global, so those positions are fine.
                if (leafValue && BoundInstanceValuedField(instName, curCls, ma.Member))
                    safe = false;
                return;
            }
            switch (e)
            {
                // `self.m[i]` (read or store): subscripting a field by a runtime
                // value loses the fold the expansion produces -- the argument or
                // loop variable binds constant per site, and `self.scale[1]` is
                // a Constant-indexed ArrayLoad the tests pin. Compile-time
                // collections fold or compare-chain -- a different SHAPE -- so
                // they refuse at any depth. A real array emits the same
                // ArrayLoad either way; the only thing lost is the constant fold
                // when THIS call site passes a literal for a param the index
                // expression names. Those params are recorded in
                // <paramref name="foldParams"/> so BoundMethodCallee keeps the
                // expansion just at const-arg sites (`d.at(1)`), while runtime
                // sites still share the body (`_pixel`'s `self._buffer[addr]`,
                // where addr is a local, marks nothing and shares always).
                case IndexExpr { Target: MemberAccessExpr { Object: VariableExpr iv } im,
                                 Index: var idx }
                        when iv.Name == selfName:
                    touched.Add(im.Member);
                    if (!BoundCompileTimeConst(idx))
                    {
                        if (BoundCompileTimeCollection(curCls, instName, im.Member))
                            safe = false;
                        else if (isTopLevel
                                 && BoundRealArrayField(curCls, instName, im.Member))
                            CollectParamUses(idx);
                    }
                    T(idx);
                    return;
                // `x in self.m` on a compile-time collection folds per site; a
                // shared body pays a compare chain for a runtime needle.
                case BinaryExpr { Op: Frontend.BinaryOp.In or Frontend.BinaryOp.NotIn,
                                  Right: MemberAccessExpr { Object: VariableExpr rv } rm } bx
                        when rv.Name == selfName:
                    touched.Add(rm.Member);
                    if (BoundCompileTimeCollection(curCls, instName, rm.Member)
                        && !BoundCompileTimeConst(bx.Left))
                        safe = false;
                    T(bx.Left);
                    return;
                // `self.m(...)`: the callee's own `self.` reads are this body's
                // reads once it expands inside the shared body.
                case CallExpr { Callee: MemberAccessExpr { Object: VariableExpr cv } cm } cc
                        when cv.Name == selfName:
                    if (BoundLookupMethod(curCls, cm.Member) is { } selfCall)
                        work.Push((selfCall.def, selfCall.cls, false));
                    // `self.f(...)` where `f` is a field bound to `obj.method`:
                    // the call re-dispatches through the receiver recorded at the
                    // bind site. A shared body can name that receiver only when
                    // it is module-level storage -- a function-scoped held
                    // instance (`main.__c1`) resolves to caller temps the body
                    // would read as phantoms.
                    else if (boundMethodFields.TryGetValue(instName + "_" + cm.Member, out var bmField)
                             && !BoundMethodRecvShareable(bmField.Recv))
                        safe = false;
                    foreach (var a in cc.Args) T(a);
                    return;
                // `super().m(...)`: the base of the CLASS THE BODY IS WRITTEN ON
                // -- MRO semantics, and the override's own fields join through
                // the recursive walk either way.
                case CallExpr { Callee: MemberAccessExpr
                        { Object: CallExpr { Callee: VariableExpr { Name: "super" } },
                          Member: var superMember } } sc:
                    if (BoundBaseOf(curCls) is { } baseCls
                        && BoundLookupMethod(baseCls, superMember) is { } superCall)
                        work.Push((superCall.def, superCall.cls, false));
                    foreach (var a in sc.Args) T(a);
                    return;
                // `Base.m(self, ...)`: the unbound spelling -- the receiver is the
                // first argument, the method lives on the named class.
                case CallExpr { Callee: MemberAccessExpr { Object: VariableExpr bv } bm } bc
                        when bc.Args.Count > 0
                             && bc.Args[0] is VariableExpr { Name: var rn } && rn == selfName:
                    if (BoundLookupMethod(bv.Name, bm.Member) is { } unboundCall)
                        work.Push((unboundCall.def, unboundCall.cls, false));
                    for (int ai = 1; ai < bc.Args.Count; ++ai) T(bc.Args[ai]);
                    return;
                // `self.m.f` / `self.m[i]` where m is itself a member: the inner
                // `self.m` is the access's OBJECT, not a leaf value -- instance-
                // valued fields resolve through their flattened global here. A
                // METHOD under it expands per usual, but a DATA member resolves
                // through the held instance's folded bindings -- constants and
                // bare globals fold identically in a shared body, while a
                // compiler temp (`main.__c1_v`) names a slot only the call
                // site's own scope materialized.
                case MemberAccessExpr m2:
                    if (m2.Object is MemberAccessExpr { Object: VariableExpr ov2,
                                                        Member: var heldFld }
                        && ov2.Name == selfName
                        && BoundInstanceValuedField(instName, curCls, heldFld))
                    {
                        touched.Add(heldFld);
                        string heldCls = instanceClasses[
                            BoundFieldFlatName(instName, curCls, heldFld)]!;
                        if (BoundLookupMethod(heldCls, m2.Member) == null
                            && BoundLookupProperty(heldCls, m2.Member) == null
                            && !BoundNestedLeafShareable(instName, curCls, heldFld, m2.Member))
                            safe = false;
                    }
                    T(m2.Object, false); return;
                case CallExpr c: T(c.Callee); foreach (var a in c.Args) T(a); return;
                case KeywordArgExpr kw: T(kw.Value); return;
                case IndexExpr ix: T(ix.Target, false); T(ix.Index); return;
                case BinaryExpr b: T(b.Left); T(b.Right); return;
                case UnaryExpr u: T(u.Operand); return;
                case TernaryExpr t: T(t.Condition); T(t.TrueVal); T(t.FalseVal); return;
                case TupleExpr tu: foreach (var el in tu.Elements) T(el); return;
                case ListExpr le: foreach (var el in le.Elements) T(el); return;
                case FStringExpr fs: foreach (var p in fs.Parts) T(p.Expr); return;
                default: return;
            }
        }
        foreach (var st in TypeInference.WalkStatements(func.Body.Statements))
        {
            switch (st)
            {
                case AssignStmt a:
                    // `self.m = v` where m holds an instance rebinds the field's
                    // class per site; a shared body would store a scalar into a
                    // name it cannot re-type.
                    if (a.Target is MemberAccessExpr { Object: VariableExpr tv } tm
                        && tv.Name == selfName
                        && BoundInstanceValuedField(instName, curCls, tm.Member))
                        safe = false;
                    T(a.Target, false); T(a.Value); break;
                case AugAssignStmt aug: T(aug.Target, false); T(aug.Value); break;
                case AnnAssign an: T(an.Value); break;
                case VarDecl vd: T(vd.Init); break;
                case ExprStmt ex: T(ex.Expr); break;
                case ReturnStmt r: T(r.Value); break;
                case IfStmt iff:
                    T(iff.Condition);
                    foreach (var br in iff.ElifBranches) T(br.Condition);
                    break;
                case WhileStmt wh: T(wh.Condition); break;
                case ForStmt f:
                    T(f.Iterable, false); T(f.RangeStart); T(f.RangeStop); T(f.RangeStep);
                    break;
                // `with self.m as x`: the bound name's class is the `__enter__`
                // result resolved per site -- the with-bound name loses it in a
                // shared body (#390 sibling). A bare `with obj:` binds nothing
                // (show()'s `with i2c_dev:`), so only the AS form refuses.
                case WithStmt w:
                    T(w.ContextExpr, false);
                    if (!string.IsNullOrEmpty(w.AsName)) safe = false;
                    break;
                case RaiseStmt r: T(r.MessageExpr); break;
                default: break;
            }
        }
        foldSafe = safe;
    }

    /// What a `self.<member>` access resolves to when the member is not a real
    /// instance field: a class-level attribute's storage class, so the caller can
    /// tell "folds identically in a shared body" (a scalar constant or class
    /// variable) from "bound per call site" (a descriptor instance, a literal
    /// collection).
    private enum BoundMember { NotClassAttr, ScalarAttr, ClassAttrInstance, ClassAttrCollection }

    private BoundMember BoundMemberKind(string cls, string instName, string member)
    {
        if (!TryFindClassAttribute(instName, member, out _, out var key)) return BoundMember.NotClassAttr;
        if (instanceClasses.ContainsKey(key)) return BoundMember.ClassAttrInstance;
        if (dictLiteralBindings.ContainsKey(key) || setLiteralBindings.ContainsKey(key)
            || constSequenceBindings.ContainsKey(key) || strConstantVariables.ContainsKey(key)
            || constantAddressVariables.ContainsKey(key))
            return BoundMember.ClassAttrCollection;
        return BoundMember.ScalarAttr;
    }

    /// True when `self.<member>` on <paramref name="instName"/> holds another
    /// instance -- `inst_member` is a flattened instance name, not a scalar.
    /// The single-field collapse is excluded on purpose: there `inst_member`
    /// resolves to the instance's own name, which is the scalar field itself,
    /// not a nested instance.
    private bool BoundInstanceValuedField(string instName, string cls, string member)
    {
        string flat = BoundFieldFlatName(instName, cls, member);
        return flat != instName && instanceClasses.ContainsKey(flat);
    }

    /// True when `self.<member>`'s value lives in a compile-time collection
    /// table (dict, set, tuple, string, address literal) rather than storage --
    /// an instance field under `inst_member`, or a class attribute under its
    /// `Cls_member` spelling.
    private bool BoundCompileTimeCollection(string cls, string instName, string member)
    {
        string flat = BoundFieldFlatName(instName, cls, member);
        if (constSequenceBindings.ContainsKey(flat) || dictLiteralBindings.ContainsKey(flat)
            || setLiteralBindings.ContainsKey(flat) || strConstantVariables.ContainsKey(flat)
            || constantAddressVariables.ContainsKey(flat))
            return true;
        return BoundMemberKind(cls, instName, member) == BoundMember.ClassAttrCollection;
    }

    /// Whether `self.<field>.<leaf>` names something a shared body can reach:
    /// a compile-time binding (constant, sequence, held class) or real global
    /// storage. What it cannot reach is a local -- the folded bindings of a
    /// held instance live under compiler temps (`main.__c1_v`) that exist only
    /// in the scope the constructor ran in.
    private bool BoundNestedLeafShareable(string instName, string cls, string field, string leaf)
    {
        string fldFlat = BoundFieldFlatName(instName, cls, field);
        string b = fldFlat;
        for (int d = 0; d < 20 && variableAliases.TryGetValue(b, out var r); d++) b = r;
        foreach (var cand in new[] { fldFlat + "_" + leaf, b + "_" + leaf })
        {
            string n = cand;
            for (int d = 0; d < 20 && variableAliases.TryGetValue(n, out var a); d++) n = a;
            if (BoundShareableName(n)) return true;
        }
        return false;
    }

    /// True when a recorded bound-method receiver names storage a synthesized
    /// body can reach: a module-level instance (`pin`, `matrix`) or a
    /// field-path under one (`b__ow`). A function-scoped held instance key
    /// (`main.__c1`) carries a `.` prefix -- its flattened fields live in the
    /// caller's frame, so the shared body must refuse it.
    private bool BoundMethodRecvShareable(string recv)
    {
        // The recorded key is a FIELD path (`b__ow`); the instance it holds
        // sits at the end of the alias chain -- chase it to the terminal key
        // the emitted call would actually name.
        for (int d = 0; d < 20 && variableAliases.TryGetValue(recv, out var r); d++) recv = r;
        if (recv.IndexOf('.') >= 0) return false;
        if (topLevelInstanceTargets.Contains(recv)) return true;
        foreach (var inst in topLevelInstanceTargets)
            if (recv.StartsWith(inst + "_", StringComparison.Ordinal)) return true;
        return false;
    }

    /// A `<inst>_*` flat name is module storage under a module-level instance
    /// (nested fields share the prefix: `b__ow_x`, `px__buf__arena_len`). Only
    /// called on names without a `.` -- dotted heads are resolved by the audit.
    private bool BoundModuleStorageSpelling(string n)
    {
        foreach (var inst in topLevelInstanceTargets)
            if (n == inst || n.StartsWith(inst + "_", StringComparison.Ordinal)) return true;
        return false;
    }

    private bool BoundShareableName(string n) =>
        constantVariables.ContainsKey(n) || floatConstantVariables.ContainsKey(n)
        || strConstantVariables.ContainsKey(n) || constSequenceBindings.ContainsKey(n)
        || dictLiteralBindings.ContainsKey(n) || setLiteralBindings.ContainsKey(n)
        || constantAddressVariables.ContainsKey(n) || noneValuedNames.Contains(n)
        || narrowedOptionals.ContainsKey(n) || instanceClasses.ContainsKey(n)
        || globals.ContainsKey(n) || mutableGlobals.ContainsKey(n)
        || arraySizes.ContainsKey(n) || moduleSramArrays.Contains(n);

    /// True when `self.<member>` is a real array in storage (SRAM or a view
    /// window) -- as opposed to a compile-time collection literal. The two are
    /// gated differently: a runtime index into a collection changes the emitted
    /// SHAPE (fold vs compare chain), while a runtime index into an array emits
    /// the same ArrayLoad the expansion would, so the refusal only applies to
    /// the outermost body's own index expressions.
    private bool BoundRealArrayField(string cls, string instName, string member)
    {
        string flat = BoundFieldFlatName(instName, cls, member);
        return arraySizes.ContainsKey(flat) || moduleSramArrays.Contains(flat)
            || arraysWithVariableIndex.Contains(flat) || arrayViewBase.ContainsKey(flat);
    }

    /// A subscript index or membership needle the shared body folds exactly like
    /// the expansion does: a literal, or a name already bound to a constant for
    /// every caller (a module-level const). Anything else -- a parameter, a loop
    /// variable, a computed value -- reads a runtime slot, and a compile-time
    /// collection subscripted by it loses the fold.
    private bool BoundCompileTimeConst(Expression e) => e switch
    {
        IntegerLiteral or BooleanLiteral or FloatLiteral => true,
        VariableExpr v => constantVariables.ContainsKey(v.Name) || globals.ContainsKey(v.Name),
        UnaryExpr u => BoundCompileTimeConst(u.Operand),
        BinaryExpr b => BoundCompileTimeConst(b.Left) && BoundCompileTimeConst(b.Right),
        _ => false,
    };

    /// The flat storage name of <paramref name="instName"/>'s field
    /// <paramref name="field"/>: `inst_field` when the field materialized under
    /// its own name -- which is every non-scalar field (a tuple, dict, array or
    /// nested instance keeps `inst_f`) and most scalar ones. The exception is
    /// the single-field ZCA collapse: a class with exactly one scalar field
    /// stores it under the instance's own name (`self.value` on a Fader IS `f`),
    /// so `f_value` never exists.
    private string BoundFieldFlatName(string instName, string cls, string field)
    {
        string flat = instName + "_" + field;
        // A field that materialized under its own name keeps it: scalar storage,
        // a collection binding, a nested instance (`inst_f` carries the held
        // class -- the collapse is for scalars only, so this wins outright).
        if (variableTypes.ContainsKey(flat) || mutableGlobals.ContainsKey(flat)
            || constantVariables.ContainsKey(flat) || floatConstantVariables.ContainsKey(flat)
            || strConstantVariables.ContainsKey(flat) || constantAddressVariables.ContainsKey(flat)
            || constSequenceBindings.ContainsKey(flat) || dictLiteralBindings.ContainsKey(flat)
            || setLiteralBindings.ContainsKey(flat) || arraySizes.ContainsKey(flat)
            || instanceClasses.ContainsKey(flat)
            || noneValuedNames.Contains(flat) || narrowedOptionals.ContainsKey(flat))
            return flat;
        if (classFieldLayout.TryGetValue(cls, out var layout)
            && layout.Count == 1 && layout[0].Field == field)
            return instName;
        return flat;
    }

    /// The method <paramref name="member"/> resolves to on <paramref name="clsKey"/>
    /// or its ancestors, in the same tables the call dispatch consults -- returned
    /// with the class it was found on, because a `super()` inside that body is
    /// relative to its own class, not the receiver's.
    private (FunctionDef def, string cls)? BoundLookupMethod(string clsKey, string member)
    {
        for (string? cur = clsKey; cur != null;)
        {
            string cand = cur + "_" + member;
            if (instanceMethodDefs.TryGetValue(cand, out var d)
                || inlineFunctions.TryGetValue(cand, out d)
                || methodAstByName.TryGetValue(cand, out d))
                return (d, cur);
            cur = BoundBaseOf(cur);
        }
        return null;
    }

    /// The @property getter or setter body for <paramref name="clsKey"/>.member --
    /// getters sit in `inlineFunctions[Cls_member]` flagged by `propertyGetters`,
    /// setters under the `Cls.member -> Cls_member___setter` redirect; the MRO
    /// walk is the same one <see cref="BoundLookupMethod"/> climbs.
    private (FunctionDef def, string cls)? BoundLookupProperty(string clsKey, string member)
    {
        for (string? cur = clsKey; cur != null;)
        {
            if (propertyGetters.Contains(cur + "." + member)
                && inlineFunctions.TryGetValue(cur + "_" + member, out var gd))
                return (gd, cur);
            if (propertySetters.TryGetValue(cur + "." + member, out var setterKey)
                && inlineFunctions.TryGetValue(setterKey, out var sd))
                return (sd, cur);
            cur = BoundBaseOf(cur);
        }
        return null;
    }

    /// The MRO parent of <paramref name="clsKey"/>, or null at the root.
    private string? BoundBaseOf(string clsKey)
    {
        string bare = clsKey.EndsWith("_", StringComparison.Ordinal) ? clsKey[..^1] : clsKey;
        if (!classBasePrefixes.TryGetValue(bare, out var b) || string.IsNullOrEmpty(b))
            return null;
        return b.EndsWith("_", StringComparison.Ordinal) ? b[..^1] : b;
    }

    /// The fields any method of the class (or an ancestor) writes through `self.`,
    /// excluding the constructor -- those are the ones a shared body cannot fold.
    private HashSet<string> ClassMutableFields(string cls)
    {
        if (classMutableFields.TryGetValue(cls, out var cached)) return cached;
        var mutable = new HashSet<string>();
        for (string? cur = cls; cur != null;)
        {
            if (classFieldLayout.TryGetValue(cur, out var layout))
            {
                var sibs = SiblingMethodsOf(cur);
                foreach (var (field, _, _) in layout)
                {
                    if (mutable.Contains(field)) continue;
                    foreach (var (mname, mdef) in sibs)
                    {
                        if (mname == "__init__") continue;
                        if (MethodMutatesFieldPublic(mdef, field, sibs))
                        {
                            mutable.Add(field);
                            break;
                        }
                    }
                }
            }
            string bare = cur.EndsWith("_", StringComparison.Ordinal) ? cur[..^1] : cur;
            if (!classBasePrefixes.TryGetValue(bare, out var b) || string.IsNullOrEmpty(b))
                break;
            cur = b.EndsWith("_", StringComparison.Ordinal) ? b[..^1] : b;
        }
        classMutableFields[cls] = mutable;
        return mutable;
    }

    /// <summary>
    /// Compile <paramref name="func"/> once as a real subroutine named for
    /// (<paramref name="callee"/>, <paramref name="instName"/>), with `self` aliased
    /// to the instance's global storage. Mirrors SynthesizeZcaIsrWrapper: save the
    /// caller's lowering context, emit into a fresh instruction list, restore.
    /// </summary>
    private string SynthesizeBoundMethod(string callee, string instName, string cls,
                                       FunctionDef func, HashSet<string> stripFields)
    {
        string synthName = "_bound_" + instName + "_" + func.Name;
        var userParams = func.Params.Skip(1).ToList();

        // Metadata first: the call site consults these tables by synth name, and a
        // recursive `self.m()` inside the body re-enters the dispatch expecting the
        // registration to exist.
        functionParams[synthName] = userParams.Select(p => p.Name).ToList();
        functionParamDefaults[synthName] = userParams.Select(p => p.DefaultValue).ToList();
        // The method's own type list normally carries the receiver at index 0, but
        // a def filed without it (an instanceMethodDefs entry ScanFunctions never
        // prefixed, say) already starts at the first user parameter -- skipping
        // another slot there ate `k` off `bump(self, k)` and the body's parameter
        // list ended one short, an IndexOutOfRange on the read_bus() demandant.
        functionParamTypes[synthName] =
            functionParamTypes.TryGetValue(callee, out var srcTypes)
                ? (srcTypes.Count == func.Params.Count
                    ? srcTypes.Skip(1).ToList() : srcTypes.ToList())
                : userParams.Select(p => DataTypeExtensions.StringToDataType(p.Type ?? ""))
                            .ToList();
        functionReturnTypes[synthName] = func.ReturnType;
        functionModulePrefix[synthName] =
            functionModulePrefix.TryGetValue(callee, out var mp) ? mp : "";
        methodInstanceTypes[synthName] =
            methodInstanceTypes.TryGetValue(callee, out var mit) ? mit : cls;
        if (functionReturnMembers.TryGetValue(callee, out var rm))
            functionReturnMembers[synthName] = rm;
        else if (func.ReturnMembers is { } declRm)
        {
            // The original method was inline, so the real-function pass never
            // decided its union return. The synth IS a real subroutine: run the
            // same reach scan now. Unpinnable or two-plus reachable members tags
            // the synth; one member keeps its plain return type; a None-only
            // reach is a void.
            string prevPrefix = currentModulePrefix, prevFn = currentFunction;
            currentModulePrefix = functionModulePrefix[synthName];
            currentFunction = callee;
            HashSet<int>? reach = ReachableReturnMembers(func, declRm);
            currentModulePrefix = prevPrefix;
            currentFunction = prevFn;
            if (reach == null || reach.Count >= 2)
                functionReturnMembers[synthName] = declRm;
            else if (reach.Count == 1 && reach.First() != NoneIndex(declRm))
                functionReturnTypes[synthName] = declRm[reach.First()];
            else
                functionReturnTypes[synthName] = "void";
        }
        // The method's tag table is aligned to its declared list (self at index 0);
        // the synthesized subroutine drops the receiver, so the indexes shift by one.
        if (functionParamTags.TryGetValue(callee, out var mTags) && mTags.Count > 1)
        {
            var shifted = new List<List<string>?>(mTags.Skip(1));
            if (shifted.Any(t => t != null)) functionParamTags[synthName] = shifted;
        }

        var savedInstructions   = currentInstructions;
        var savedFunction       = currentFunction;
        var savedModulePrefix   = currentModulePrefix;
        var savedInlinePrefix   = currentInlinePrefix;
        int savedInlineDepth    = inlineDepth;
        var savedLoopStack      = loopStack;
        var savedInlineStack    = inlineStack;
        // The caller's pending `with`/`finally` bodies are the CALLER's control
        // flow -- a `return` inside this body must not run them here (it would
        // emit the caller's `__exit__` against this body's `self` and strand
        // the stack when the visit throws). Own stack, like loop/inline.
        var savedFinallyStack   = finallyStack;
        // The caller's `try`/`except` frames are caller control flow too: a raise or a
        // fault inside the shared body must deliver to the synth's own catch (or to no
        // catch at all), never jump at a label or an exn variable only the caller owns.
        var savedTryCatchStack  = tryCatchStack;
        var savedHandlerStack   = handlerCodeStack;
        int savedLastLine       = lastLine;
        var savedSourcePath     = currentSourcePath;
        var savedSourceFile     = currentSourceFile;
        bool savedTracksCallee  = inlineTracksCalleeLine;
        int savedCalleeLine     = inlineCalleeStmtLine;
        var savedFunctionGlobals = currentFunctionGlobals;
        // An expansion a shared body cannot carry throws out of VisitBlock with
        // `activeInlineExpansions` still holding its callee -- the rewind below
        // must put the set back too, or the per-site fallback expansion reports
        // that same callee "recursive" and masks the real error.
        var savedActiveInline   = new HashSet<string>(activeInlineExpansions);

        currentInstructions    = new List<Instruction>();
        currentFunction        = synthName;
        currentModulePrefix    = functionModulePrefix[synthName];
        currentInlinePrefix    = "";
        inlineDepth            = 0;
        loopStack              = new List<LoopLabels>();
        inlineStack            = new List<InlineContext>();
        finallyStack           = new List<List<Statement>>();
        tryCatchStack          = new List<string>();
        handlerCodeStack       = new List<string>();
        activeInlineExpansions = new HashSet<string>();
        lastLine               = -1;
        inlineTracksCalleeLine = false;
        inlineCalleeStmtLine   = 0;
        currentFunctionGlobals = new HashSet<string>();
        if (functionSourcePath.TryGetValue(func, out var srcPath))
        {
            currentSourcePath = srcPath;
            currentSourceFile = srcPath.Length > 0 ? SourceFileLabel(srcPath) : "";
        }

        // `self` names the instance's global storage: every `self.f` the body
        // reads or writes resolves to `inst_f`, exactly as the expansion's alias
        // made it do.
        string selfKey = synthName + "." + func.Params[0].Name;
        variableAliases[selfKey] = instName;
        instanceClasses[selfKey] = cls;

        // Mutable fields the body touches: strip the folded values for this
        // instance so `self.f` reads the SRAM a later write reaches, and give
        // that storage an explicit home plus a preamble init when the
        // constructor's value only lived in the constant tables.
        int preambleBefore = boundFieldInitPreamble.Count;
        var addedStorage = new List<string>();
        var strippedConsts = StripMutableInstanceConsts(instName, cls, stripFields, addedStorage);

        var irFunc = new Function { Name = synthName, OriginalName = func.Name };
        for (int i = 0; i < userParams.Count; ++i)
        {
            var p = userParams[i];
            string qn = synthName + "." + p.Name;
            irFunc.Params.Add(qn);
            DataType pdt = functionParamTypes[synthName][i];
            if (p.Type == "bytearray" || p.Type == "bytes" || p.Type == "memoryview"
                || (p.Type != null && p.Type.StartsWith("ptr["))
                || bytearrayParams.Contains(callee + "." + p.Name))
                bytearrayParams.Add(qn);
            variableTypes[qn] = pdt;

            if (functionParamTags.TryGetValue(synthName, out var sTags)
                && i < sTags.Count && sTags[i] is { } tagMembers)
            {
                variableTypes[qn] = UnionPayloadType(tagMembers);
                string tagParam = qn + "$tag";
                (irFunc.TagParams ??= new List<int>()).Add(irFunc.Params.Count);
                irFunc.Params.Add(tagParam);
                variableTypes[tagParam] = DataType.UINT8;
                MarkOptional(qn, new Variable(tagParam, DataType.UINT8), tagMembers);
                unionNameDeclared.Add(qn);
            }
            else if (p.UnionMembers is { } provMembers
                     && functionParamProven.TryGetValue(callee, out var provSet)
                     && provSet.TryGetValue(p.Name, out var provIdx)
                     && provIdx == NoneIndex(provMembers))
            {
                // Every caller provably passes None: the body folds `p is None`
                // without a tag byte, matching the real-function path.
                noneValuedNames.Add(qn);
            }

            if (ClassKeyFromAnnotation(p.Type ?? "") is { } paramCls)
                instanceClasses[qn] = paramCls;
        }

        // The visit is best-effort: a construct a shared body cannot carry that the
        // eligibility walk did not foresee (a sequence operation on a parameter reached
        // through a local alias, say) throws a CompileError the per-site expansion would
        // never raise. Rather than fail the build for an optimisation, the catch rewinds
        // every piece of state the attempt touched and the caller falls back to the
        // expansion -- today's behaviour, for that call.
        try
        {
            bool savedSeqTerminated = _seqTerminated;
            _seqTerminated = false;
            VisitBlock(func.Body);
            _seqTerminated = savedSeqTerminated;

            // A body whose every return hands back a folded constant is a
            // constant thunk: the expansion lands the constant in the caller's
            // expression, a shared body hands back a runtime temp holding the
            // same value. The fold is load-bearing -- `buf[0] = d.at(1)` is
            // pinned to store Constant(20) -- and outlining a `ret K` saves
            // nothing anyway, so this body keeps the per-site expansion.
            var rets = currentInstructions.OfType<Return>().Select(r => r.Value).ToList();
            if (rets.Any(v => v is Constant or FloatConstant)
                && rets.All(v => v is Constant or FloatConstant or NoneVal))
                throw new PyMCU.Common.CompilerError("CompileError",
                    "bound method is a constant thunk", lastLine);

            // Phantom-name audit: a shared body may only read storage it owns
            // (its params, names it writes itself, temps its own expansions
            // mint) or module-reachable storage (globals, module arrays,
            // instance-field flats, compile-time bindings). Anything else -- a
            // temp minted by a caller-side expansion such as
            // `inline15___init___buf__arena_len`, or a `main.`-spelled name that
            // can be homed inside a callee's frame region where a sibling call
            // overlays it -- names a slot the shared body cannot safely reach.
            // Refuse; the sites keep expanding.
            var writtenHere = new HashSet<string>();
            foreach (var ins in currentInstructions)
                foreach (var dv in Verifier.DstVals(ins))
                {
                    if (dv is Variable wv) writtenHere.Add(wv.Name);
                    else if (dv is Temporary wt) writtenHere.Add(wt.Name);
                }
            string? phantom = null;
            void AuditName(string n)
            {
                if (phantom != null) return;
                int dot = n.IndexOf('.');
                if (dot > 0)
                {
                    string head = n[..dot];
                    // Own params/locals, plus temps this body's own inline
                    // expansions minted (`inlineNN.func.*`) -- both home in
                    // the synth's frame. Any other dotted head is a foreign
                    // frame's spelling: `main.x` included -- a name shared by
                    // main and a callee can be homed inside the callee's frame
                    // region, where a sibling call overlays it.
                    if (head == synthName) return;
                    if (head.StartsWith("inline", StringComparison.Ordinal)
                        && head[6..].All(char.IsDigit) && writtenHere.Contains(n)) return;
                    phantom = n; return;
                }
                if (writtenHere.Contains(n)) return;
                // Backend-owned register alias, not a memory slot (Verifier's
                // DeclaredStorage blesses it the same way).
                if (n == "__exn_r22_capture") return;
                if (BoundShareableName(n) || BoundModuleStorageSpelling(n)) return;
                phantom = n;
            }
            foreach (var ins in currentInstructions)
            {
                foreach (var v in Verifier.ReadVals(ins))
                    if (v is Variable rv) AuditName(rv.Name);
                    else if (v is Temporary tv) AuditName(tv.Name);
                    else if (v is ArrayBase ab) AuditName(ab.ArrayName);
                switch (ins)
                {
                    case ArrayLoad al: AuditName(al.ArrayName); break;
                    case ArrayStore ast: AuditName(ast.ArrayName); break;
                }
            }
            if (phantom != null)
                throw new PyMCU.Common.CompilerError("CompileError",
                    $"bound body reads caller-scoped name '{phantom}'", lastLine);
        }
        catch (PyMCU.Common.CompilerError)
        {
            RestoreStrippedConsts(strippedConsts);
            boundFieldInitPreamble.RemoveRange(preambleBefore,
                                               boundFieldInitPreamble.Count - preambleBefore);
            foreach (var k in addedStorage)
            {
                variableTypes.Remove(k);
                mutableGlobals.Remove(k);
            }
            variableAliases.Remove(selfKey);
            instanceClasses.Remove(selfKey);
            currentInstructions    = savedInstructions;
            currentFunction        = savedFunction;
            currentModulePrefix    = savedModulePrefix;
            currentInlinePrefix    = savedInlinePrefix;
            inlineDepth            = savedInlineDepth;
            loopStack              = savedLoopStack;
            inlineStack            = savedInlineStack;
            finallyStack           = savedFinallyStack;
            tryCatchStack          = savedTryCatchStack;
            handlerCodeStack       = savedHandlerStack;
            lastLine               = savedLastLine;
            currentSourcePath      = savedSourcePath;
            currentSourceFile      = savedSourceFile;
            inlineTracksCalleeLine = savedTracksCallee;
            inlineCalleeStmtLine   = savedCalleeLine;
            currentFunctionGlobals = savedFunctionGlobals;
            activeInlineExpansions = savedActiveInline;
            return null;
        }
        if (currentInstructions.Count == 0 || currentInstructions[^1] is not Return)
            Emit(new Return(new NoneVal()));

        irFunc.Body = new List<Instruction>(currentInstructions);

        // The annotation is not the ground truth here: the parser files an
        // unannotated method as "void", and return-type inference gives up on
        // `return self.f` (a member access yields no member list), so a body that
        // hands a value back can still carry the "void" label. The emitted Return
        // instructions know -- a real value means the caller receives one, and
        // the payload width comes from those values, not from the label.
        string rt = func.ReturnType ?? "";
        if (rt is "" or "void" or "None"
            && irFunc.Body.OfType<Return>().Any(r => r.Value is not NoneVal))
        {
            DataType widest = irFunc.Body.OfType<Return>()
                .Where(r => r.Value is not NoneVal)
                .Select(r => GetValType(r.Value))
                .Aggregate(DataType.UINT8, DataTypeExtensions.GetPromotedType);
            rt = DataTypeToSuffixStr(widest);
            functionReturnTypes[synthName] = rt;
        }
        irFunc.ReturnType =
            functionReturnMembers.TryGetValue(synthName, out var synthRm)
                ? UnionPayloadType(synthRm)
                : rt is "" or "void" or "None" ? DataType.VOID
                : DataTypeExtensions.StringToDataType(rt);
        if (irFunc.ReturnType == DataType.UNKNOWN) irFunc.ReturnType = DataType.UINT8;
        // An untagged union spelling (`-> Optional[bool]`) has no DataType case, so a call's
        // result temp would mint UNKNOWN. The payload width the ABI actually returns is
        // irFunc.ReturnType; rewrite the text so the call side types its dst with it.
        if (!functionReturnMembers.ContainsKey(synthName)
            && AnnotationText.UnionMembers(functionReturnTypes[synthName]) != null
            && irFunc.ReturnType != DataType.VOID)
            functionReturnTypes[synthName] = DataTypeToSuffixStr(irFunc.ReturnType);
        if (functionReturnMembers.TryGetValue(synthName, out var frm))
            irFunc.ReturnMembers = frm;
        pendingZcaSynthFunctions.Add(irFunc);

        RestoreStrippedConsts(strippedConsts);
        currentInstructions    = savedInstructions;
        currentFunction        = savedFunction;
        currentModulePrefix    = savedModulePrefix;
        currentInlinePrefix    = savedInlinePrefix;
        inlineDepth            = savedInlineDepth;
        loopStack              = savedLoopStack;
        inlineStack            = savedInlineStack;
        finallyStack           = savedFinallyStack;
        tryCatchStack          = savedTryCatchStack;
        handlerCodeStack       = savedHandlerStack;
        lastLine               = savedLastLine;
        currentSourcePath      = savedSourcePath;
        currentSourceFile      = savedSourceFile;
        inlineTracksCalleeLine = savedTracksCallee;
        inlineCalleeStmtLine   = savedCalleeLine;
        currentFunctionGlobals = savedFunctionGlobals;
        activeInlineExpansions = savedActiveInline;
        return synthName;
    }

    /// <summary>
    /// For the duration of a bound body's visit, drop the constant-table entries of
    /// this instance's mutable fields so `self.f` reads the SRAM the writes reach;
    /// each gets a declared home and a preamble store of the folded value. The saved
    /// entries come back so the caller's view of the world is unchanged by the
    /// synthesis itself -- the call's own invalidation is a separate step.
    /// </summary>
    private List<(int which, string key, int iv, double dv, string sv)>
        StripMutableInstanceConsts(string instName, string cls, HashSet<string> stripFields,
                                   List<string> addedStorage)
    {
        var saved = new List<(int, string, int, double, string)>();
        foreach (var f in stripFields)
        {
            // The field's storage key is `inst_f` -- or `inst` itself when the
            // class has a single field and the member read collapses onto the
            // receiver's own name.
            foreach (var key in new[] { BoundFieldFlatName(instName, cls, f), instName + "." + f })
            {
                if (constantVariables.TryGetValue(key, out var iv))
                {
                    constantVariables.Remove(key);
                    saved.Add((0, key, iv, 0, ""));
                    if (!variableTypes.ContainsKey(key)) addedStorage.Add(key);
                    EnsureBoundFieldStorage(key, cls, f, new Constant(iv));
                }
                if (floatConstantVariables.TryGetValue(key, out var dv))
                {
                    floatConstantVariables.Remove(key);
                    saved.Add((1, key, 0, dv, ""));
                    if (!variableTypes.ContainsKey(key)) addedStorage.Add(key);
                    EnsureBoundFieldStorage(key, cls, f, new FloatConstant(dv));
                }
                if (strConstantVariables.TryGetValue(key, out var sv))
                {
                    strConstantVariables.Remove(key);
                    saved.Add((2, key, 0, 0, sv));
                }
                if (constantAddressVariables.TryGetValue(key, out var av))
                {
                    constantAddressVariables.Remove(key);
                    saved.Add((3, key, av, 0, ""));
                }
                if (noneValuedNames.Remove(key))
                    saved.Add((4, key, 0, 0, ""));
                if (narrowedOptionals.TryGetValue(key, out var nv))
                {
                    narrowedOptionals.Remove(key);
                    saved.Add((5, key, nv, 0, ""));
                }
            }
        }
        return saved;
    }

    /// A field a bound body reads or writes needs real storage: declare the flat
    /// name and, when the constructor's value lived only in the constant tables,
    /// queue a head-of-main store so the bytes exist before any call can run.
    private void EnsureBoundFieldStorage(string flat, string cls, string field, Val init)
    {
        if (variableTypes.ContainsKey(flat)) return;
        string declared = "";
        for (string? cur = cls; cur != null && declared.Length == 0;)
        {
            if (classFieldLayout.TryGetValue(cur, out var layout))
                foreach (var (fld, ty, _) in layout)
                    if (fld == field) declared = ty;
            string bare = cur.EndsWith("_", StringComparison.Ordinal) ? cur[..^1] : cur;
            if (!classBasePrefixes.TryGetValue(bare, out var b) || string.IsNullOrEmpty(b))
                break;
            cur = b.EndsWith("_", StringComparison.Ordinal) ? b[..^1] : b;
        }
        DataType dt = declared == "bool" ? DataType.UINT8
            : declared.Length > 0 ? DataTypeExtensions.StringToDataType(declared)
            : init is FloatConstant ? DataType.FLOAT : DataType.UINT8;
        if (dt == DataType.UNKNOWN) dt = DataType.UINT8;
        variableTypes[flat] = dt;
        mutableGlobals[flat] = dt;
        boundFieldInitPreamble.Add(new Copy(init, new Variable(flat, dt)));
    }

    private void RestoreStrippedConsts(
        List<(int which, string key, int iv, double dv, string sv)> saved)
    {
        foreach (var (which, key, iv, dv, sv) in saved)
        {
            switch (which)
            {
                case 0: constantVariables[key] = iv; break;
                case 1: floatConstantVariables[key] = dv; break;
                case 2: strConstantVariables[key] = sv; break;
                case 3: constantAddressVariables[key] = iv; break;
                case 4: noneValuedNames.Add(key); break;
                case 5: narrowedOptionals[key] = iv; break;
            }
        }
    }
}

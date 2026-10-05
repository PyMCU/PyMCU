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

using PyMCU.Frontend;

namespace PyMCU.IR;

public class InlineContext
{
    public string ExitLabel { get; set; } = "";

    public Temporary? ResultTemp { get; set; }

    // Element type of the list[T] a `return <list var>` in this expansion carried.
    // Kept on the context because listVarElemTypes is rebuilt at every branch join
    // and would drop a registration made mid-expansion; the tail re-registers the
    // temp the caller actually receives.
    public DataType? ResultListElem { get; set; }

    // One level deeper than ResultListElem: for `return <list[list[T]]>` this is T,
    // the element type of each inner list the returned payload points at.
    public DataType? ResultListInnerElem { get; set; }

    // RFC 0009: the tag byte an `-> Optional[X]` callee's result carries. Minted alongside
    // ResultTemp when the callee's declared return members include None; each return writes
    // the member index into it (None is the last member, a payload is index 0).
    public Temporary? ResultTagTemp { get; set; }

    // RFC 0009: set once a return on a REACHED path can leave the tag at the None index --
    // a `return None`, or a return that forwards another live optional's runtime tag. The
    // caller marks its result a live optional only when this is set; an expansion whose
    // every None arm folded away keeps the compile-time answer it always had.
    public bool SawOptionalNone { get; set; }

    // Multi-return tuple: each result slot is a named variable "prefix.result_K"
    public List<string> ResultVars { get; set; } = [];

    // The "bBase.iret_depth_seq_" prefix ResultVars names are built under. A return whose
    // arity the call site cannot count -- `return struct.unpack_from(fmt, ...)` reads
    // the arity from a format that only binds inside the body -- mints its slots here,
    // under the same names a caller's request would have allocated.
    public string TupleSlotPrefix { get; set; } = "";
    public string CalleeName { get; set; } = "";
    public bool ResultAssigned { get; set; } = false;

    // `return <local array>`: the callee's buffer is a fixed static slot, so the value that
    // travels back is its NAME -- the caller's receiving variable aliases this key and the
    // elements never move. Null when no buffer was returned (#464).
    public string? ReturnedBuffer { get; set; } = null;

    // The tuple counterpart of ReturnedBuffer: `return buf, val` records which RESULT SLOT
    // INDICES (into ResultVars) are a fixed array/bytearray name rather than a scalar, keyed
    // by index. A slot present here was never given a scalar Copy -- there is no byte at the
    // name itself to copy, every byte lives under arraySizes -- so the caller's unpack must
    // alias its target to this value instead (mirrors the single-value ReturnedBuffer path
    // one level up, through VisitTupleUnpack's lastTupleResultBuffers).
    public Dictionary<int, string>? ReturnedBufferSlots { get; set; }

    // The mirror image of ReturnedBufferSlots: the result slot indices that DID get a scalar
    // Copy. A return on a second path that delivers a buffer into a slot this set already
    // holds (or a scalar into a slot ReturnedBufferSlots holds) cannot be bound by the
    // caller -- the target would read one storage on a path that meant another -- so
    // VisitReturn refuses the mix instead of compiling it silently.
    public HashSet<int>? ReturnedScalarSlots { get; set; }

    // The constant this expansion's result has been tracked as, if any. Set by the first
    // `return <constant>` that is actually visited; cleared the moment a second REACHABLE
    // return yields a DIFFERENT constant, because then the value is selected at run time.
    public int? ResultConst { get; set; }

    // Set once a return has been visited at this expansion's own branch depth, i.e. one that
    // no run-time condition guards. Control cannot get past it, so every later return in the
    // body is dead code and must not disturb what the result is tracked as. A folded `if`
    // leaves the branch depth alone, which is what makes `if n == 13: return "PB5"` followed
    // by a trailing `return "PD2"` still a compile-time "PB5" when n is 13.
    public bool ResultReturnedUnconditionally { get; set; }

    // For each live `return` visited: the instance it returned (the terminal name of its
    // alias chain), or null for any other value, None included. One instance on every path
    // makes the call's value THAT instance; two or more are chosen at run time.
    public List<string?> ReturnedInstances { get; } = new();

    // The body's deciding return produced None (`return None` or a bare `return`, no value
    // return on any reachable path before it). The call's value is then a compile-time None:
    // the typed ResultTemp is only a formality of the declared `Optional[float]`, and the
    // `Copy(NoneVal)` into it leaves the slot holding whatever a previous temp left behind --
    // `print(s.angle)` on a disabled servo read that residue and wrote 180.0. A value return
    // under a run-time condition keeps the slot instead: the union has no tag.
    public bool ResultIsNone { get; set; }

    // The deciding return produced a live call result (`return f()` where f is a
    // void-declared real call whose value is in the return register). The expansion's
    // result is that same channel: `print(g())` for `def g(): return f()` reads f's
    // R24:R25, which is still live -- nothing ran between the two calls.
    public bool ResultIsLiveCall { get; set; }

    // Where the expansion's first instruction goes, so a member tag minted by a later
    // `return None` can be given its value-path default at the entry.
    public List<Instruction>? EntryInstructions { get; set; }
    public int EntryIndex { get; set; }

    // The inline name-prefix active inside this expansion's body (e.g. "inline1.outer.").
    // Used to resolve a free variable captured from an enclosing INLINE scope.
    public string Prefix { get; set; } = "";

    // Runtime-branch depth at the moment this expansion started. A raise
    // CompileError is "statically unconditional" when no runtime branch was
    // opened INSIDE the expansion — user control flow around the call site
    // (a while/if wrapping the call) does not make the raise conditional.
    public int EntryBranchDepth { get; set; } = 0;

    // Set once this expansion has visited a loop whose trip count is not known at compile
    // time. A `raise` AFTER such a loop is the search-failed arm of a lookup ("probe the
    // table, and if the key is not there, raise") -- reached only for data the compiler
    // cannot see, so it is not the unconditional raise the abort rule is looking for.
    public bool SawDynamicLoop { get; set; } = false;

    // Set once a `return` or a `raise` in this expansion sits under a run-time branch. A raise
    // after it is reached only when that branch was not taken, which the compiler does not
    // decide, so it is not the unconditional raise the abort rule is looking for either
    // (`if c: return x` ... then `raise ValueError(...)` at the end of the body).
    public bool SawConditionalExit { get; set; } = false;

    // The file the CALL is written in, which is not the file being lowered once the body walk
    // has moved the pair to the callee. An author-written `raise CompileError` in a driver is
    // about the caller's argument, so it reports the caller, and reporting a caller means
    // naming the caller's file rather than falling back to the entry file. Empty means the
    // entry file, which is then a statement rather than a fallback. Issue #230.
    public string CallerSourcePath { get; set; } = "";

    // The pendingConstructorTarget the expansion was entered under -- the name the call's
    // result binds to (`r` in `r = f()`). Every `return Cls(...)` in the body IS that
    // result, so each one is offered this name again: without it only the FIRST return's
    // constructor wrote `r_field` and every later one minted an anonymous `__cN` the
    // reads never resolved (decode_bits' `return IRMessage(...)` after its
    // `return NECRepeatIRMessage(...)`).
    public string? CtorTarget { get; set; }

    // finallyStack depth when this expansion started. A `return` inside the body runs the
    // pending exits pushed since then -- a `with`'s `__exit__`, a try's `finally` -- but
    // never the CALLER's pending finallys, which the jump to ExitLabel does not escape.
    public int FinallyDepth { get; set; } = 0;

    // Raises inside the body whose unwinding crosses the CALLER's pending finallys (a `with`
    // around the call). Each one runs the body's own finallys and jumps to its landing, which
    // the expansion emits after it has restored the caller's frame and raises again from
    // there. See EmitRaiseUnwind.
    public List<(string Label, Val Code, bool UnhandledInMain)> RaiseLandings { get; } = new();

    // Per-expansion counter for the scratch keys MakeTemp hands the allocator inside
    // this frame. A `tmp_{n}` minted in the frame records canonicalTemps entry
    // "d{depth}_t{k}"; every expansion at the same depth mints the same keys, so the
    // backend's canonical fold gives sibling expansions ONE shared static slot set
    // instead of each site paying for its own.
    public int TempNext { get; set; } = 0;
}

public class ModuleScope
{
    public Dictionary<string, SymbolInfo> Globals { get; set; } = new();
    public Dictionary<string, DataType> MutableGlobals { get; set; } = new();
    public Dictionary<string, string> FunctionReturnTypes { get; set; } = new();
    public Dictionary<string, List<string>> FunctionParams { get; set; } = new();
    public Dictionary<string, FunctionDef> InlineFunctions { get; set; } = new();
}

public class LoopLabels
{
    public string ContinueLabel { get; set; } = "";
    public string BreakLabel { get; set; } = "";

    // finallyStack depth when the loop was entered: a break/continue runs the finally blocks
    // pushed since then (the try-with-finally blocks between it and the loop), not outer ones.
    public int FinallyDepth { get; set; }
}

public class FunctionEntry
{
    public string? Prefix { get; set; } = "";
    public FunctionDef Func { get; set; } = null!;
    public string SourceFile { get; set; } = "";

    /// The path the module was loaded from, or empty for the entry file. SourceFile is a bare
    /// display name derived from the module name, which collides across directories and cannot
    /// be opened; this is what a diagnostic needs in order to name the file it is really in.
    public string SourcePath { get; set; } = "";
}
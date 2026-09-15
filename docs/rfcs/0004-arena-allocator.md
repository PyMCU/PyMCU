# RFC 0004: arena allocator for runtime-sized buffers

- Status: **PHASE 1 IMPLEMENTED and measured** (module-level and `__init__`-once
  allocation, the once rule, `MemoryError`, observability, AVR only -- section 7). Not
  implemented: zero-copy slice/`memoryview` views over a runtime-sized buffer, passing
  one to a function's `bytearray` parameter, a general call-site-count proof for
  allocation inside a non-inlined function or method, and indexing an arena buffer two
  `@inline` levels deep. All named as follow-up work in section 6, alongside two
  pre-existing gaps this work found but does not fix (constructing a `bytearray` as a
  per-instance field; reading a plain module global as `module.name`).
- Date: 2026-09-15
- Affects: `lib/src/pymcu/arena.py` (new),
  `extensions/pymcu-sdk/csharp/Common/BuiltinExceptionNames.cs`,
  `src/compiler/IR/IRGenerator/{State,Assign,Expr,Call,Statements}.cs`,
  `src/driver/commands/build.py`, `docs/language/limitations.md`,
  `docs/language/roadmap.md`, `LANGUAGE_ROADMAP.md`, `CHANGELOG.md`. In `pymcu-avr`:
  `tests/integration/fixtures/arena-{module-level,in-init,overflow,high-water}/`,
  `tests/integration/Tests/AVR/Arena*Tests.cs`.

## 1. Problem

`bytearray(n)` where `n` is not a compile-time constant is refused today
(`Assign.cs`: "bytearray: could not determine buffer size from initializer") because a
runtime-sized buffer has no home: PyMCU has no heap and no GC beyond the AVR-only
`list[T]` bump allocator (`docs/language/limitations.md`). A driver that reads a
sensor-reported payload length, or a protocol frame whose size comes from a header byte,
cannot express "give me n bytes" at all -- only a fixed `uint8[256]` worst case, wasting
SRAM on a part that has 2048 bytes of it.

## 2. Model

**One arena, reserved once, never freed.** A single static byte array
(`lib/src/pymcu/arena.py:_arena`) plus a bump pointer (`_arena_pos`). `alloc(n)` checks
`_arena_pos + n <= ARENA_SIZE`, advances the pointer, and returns the old value as a
`ptr`; on overflow it raises `MemoryError`. There is no `free`. This is deliberately
`list[T]`'s allocator (`docs/language/limitations.md` "bounded bump-allocator with GC")
with the GC removed: `list[T]` frees because a list can go out of scope inside a loop and
does; a `bytearray` from `alloc()` is a permanent buffer for the life of the program, so
freeing has nothing to reclaim from and the mark/sweep machinery is pure overhead. Zero
freeing is the simplification `list[T]` could not make.

**The once rule.** Because nothing is freed, an allocation inside a loop or a function
called more than once grows the arena forever until it overflows -- silently, at a point
in the run that has nothing to do with the line that allocated. The compiler refuses this
at build time instead: `bytearray(n)` with a runtime `n` is accepted only where the
compiler can prove the statement executes at most once.

Phase 1 proves this with one check, reusing two facts the IR generator already tracks
rather than adding call-graph analysis:

- `currentFunction == "main"` (or ends with `"___module_init"` for a library module) is
  exactly the marker `Assign.cs` already uses to mean "this statement is module-level
  code being replayed by the synthesized entry point" (see the `replayingModuleLevel`
  comment at `Assign.cs:4347-4360`).
- A ZCA `@inline` class's `__init__` expands **textually at its construction call site**
  (`docs/language/limitations.md` "ZCA `@inline` classes (zero SRAM)"; the caller's frame
  holds the fields, there is no subroutine). Nothing in the generator reassigns
  `currentFunction` during `@inline` expansion (only `currentInlinePrefix` changes), so
  `self.buf = bytearray(n)` inside an `@inline __init__` sees the *same*
  `currentFunction` the construction statement itself sees. A construction at module
  level makes `currentFunction == "main"` true for the allocation too, with no separate
  bookkeeping.

A new `loopDepth` counter (incremented around `VisitWhile` / `VisitFor`, decremented in a
`finally`) is nonzero inside any loop, compile-time-unrolled or not -- an unrolled
`for x in [1, 2, 3]:` still calls `alloc()` three times, which is still "not once".

So the rule is one expression:

```
onceOk = loopDepth == 0
      && (currentFunction == "main" || currentFunction.EndsWith("___module_init"))
```

This is sound (never accepts something unsafe) and intentionally conservative: it refuses
allocation inside *any* non-inlined function or method, whatever its call count, rather
than attempting the general "every call site is itself such a statement" proof the design
note asks for. In exchange it needs no new analysis pass, and it is exactly right for the
stated common case, "typically `__init__` of an object constructed at module level" --
because that `__init__`, being `@inline`, is not a separate function in the sense the
check cares about; it is the module-level statement, continued.

It also naturally covers the other three refusal cases the design calls out, with no
extra code:

- **Inside an ISR handler.** A handler passed to `compile_isr()` is always a real
  (non-inlined) function with a fixed address for the vector table, so its body has
  `currentFunction == "<handler name>"`, never `"main"`.
- **Inside a function called from a loop, or from two or more sites.** Any such function
  is, definitionally, a real function; its body's `currentFunction` is its own name.
- **Inside `async def`.** Already refused unconditionally, earlier in the pipeline
  (`Statements.cs:544`, "the coroutine-to-state-machine lowering is not implemented
  yet") -- `async def` bodies never reach IR generation at all.

**The diagnostic** distinguishes the two cases users can actually hit (the last two above
never reach a user, since `async def` and a bare ISR handler are refused, or simply
un-inlined, before this check runs):

```
`bytearray(n)` here would allocate a new region every time this loop runs, and PyMCU's
arena never frees. Either give it a compile-time size (`bytearray(64)`), or move the
allocation to a module-level statement that runs once.
```

```
`bytearray(n)` inside 'read_frame' cannot be proven to run at most once (PyMCU's arena
never frees). Either give it a compile-time size (`bytearray(64)`), or move the
construction to a module-level statement (or an `@inline __init__` reached only through
inlining from one).
```

## 3. The allocator is Python

`lib/src/pymcu/arena.py` (abridged; the shipped file has the full comments):

```python
from pymcu.types import uint8, uint16, inline

ARENA_SIZE: uint16 = 0

_arena: bytearray = bytearray(ARENA_SIZE)
_arena_pos: uint16 = 0
arena_high_water: uint16 = 0

@inline
def alloc(n: uint16) -> uint16:
    global _arena_pos, arena_high_water
    if _arena_pos + n > ARENA_SIZE:
        raise MemoryError
    base: uint16 = _arena_pos
    _arena_pos = _arena_pos + n
    arena_high_water = _arena_pos
    return base

@inline
def read8(off: uint16) -> uint8: return _arena[off]

@inline
def write8(off: uint16, v: uint8) -> None: _arena[off] = v
```

Anyone can read it; there is no hidden compiler intrinsic doing the bump. `alloc()`
returns an **offset**, not a raw address -- not the design this section originally
described. `ptr(_arena) + _arena_pos`, the constant-base-plus-runtime-offset arithmetic
that shipped for `ptr(BASE + x)` (2026-06-13), turned out not to apply here: that
feature's constant base is a hardware register (a `MemoryAddress` with a compile-time-
known numeric address); `_arena` is an ordinary SRAM array, addressed by an assembler
label the front end does not resolve to a number, and `ptr(_arena)` silently read
`_arena`'s first byte instead of computing its address (measured directly: the IR showed
a `Copy` of a UINT8-typed `Variable("main.buf", ...)` into the pointer temp, not an
address). Returning an offset instead needs no such primitive: `_arena[off]` is ordinary
variable-indexed fixed-array access, which already exists, so `read8()` / `write8()` are
the entire new surface. The compiler rewrites `buf[i]` / `buf[i] = v` on an arena-backed
name into a call to one of these two ("Section 4"), inlined at the call site -- `buf`
itself is compiled as a completely ordinary `uint16` variable that happens to hold an
offset, so nothing about reading `buf`'s own value needed any new code either.

**`ARENA_SIZE` reaches the stdlib as a whole-file substitution, the way `board.py`
does, not through a separate imported config module.** The first design tried a
separate `pymcu.arena_config` module holding just the constant, imported by `arena.py`
(mirroring `__TIMEBASE__`'s intent -- see below). That constant folded fine in an
ordinary expression (`alloc()`'s own `_arena_pos + n > ARENA_SIZE` check), but not as
`bytearray(ARENA_SIZE)`'s size argument in a DIFFERENT file: `TryEvalElemConst` reached
the same `EvaluateConstantExpr` cross-module lookup a bare comparison does, and still
failed to resolve the imported name (measured directly, with a generated override
present and correctly picked up by the include-path search -- confirmed by the
comparison folding to the right literal -- yet the array-size determination distinct code
path in `Assign.cs` still could not see it). Rather than debug why one call site to a
shared evaluator disagrees with another, phase 1 sidesteps the question: `ARENA_SIZE`
lives in `arena.py` itself, a same-file literal (`bytearray(WINDOW)`'s exact case, which
demonstrably works), and `pymcu build` generates a **full replacement** of `arena.py`
into `dist/_generated/pymcu/arena.py` with only that one line's number changed --
exactly the mechanism already used for `board.py` (`build.py`, the `board_shim` block):
a generated file placed first on the include search path shadows the shipped one.

While researching how a board constant is *supposed* to reach the stdlib, `__TIMEBASE__`
(`lib/src/pymcu/chips/__init__.py:92`, "Bound by the compiler like `__FREQ__`") turned
out to have no consumer anywhere in the compiler (`context.DeviceConfig.Timebase` is
written in `InitializationPhase.cs` and never read again) -- its binding is documented
as if it existed and does not. This is unrelated to the arena and not something phase 1
relied on; noted in section 6 so it is not mistaken for something quietly fixed here.

**Sizing.** `build.py` scans `.py` sources for `bytearray(<non-constant>)` the same way
it already scans for `= f"..."` (`_detect_fstring_value_usage`, "over-inclusive on
purpose... unused is dropped by DCE"). For each match it tries to fold the argument as a
plain integer literal or a `+`/`*` expression of literals; if every runtime-sized
`bytearray(...)` call in the program folds this way, `ARENA_SIZE` is their exact sum --
tight, zero waste. If any does not fold (a size that is genuinely a runtime value, e.g.
`bytearray(header[0])`), or the user set `arena_size` in `[tool.pymcu]`, `ARENA_SIZE` is
the explicit override if given, else a board default (256 bytes on `atmega328p`,
documented in `arena.py` and overridable). This heuristic only affects capacity,
never correctness: the once rule is enforced independently and unconditionally by the
compiler, so a program the heuristic undersizes fails safe, with `MemoryError`, the first
time it is actually exercised -- not with silent corruption. **Zero-cost gate:** when no
source matches, `ARENA_SIZE` stays the shipped 0 default, `bytearray(0)` is zero bytes of
SRAM, and nothing calls `alloc()`, so nothing pulls `arena.py`'s code into the link at
all -- a program that never allocates from the arena is unaffected, byte for byte.

`MemoryError` is a new builtin exception code
(`extensions/pymcu-sdk/csharp/Common/BuiltinExceptionNames.cs`), alongside `ValueError`
etc.: `except MemoryError:` works under the existing T-flag model with no changes to
`ControlFlow.cs`'s dispatch, because that dispatch is generic over exception codes
already.

## 4. Surface: what a runtime-sized `bytearray(n)` compiles to

`x`, once allocated, is registered under its own qualified name (`arenaBufferNames`,
`arenaBufferLenVar` -- `State.cs`) -- **not** in `bytearrayParams` or `arraySizes`, the
two dictionaries the static-array and bytearray-parameter paths use. It is compiled as a
plain `uint16` variable holding the byte offset `alloc()` returned:

- `x = bytearray(n)` (once-provable) lowers to a call into `_pymcu_arena.alloc(n)`
  (resolved through `importedAliases`, the same lookup `pymcu.strfmt` uses), copied into
  `x` exactly as any other `x: uint16 = <expr>` declaration would be -- `x` needs no
  special-cased storage or resolution logic of its own, only the membership fact that it
  IS an arena buffer, recorded once at this point.
- `x[i]` / `x[i] = v`: a new, early check in `VisitIndex` (`Expr.cs`) and
  `EmitIndexAssign` (`Assign.cs`) -- "is this target name a registered arena buffer?" --
  rewrites the subscript into a call to `_pymcu_arena.read8(x + i)` /
  `_pymcu_arena.write8(x + i, v)`, inlined at the call site. `x + i` is ordinary integer
  arithmetic over `x`'s own (already-correct) value, so no address computation is needed
  here either -- only inside `read8`/`write8`'s own bodies, where `_arena[off]` is a
  completely ordinary array access this compiler already had.
- `len(x)`: a new fallback in `EmitLenBuiltin`, parallel to the existing
  `TryGetRuntimeStr` case for f-string-value buffers -- the allocation site also stores
  `n` into a second, paired variable (`x__arena_len`) and `len(x)` on a registered arena
  buffer reads that variable instead of failing.
- Constant-sized `bytearray(N)` is untouched: the `TryEvalElemConst` branch that already
  handles it runs first, so existing programs stay on the static-array path, byte for
  byte -- measured directly (section 7).

## 5. Targets

AVR only, like `list[T]`. `GcAnalysisPhase`-style, the once-rule check raises naming the
architecture on any other target (`arch != "avr" && arch != ""`), pointing at
`bytearray(N)` with a compile-time size as the portable alternative.

## 6. What phase 1 does not do

- **No zero-copy slice / `memoryview` view over a runtime-sized buffer**, and **no
  passing an arena buffer to a function's `bytearray` parameter.** `buf[k:]` and
  `memoryview(buf)[k:]` as "base + offset, no copy" exist today only for compile-time-
  sized bytearrays and fixed arrays; an arena buffer has no slicing support to extend.
  Passing `x` to `def f(buf: bytearray)` needs a genuine runtime pointer at the call
  boundary the same way a slice view would (an "address of `_arena`, plus a runtime
  offset" value) -- section 3 already found that `_arena`'s address is not something
  this target computes for an ordinary SRAM array the way it does for a register. Both
  are real new IR surface, not a small addition, and left for a follow-up RFC increment;
  the diagnostics and tests below do not claim either works. What DOES work today is
  forwarding through `@inline` expansion, since that is pure textual substitution with no
  pointer involved -- the parameter becomes an alias for the same offset variable.
- **Indexing an arena buffer two levels of `@inline` deep does not resolve.**
  `buf[i]` / `buf[i] = v` rewrites to a call into `read8()`/`write8()`, themselves
  `@inline`, so indexing from inside an already-inlined context nests a SECOND `@inline`
  expansion inside the first. One level deep (a bare module-level statement calling
  `read8`/`write8`, or an `@inline __init__`'s local indexed directly) works, measured
  directly. Two levels deep (indexing a LOCAL created inside an `@inline __init__`, so
  module -> `__init__` -> `read8`/`write8`) does not: `_arena`'s own resolution inside
  `read8`/`write8`'s inlined body falls through to a different, wrong interpretation
  ("runtime bit index is only supported on a chip register"). Reproduced with a minimal
  non-arena, non-cross-module case that did NOT trigger it (two inline levels, a runtime
  value, a module-local array), so the trigger is narrower than "nested inlining" alone
  and was not isolated further under this RFC's time budget. The `arena-in-init` AVR
  fixture (`pymcu-avr` repo) works around it by not indexing the buffer inside
  `__init__` -- it allocates and stores the offset in a plain field, which is enough to
  exercise the once rule this RFC is actually about, and is a real, if narrower,
  demonstration of "allocate inside `__init__`, use the buffer afterward through the
  stored offset."
- **No general call-site-count proof.** A `bytearray(n)` inside a plain (non-`@inline`,
  non-`__init__`) function is refused unconditionally today, even when the author can see
  by hand that the function has exactly one call site. Proving that in general needs a
  call-graph pass this phase does not add (see section 2).
- **`__TIMEBASE__`'s binding gap**, found while researching how a board constant is
  supposed to reach the stdlib, is unrelated to the arena and out of scope here; noted so
  it is not mistaken for something this RFC relied on and quietly fixed.
- **Constructing a `bytearray` as a per-instance field does not compile at all yet**,
  found while testing the `__init__` case this RFC's once rule is built around --
  `self.buf: bytearray = bytearray(4)` and the unannotated `self.buf = bytearray(4)` both
  fail identically, with a compile-time size, on a build with none of this RFC's changes:
  `bytearray() is a Python builtin that PyMCU does not provide`. A member-target
  assignment's RHS is evaluated as an ordinary expression with no special case for
  `bytearray(...)`, unlike a plain local or module-level declaration. This is a
  pre-existing gap in ZCA field construction, unrelated to arenas and larger than this
  RFC (it blocks every `self.buf: bytearray = ...` shape, constant-sized or not) --
  phase 1 does not fix it. What phase 1's once rule does verify: a `bytearray(n)`
  allocation as a **local** inside an `@inline __init__` that constructs at module level
  is accepted (`currentFunction` stays `"main"` through the inlining, exactly as
  section 2 predicts), so the mechanism is right; only the field-assignment spelling
  needs the separate fix to reach it. Filed as a finding rather than fixed here because
  fixing it is a ZCA field-layout change with its own blast radius, not an arena change.
- **A plain module-level global cannot be read as `module.name` from outside the
  module**, found writing the `arena-high-water` AVR fixture: `_pymcu_arena.
  arena_high_water` (a public, non-underscore data global) fails the same
  `Unknown module member` diagnostic a private one does. Module member access resolves
  functions; it does not resolve data. `arena_high_water` stays the named global the
  design asks for (readable from a `.lst`/`.map`/debugger), and `arena.py` gained one
  more small `@inline` getter, `high_water()`, so an emulator test can still read it the
  way it reads anything else in the module. Another pre-existing gap, not fixed here.

## 7. Measured

- **1846/1849 PyMCU unit tests pass** (`just test-unit`), including 12 new
  `ArenaAllocatorTests` covering the once rule (module level, `@inline __init__`,
  refused in a loop / a plain function / two call sites / an ISR handler, an unrolled
  compile-time loop still refused), `MemoryError` catchability, and that a constant-sized
  `bytearray(N)` is unaffected. The 3 skips pre-date this change.
- **792/794 PyMCU driver tests pass** (`pytest tests/driver`), including 18 new tests for
  the sizing heuristic (`_fold_int_expr`, `_detect_and_size_arena_usage`) and an
  end-to-end `pymcu build` check that the import, the generated shim and the printed
  build line all appear together. The 2 failures are pre-existing and reproduce
  identically on an unmodified checkout (an `ipecmd` entry-point packaging gap and one
  frontend-parity test unrelated to bytearrays, both confirmed by running the same tests
  against `main`).
- **4/4 pymcu-avr integration fixtures pass on avr8sharp**, built through the real
  `pymcu build` driver end to end: `arena-module-level` (write/read/len), `arena-in-init`
  (once rule through an `@inline __init__`), `arena-overflow` (`MemoryError` raised and
  caught), `arena-high-water` (the named global after two allocations).
- **ROM/IR differential across the pymcu-avr fixture corpus:** 351 fixtures scanned, 286
  invocable directly with `pymcuc --emit-ir` (the rest need `pymcu build`'s own preamble
  injection -- stdout, `strfmt`, `**kwargs`, etc. -- which a raw `pymcuc` call does not
  provide, arena's own new fixtures included). Of those, 270 produced **byte-for-byte
  identical IR** between the pre-arena compiler (built from the branch's merge-base with
  `main`) and the arena compiler; **0 changed**; **0 new failures**. The 15 fixtures that
  failed to compile directly on both compilers (the arena fixtures themselves, plus
  pre-existing ones needing driver preamble injection) failed identically on both --
  confirmed neutral, not a regression, by definition of "both". Since AVR codegen is a
  pure function of IR, byte-identical IR guarantees byte-identical ROM without needing to
  invoke the AVR toolchain per fixture.

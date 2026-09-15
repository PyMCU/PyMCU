# RFC 0004: arena allocator for runtime-sized buffers

- Status: **PHASE 1 IMPLEMENTED** (module-level and `__init__`-once allocation, the once
  rule, `MemoryError`, observability, AVR only). Not implemented: zero-copy slice/
  `memoryview` views over a runtime-sized buffer, and a general call-site-count proof for
  allocation inside a non-inlined function or method. Both are named as follow-up work in
  section 6.
- Date: 2026-09-15
- Affects: `lib/src/pymcu/arena.py` (new), `lib/src/pymcu/exceptions.py`,
  `extensions/pymcu-sdk/csharp/Common/BuiltinExceptionNames.cs`,
  `src/compiler/IR/IRGenerator/{State,Assign,Expr,Call,ControlFlow,Iteration}.cs`,
  `src/driver/commands/build.py`, `docs/language/limitations.md`.

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

`lib/src/pymcu/arena.py`:

```python
from pymcu.arena_config import ARENA_SIZE
from pymcu.exceptions import CompileError
from pymcu.types import uint8, uint16, ptr, inline

_arena: bytearray = bytearray(ARENA_SIZE)
_arena_pos: uint16 = 0

@inline
def alloc(n: uint16) -> ptr:
    global _arena_pos
    if _arena_pos + n > ARENA_SIZE:
        raise MemoryError
    p: ptr = ptr(_arena) + _arena_pos
    _arena_pos = _arena_pos + n
    return p
```

Anyone can read it; there is no hidden compiler intrinsic doing the bump. The only new
runtime primitive `alloc()` needs is `ptr(_arena) + _arena_pos`, and that already exists:
`_arena` is an ordinary fixed-size (`ARENA_SIZE` is a plain compile-time `int`) SRAM
array, so `ptr(_arena)` is a constant base, and constant-base-plus-runtime-offset
`ptr` arithmetic shipped for the `ptr(BASE + x)` feature (2026-06-13). `alloc()` needed
zero new backend code as a result -- it compiles through the exact path a user's own
`ptr(BASE + x)` code would.

**`ARENA_SIZE` reaches the stdlib the way `board_shim.py` reaches it, not the way
`__TIMEBASE__` was meant to.** `__TIMEBASE__` (`lib/src/pymcu/chips/__init__.py:92`,
"Bound by the compiler like `__FREQ__`") turned out, on inspection for this RFC, to have
no consumer anywhere in the compiler (`context.DeviceConfig.Timebase` is written in
`InitializationPhase.cs` and never read again) -- its binding is not implemented, only
documented as if it were. Rather than build `ARENA_SIZE` on a mechanism that does not
currently exist, phase 1 uses the pattern that demonstrably does: `pymcu build` already
stages a generated `board.py` shim into `dist/_generated`, prepended to the include
search path so it is found before the real stdlib module of the same name (`build.py`,
the `board_shim` block). `ARENA_SIZE` gets the same treatment: `lib/src/pymcu/
arena_config.py` ships `ARENA_SIZE: uint16 = 0` as the "arena unused" default, and the
driver, when it detects arena usage, generates `dist/_generated/pymcu/arena_config.py`
with the real literal (`ARENA_SIZE: uint16 = 512`) and inserts that directory first in
`extra_includes`, exactly like the board shim. This needed no change to the C# compiler
at all -- `ARENA_SIZE` is compiled as the perfectly ordinary module-level integer
constant it looks like, because by the time the compiler sees it, it is one.

**Sizing.** `build.py` scans `.py` sources for `bytearray(<non-constant>)` the same way
it already scans for `= f"..."` (`_detect_fstring_value_usage`, "over-inclusive on
purpose... unused is dropped by DCE"). For each match it tries to fold the argument as a
plain integer literal or a `+`/`*` expression of literals; if every runtime-sized
`bytearray(...)` call in the program folds this way, `ARENA_SIZE` is their exact sum --
tight, zero waste. If any does not fold (a size that is genuinely a runtime value, e.g.
`bytearray(header[0])`), or the user set `arena_size` in `[tool.pymcu]`, `ARENA_SIZE` is
the explicit override if given, else a board default (256 bytes on `atmega328p`,
documented in `arena_config.py` and overridable). This heuristic only affects capacity,
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

A runtime-sized `bytearray(n)` is registered exactly like a `bytearray` **parameter** is
today (`bytearrayParams`, not `arraySizes`): a pointer variable, indexed through the
existing `BytearrayLoad` / `BytearrayStore` IR instructions that already exist for that
case. This is why indexing, `len()` and passing the buffer to a function declared to
take `bytearray` needed no new IR instruction and no AVR backend change:

- `x = bytearray(n)` (once-provable) lowers to a call into `_pymcu_arena.alloc(n)`
  (resolved through `importedAliases`, the same lookup `pymcu.strfmt` uses -- see
  `Assign.cs:2953-2960`), the result copied into a new pointer variable `x`, registered
  in `bytearrayParams`.
- `x[i]` / `x[i] = v` reads or writes through that pointer via `BytearrayLoad` /
  `BytearrayStore`, unchanged code.
- `len(x)`: a new fallback in `EmitLenBuiltin`, parallel to the existing
  `TryGetRuntimeStr` case for f-string-value buffers -- the allocation site also stores
  `n` into a second, paired variable (`x__arena_len`) and `len(x)` on a
  `bytearrayParams`-registered name with no `arraySizes` entry reads that variable
  instead of failing.
- Passing `x` to `def f(buf: bytearray)`: unchanged -- call-argument lowering already
  passes a `bytearrayParams`-registered name by its pointer value (it is already an
  address, unlike an `arraySizes` fixed array, which needs its address taken), because
  that is what forwarding an existing `bytearray` parameter to another function already
  does.
- Constant-sized `bytearray(N)` is untouched: the `TryEvalElemConst` branch that already
  handles it runs first, so existing programs stay on the static-array path, byte for
  byte.

## 5. Targets

AVR only, like `list[T]`. `GcAnalysisPhase`-style, the once-rule check raises naming the
architecture on any other target (`arch != "avr" && arch != ""`), pointing at
`bytearray(N)` with a compile-time size as the portable alternative.

## 6. What phase 1 does not do

- **No zero-copy slice / `memoryview` view over a runtime-sized buffer.** `buf[k:]` and
  `memoryview(buf)[k:]` as "base + offset, no copy" exist today only for compile-time-
  sized bytearrays and fixed arrays; a `bytearrayParams`-registered runtime pointer has
  no slicing support to extend, and building it (a pointer-to-pointer offset view, still
  indexable/passable) is real new IR surface, not a small addition. Left for a follow-up
  RFC increment; the diagnostics and tests below do not claim it works.
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

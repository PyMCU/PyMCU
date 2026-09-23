# RFC 0011: Generators -- `yield` as a state machine, one static frame per instantiation site

- Status: **PROPOSED (Phase 1 implemented: methods)**. The module-level shape
  below already compiles on main; this RFC records what the 2026-05-02
  measurement campaign found on the whole idiom, decides the model the method
  extension builds on, and says explicitly what stays refused and why.
  Measurements: `report-yield.md` in the campaign scratchpad (109 probes,
  CPython-oracle, both front ends, `PYMCU_NO_OPT=1`); the byte numbers are in
  section 4.
- Date: 2026-05-02
- Demanding program: `adafruit_irremote.NonblockingGenericDecode.read`
  (adafruit_irremote.py:226, upstream 5.0.8, md5
  `c93bd33cef7387e59e384c4964ee924d`) -- a `while`-driven generator *method*
  that yields `result : None | IRMessage | UnparseableIRMessage |
  NECRepeatIRMessage`. `test02_marker_per_poll` and
  `test03_self_loopback_nec` call `read_pulses`, which never drives `read()`;
  the library still fails to build until the method generator compiles.
- Affects: `src/compiler/Frontend/AsyncTransform.cs` (the whole transform --
  `genFns` selection, `RejectGeneratorMethods`, `ContainsYield`, `EmitStmt`,
  `RewriteGenFors`), the `VisitYield` backstop in
  `src/compiler/IR/IRGenerator/Expr.cs`, and the for-in iterable diagnostic
  that currently swallows unclassified `g()` calls.
- Builds on: the coroutine lowering (`async def` -> `poll()` state machines,
  `async-await-vision` note -- a generator is the same machine with no event
  loop), the ZCA instance model (RFC 0006: instances are named static slots,
  never values), and RFC 0009's tag-byte thinking for the `_value` question
  (its phase-2 field tags and instance-union refusals bound what `_value` may
  ever hold).

## 0. Decisions this RFC encodes

1. **A generator is a class; an instantiation is a static slot.** `def g()`
   with a reachable `yield` lowers to a ZCA class `g` with `__init__` (params
   and field locals) and `poll() -> uint8`. There is no generator object, no
   heap, no iterator protocol -- `for x in g(...)` is desugared at the call
   site into an explicit poll loop. One `g(...)` call *site* (or one name
   bound to such a call) owns exactly one static frame.
2. **`yield` suspends; `poll() == 2` means a value is waiting in `_value`.**
   The protocol is `0` done, `1` internal transition, `2` yielded. `return`
   sets the terminal state (`0x7FFF`) and, with a value, stores the
   `StopIteration` payload in `_value` first.
3. **`_value` is a scalar or it is opaque.** When every `yield`/`return` in a
   generator produces a value of one scalar type, `_value` is that type
   (narrowest fit, the existing inference). When any of them is not a scalar
   -- `yield`, `yield None`, `yield 1.5`, `yield s` for a `str` param,
   `yield <instance>`, the demandant's `yield result` union -- `_value` has
   no representation: the yield still suspends correctly (the machine's
   control flow is unaffected), no field storage is emitted, and *reading*
   the value (`x = __gen._value`, which every `for` desugar emits) is a
   CompileError naming the generator and the unrepresentable type. This is
   what turns today's silent `0`/`256`/garbage reads (measurement RED-1..4)
   into an honest refusal *without* refusing the suspension itself -- the
   demandant's `read()` compiles because its yields suspend; a program that
   tried to consume the union gets a diagnostic instead of a number.
4. **A generator method keeps its receiver as its first parameter.** `def
   read(self)` inside `class C` lowers to a class `read` (namespaced under
   `C` so two classes may both name a method `read`) whose `__init__` takes
   `self` as parameter zero -- the same mechanism that already lets a
   generator param hold an instance (measured: mutating calls through a
   param-held instance, including nested `t.inner.bump()`, write through to
   the original slot). `for x in obj.read()` desugars to `__gen =
   C_read(obj); <poll loop>`. The receiver's class is resolved from the
   *binding* (`obj = C(...)`) the same way bound generator names are tracked
   today; a receiver the tracker cannot name is refused by name (section 8).
   `@staticmethod` generators take no receiver; `@classmethod` takes `cls`.
   `__iter__`, `__next__`, `__enter__`/`__exit__` and other protocol slots
   stay refused: they would be *called by machinery*, and PyMCU has no
   machinery that calls them.
5. **`global` inside a generator names the module global.** A name declared
   `global` is not a local and must not be promoted to a machine field
   (measured RED-5: `T = 9` inside a generator wrote `self.T` and left the
   global at 0).
6. **`yield` inside `async def` is an async generator and is refused.**
   Today it lowers to a real suspension inside the coroutine's `poll()`;
   `poll() == 2` then ends the coroutine early under the scheduler
   (measured RED-6: `asyncio.run(g())` prints `END` and produces nothing).
   A coroutine that yields is not "a generator inside async" -- it is a
   third idiom (push + pull + scheduling) this compiler has no model for.
7. **`return` anywhere inside a generator body terminates it.** A `return`
   inside a statement the splitter keeps whole (`if k: return`) means
   StopIteration, not a None-return from `poll`; it is rewritten to
   `{ _state = Terminal; return 0 }` the way `break`/`continue` inside kept
   statements already rewrite to state transitions (measured D-2: today the
   kept statement reaches `poll` and the RFC 0009 check refuses it, naming
   `poll` -- a diagnostic that is wrong about both the name and the
   semantics).
8. **The compile-time generator-expression path is untouched.** `all(x for x
   in seq)`/`sum`/etc. over compile-time-known iterables keep unrolling in
   `GenExp.cs` exactly as today; nothing in this RFC creates a generator
   object they could ride on, and none is needed.

## 1. The measured problem

109 one-aspect probes (the corpus is enumerated in `report-yield.md`), each
run under CPython with the oracle shims, compiled, and executed on the
avr8sharp emulator -- under the C# front end, the Python-AST front end
(`PYMCU_PY_PARSER=1`), and `PYMCU_NO_OPT=1`. **All three configurations
classify every probe identically**: 37 GREEN, 65 REFUSED, 7 RED, 0 CRASH.

The module-level state machine already exists and is mostly right: `for`
consumption, `for...else`, `break` from an infinite generator, partial
abandonment, two live instances, bound-name resumption, `while`/`range`
loops inside the body, `yield from` same-module expansion, runtime trip
counts -- all match CPython byte-for-byte on the UART.

The silent wrong code the measurement found (each a minimal reproducer in
the report):

| finding | shape | today |
|---|---|---|
| RED-1 | `yield` (bare) | stores `0` in `_value`; CPython yields `None` |
| RED-2 | `yield None` | stores `0` |
| RED-3 | `yield 1.5` | prints 258 (opt) / 333 (no-opt) -- a garbage read, which is how the two runs proved it |
| RED-4 | `def g(s): yield s` with `s="hi"` | prints `256`, the string's flash id (the literal form `yield "hi"` is caught by an unrelated field-type check that names `_value`/`poll`) |
| RED-5 | `global T` inside a generator | `T = 9` writes `self.T`; the module global stays 0 |
| RED-6 | `async def g(): yield 1` driven by `asyncio.run(g())` | compiles; the coroutine ends at the first `poll()==2` having produced nothing; CPython raises `TypeError` |

And the refused-but-dishonest diagnostics (verbatim quotes in the report):

- `yield` inside `try`/`with`/`match`/`except` is invisible to
  `ContainsYield`, so the function is never classified and the consumer's
  `for` reports the *iterable-kind* list -- naming neither `yield` nor the
  construct hiding it (D-1, 7 probes).
- `if k: return` inside a generator reaches `poll` as a bare `return` and
  RFC 0009 decision-5 fires naming `poll` and calling it a None-return
  (D-2). `-> Optional[int]` generators die on the same check.
- `for x in <seq>` inside a generator refuses with
  `` async def 'g': `await` inside a for-loop ... `` -- the function is not
  async and there is no `await` (D-3, 7 probes incl. pipelines, recursion
  bodies, grid rows, bytearray, const seq, fixed array, string).
- `await` inside a generator is refused as `` async def 'g' `` -- `g` is a
  `def` (D-4).
- `x = yield 5` gets the generic VisitYield text instead of the
  `send()`-naming one that already exists (D-5).
- `yield "hi"` refuses via a field-type error naming `_value` and `poll`;
  `yield T()` fails later with `'x' is not a member of a numeric value`
  (D-6).
- `for a, b in g()` reports `name 'b' is not defined` (D-7).
- A generator defined in an *imported module* (`import genmod` or
  `from genmod import gen`) hits the iterable-kind error -- the `genFns`
  scan and the consumer rewrite only see the main module (D-8).

## 2. The principle, one paragraph

A Python generator is a resumable computation: a body that can hand a value
out and later continue where it stopped. CPython suspends a stack frame;
PyMCU has no stack to suspend -- but it already knows the equivalent
trick: `async def` lowers to a state machine whose resumable state lives in
fields (`_state`, params, locals that must survive a suspension). A
generator is the same machine with the event loop deleted: `yield v`
publishes `v` and returns "a value is ready"; `poll()` resumes the machine
at its state field. Everything the measurement calls GREEN is this machine
working. Everything it calls RED is a payload (`_value`), a declaration
(`global`), or an idiom (`async` + `yield`) the machine was never taught --
never a flaw in the model itself.

## 3. Representation

A generator function `def g(params)` becomes a ZCA class `g`:

```python
class g:
    def __init__(self, <params>):
        self._state: uint16 = 0          # machine state; 0x7FFF = done
        self.<param> = <param>           # params are always fields
        self.<local> = 0                 # locals that survive a suspension
        self._value: <narrowest int> = 0 # the yielded payload, when scalar
    def poll(self) -> uint8:             # 0 done / 1 working / 2 yielded
        if self._state == 0: <state-0 raw statements>
        if self._state == 1: <state-1 raw statements>
        ...
        return 0
```

- `_state`: `uint16` -- small ids plus the `0x7FFF` terminal.
- `_value`: the narrowest type covering every yielded expression
  (`uint8`/`int8`/`uint16`/`int16`, `float` when any payload is a float,
  `uint32` fallback). Under decision 3 a non-scalar yield emits *no*
  `_value` field; the `AssignStmt`s to it are dropped at EmitStmt, and
  the `for` desugar refuses the read naming the generator.
- Locals: a local touched in more than one state, or read before written in
  its only state, becomes a field; all others stay poll-locals. A name
  declared `global` is never collected (decision 5).
- `__init__` is force-inlined at the construction site (as every hand-written
  `__init__` is), so `g(args)` writes fields directly.
- **One static frame per instantiation site.** Each `for x in g(...)` gets a
  fresh hidden instance `__genN`; each `h = g(...)` binds `h`'s own slot.
  Two `g()` calls = two live frames (measured GREEN). Two loops over the
  same `h` share it -- the second `for` resumes the same machine, which is
  exactly CPython's semantics for a partially-consumed generator (measured
  GREEN). A `for x in g()` inside a function called twice reuses the one
  static frame and re-`__init__`s it per call -- safe because a PyMCU
  generator can never outlive the loop that drives it (decision 1: it
  cannot be stored, passed, or returned).
- Recursion stays refused: a generator calling itself would need a second
  frame of the same type, and there is no way to name it at compile time.
  `yield from` recursion is already refused by name; direct `g()` inside
  `g`'s body gets the same refusal in phase 1's diagnostic pass.

### Method generators

`class C: def read(self, params)` becomes a class `C_read` whose `__init__`
takes the receiver first:

```python
class C_read:
    def __init__(self, self_, params):
        self.self = self_               # the receiver, held by name-alias
        ...                              # _state, params, locals, _value
```

`self.x` inside the body reads `self.self.x` -- the receiver's slot, not a
copy. This is the same path as `def g(t): t.bump()` measured correct in
Phase 1 (writes through, including nested `t.inner.bump()`); the earlier
worry recorded in `RejectCoroutineMethods` ("a mutating method on a held
instance does not always stick", PyMCU#110) does not reproduce on the
param-held shape the compiler emits today.

`for x in obj.read()`: `obj`'s class comes from binding-tracking
(`obj = C(...)` at module or function level, mirroring `moduleBound`);
`obj.read(...)` desugars to `__gen = C_read(obj)` + the poll loop.
`S.gen()` for a `@staticmethod` resolves `S` directly. A receiver the
tracker cannot name -- `make_decoder().read()`, `self._dec.read()` (a
receiver in a field), `for x in bag.items() if cond else other` -- is
refused naming the method and the reason (section 8).

## 4. Size cost, measured

Unit: `pymcu build` "bytes of your code" (ATmega328P, default optimisation),
each program differing only in the generator.

| program | code bytes | delta |
|---|---:|---|
| baseline: 3 `print`s, no generator | 284 | -- |
| `def g(): yield 1` + `for x in g(): print(x)` | 390 | **+106 fixed** |
| same, 3 yields | 470 | +40/yield |
| same, 6 yields | 590 | +40/yield |
| `def g(n): yield n` (1 param, wider `_value`) | 562 | +172 over 1-yield no-param |

Reading it:

- **Fixed cost ≈ 106 B** for the first generator: `__init__` expansion,
  `poll` dispatch skeleton, `_state`/`_value` field traffic, and the
  consumer's `while True: poll` loop.
- **≈ 40 B per additional yield point**: each yield is one dispatch arm
  (`CPI _state`; `STS _value`; `STS _state`; `RET 2`) plus the resumed
  arm's dispatch test.
- **Params and wide `_value` cost the most**: one param + `yield n` costs
  +172 B over the no-param case (param field, `__init__` copy, `uint16`
  `_value` loads/stores). The demandant's byte report (section 10) is
  measured on the real `read()`.
- SRAM: one frame = 2 (`_state`) + 1..4 (`_value`, scalar only) + param
  fields + surviving-local fields, statically allocated once per
  instantiation site.

## 5. Calling convention

```python
__gen = g(args)                 # __init__ expanded inline at the site
while True:
    __gr = __gen.poll()
    if __gr == 0: break         # done
    if __gr != 2: continue      # internal transition
    x = __gen._value            # only when poll() said 2
    <body>                      # user's break/continue target this loop
```

- `return` inside the body: `_state = 0x7FFF; _value = expr?; return 0`.
- `for...else`: the loop-else desugar already composes with this rewrite.
- `for` over a *bound name* (`h = g(); for x in h:`) skips the construction
  line and polls `h` directly -- resumption, not a fresh machine.
- Everything is compile-time except `poll()`'s per-state work and the
  `poll()` call itself. No allocation, no allocation failure mode, no
  runtime type tests anywhere in the protocol.

## 6. Compile time vs run time

| decided at compile time | costs code at run time |
|---|---|
| the function *is* a generator (reachable `yield`) | -- |
| state split, state ids, which locals must be fields | `poll` dispatch chain (~4-6 B per state test) |
| `_value` scalar vs opaque | `STS _value` per yield (~4-8 B) when scalar |
| instantiation sites and their frames | `__init__` expansion per site |
| `yield from` expansion (same-module, non-recursive) | the delegate's states inline in the caller's machine |
| receiver binding for a method generator | `self` field store at `__init__` |
| `for` desugar | one `while True` + `CALL poll` + compares per loop |

## 7. What stays REFUSED, and why

| construct | status | reason |
|---|---|---|
| `next(g)`, `next(g, d)`, `iter()` | refused | no iterator protocol outside `for`; adding it buys one-step manual driving for a value the consumer can already get from `for` |
| `x = yield`, `g.send(v)` | refused | `yield` is a statement; a sent value would need a second payload direction through `poll` with no demandant |
| `g.close()`, `g.throw()`, `g.__next__()` | refused | the generator protocol; needs a generator object with identity/finalizer semantics that does not exist |
| `yield from <seq>` / `yield from <var>` / `yield from <non-gen>` | refused | delegation is compile-time expansion; a sequence-delegate could lower to `for x in seq: yield x` -- a later phase, needs `for`-over-seq inside generators first |
| `yield from` recursive/mutual/`return`-in-delegate | refused | expansion has no finite unrolling for cycles; `return` in a delegate ends the delegation, not the delegator -- a distinction with no representation |
| `yield` inside `try`/`except`/`finally`(real)/`with`/`match` | refused | suspension across a pending exception context or context-manager `__exit__` needs resume-into-handler machinery; measured D-1 makes today's silence a *finding*, and phase 1 makes it an honest refusal |
| `for x in <non-range>` inside a generator | refused | `EmitFor` lowers `range` only; extending it to const seqs/arrays/strings/generator pipelines is a later phase |
| `await` inside a generator | refused | the two idioms do not compose |
| `yield` inside `async def` (async generator) | refused | decision 6; no model for push+pull+scheduling |
| `yield` inside `@inline` | refused | @inline expands into call sites; a generator needs its own state machine -- these are opposites |
| generator in an imported module | refused | consumer rewrite is main-module-only today; supporting it is a later phase (per-module `genNames` + member-call consumers) |
| `list(g())`/`sum(g())`/`zip`/`enumerate`/`reversed` over a generator | refused | no iterator object; `for` is the only consumer |
| generator as a value (arg, return, field, list element, conditional binding) | refused | a machine is a static slot, not a value |
| generator recursion (direct `g()` inside `g`) | refused | needs a second frame of the same type |
| unbounded recursion via `yield from` | refused | same, by name |
| `for` over a generator inside `with`/`match` bodies | refused | consumer sites are plain statements today; a later phase |

## 8. Diagnostics

Every refusal names the construct the user wrote, says why, and names no
generated internals (`poll`, `_value`, `__gen*`).

| shape | today (measured) | proposed |
|---|---|---|
| `yield` in a method | `` `yield` in method 'items' of class 'Bag': a generator has to be a module-level function today ... Move it out of the class ... `` | compiles (phase 1) |
| `yield` inside `try`/`with`/`match`/`except` | generic for-in iterable list at the `for` line | `` `yield` inside `try`/`with`/`match` is not supported yet: a suspension cannot cross a pending handler or `__exit__`. `` at the `yield` |
| `if k: return` in a generator | `` this return gives None ... 'poll' is declared to return uint8 `` | compiles (decision 7) |
| `for x in <seq>` inside a generator | `` async def 'g': `await` inside a for-loop ... `` | `` `for` over <kind> inside a generator is only supported for `for i in range(...)` yet. `` |
| `await` inside a generator | `` async def 'g': ... `` | `` generator 'g' contains `await`; a generator cannot await -- use `async def` (a coroutine) or `def` (a generator), not both. `` |
| `x = yield` / `send` | generic VisitYield / `send` is named already | keep the `send()` text; route `x = yield` to it |
| `yield <non-scalar>` then consume | silent `0`/`256`/garbage, or `'x' is not a member of a numeric value`, or `_value`/`poll` field errors | at the `for`: `` generator 'g' yields <type> values, which have no scalar representation; the value cannot be read in a `for` loop. `` |
| `for a, b in g()` | `` name 'b' is not defined `` | `` `for` over a generator takes one loop variable; tuple unpacking is not supported. `` |
| `import genmod; for x in genmod.g():` | generic iterable list | `` 'g' is a generator in module 'genmod'; PyMCU generators are consumed in the module that defines them. `` |
| `for x in <untracked>.read():` | n/a | `` cannot tell which class '<expr>' is here; a generator method needs a receiver bound as `name = ClassName(...)`. `` |
| `async def g(): yield` | silent coroutine termination | `` `yield` inside `async def g` makes it an async generator, which PyMCU does not support: a coroutine awaits, a generator yields -- not both. `` |
| `global T` in a generator | silent field shadow | compiles (decision 5) |

## 9. Phases

- **Phase 1 (this campaign)**: generator *methods* with receiver capture;
  decisions 3 (opaque `_value`), 5 (`global`), 6 (async-gen refusal), 7
  (`return` in kept statements); the `ContainsYield` deep-walk so every
  refusal lands on the `yield` and names the construct; all of section 8's
  renamed diagnostics. Demandant: `adafruit_irremote` builds unmodified;
  `test02`/`test03` match CPython on the emulator.
- **Phase 2**: `for` over const sequences/fixed arrays/strings inside a
  generator; generator pipelines (`for v in inner()` inside `outer`);
  `yield from <seq>`; tuple loop targets.
- **Phase 3**: imported-module generators (per-module `genNames` +
  `mod.g()` consumers); `for`-inside-`with`/`match` consumer sites;
  `_value` holding a *single* instance type via the field-alias machinery
  (`yield <instance>` of one class).
- **Phase 4 (needs RFC 0009 phase 2+)**: `_value` as a tagged union for
  `None`-able scalar yields (`yield None` consumed as `x is None`), the
  demandant's union-of-instances yield if instance-union members ever gain
  a representation; `float` `_value`.
- **Gate, every phase** (mirrors RFC 0009 section 10): a program with no
  generator is byte-identical; a program whose generator use is already
  GREEN keeps emitting byte-identical code; the `GenExp` compile-time
  reductions emit exactly today's code -- measured as zero size delta on
  every current generator-expression fixture.

## 10. Demandants and open questions

1. **The irremote `yield result`** is a `None | IRMessage |
   UnparseableIRMessage | NECRepeatIRMessage` union of namedtuple
   instances -- unrepresentable as a scalar *and* refused as an instance
   union under RFC 0009. Phase 1's opaque `_value` (decision 3) is what
   makes it compile honestly: the machine suspends, the payload reads are
   refused at any consumer that appears. Whether phase-3/4 ever gives the
   union a representation is an open question -- the demandant's own tests
   never read it.
2. **Two receivers of the same method in flight** (`for x in a.read()` and
   `for y in b.read()` interleaved): each `for` site owns a `C_read` frame
   holding its own receiver -- measured safe by construction (two
   `g()`-site instances already hold distinct state).
3. **`_value` for `yield`/`yield None` when the consumer wants the count
   only** (`for _ in g(): n += 1`): still refused on read; a bare-yield
   generator is legal control flow and refuses only the payload.
4. **The `poll()==1` internal-transition code** exists for coroutine
   awaits; a pure generator today only emits 0/2 and `goto`s. Keeping `1`
   in the shared protocol costs nothing; unifying the two protocols is how
   a later `await`-in-generator or scheduler-driven generator would slot
   in.

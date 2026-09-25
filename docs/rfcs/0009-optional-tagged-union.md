# RFC 0009: Optional[T] and Union returns as a tagged union -- one tag byte, only when None-ness is a run-time fact

- Status: **IMPLEMENTED (all phases)**. `-> Optional[X]` / `-> Union[X, None]` on real
  subroutines and locals carry the payload plus one tag byte as specified here; the
  readers, narrowing and the decision-5 diagnostic are in. Phase 3 adds
  `-> Union[A, B, ...]` member lists (up to four members, `None` anywhere in the list,
  `A | B` spellings included): the payload is the widest member's storage, the tag
  reports the member index, `isinstance(r, T)`/`match` dispatch on it, and
  compile-time-decidable unions keep byte-identical code. Union-typed fields carry a
  flattened payload plus a sibling tag byte (the `adafruit_dht` `_temperature`/`_humidity`
  demandant). Union parameters on real subroutines stage the member byte in the argument
  run immediately after the payload (the `adafruit_ht16k33` `pixel` demandant, including
  its bound-instance outlined form). A local merged `None` under a runtime condition
  carries the tag; writes that can only meet sequentially stay provable.
  Measurements: `docs/rfcs/0009-measurement-2026-09-21.json`; the GAS snippets and the
  avr8sharp harness live on branch `rescue/optional-proto` under `proto/optional-tag/`
  and are never merged into this branch.
- Date: 2026-09-21
- Demanding program: `adafruit_dht.DHT11.temperature`
  (`-> Union[int, float, None]`, adafruit_dht.py:263). `measure()` writes
  `self._temperature` on success or raises `RuntimeError`; the property returns the field,
  so the None-ness is a fact stored in a field at run time and forwarded across a call
  boundary. `adafruit_motor.servo` (`fraction`/`angle`/`throttle -> Optional[float]`),
  `adafruit_bmp280.pressure -> Optional[float]`, `adafruit_ht16k33.pixel -> Optional[bool]`
  and `adafruit_irremote.read_pulses -> Optional[list]` are the same shape.
- Affects (once implemented): `src/compiler/Common/AnnotationText.cs` (Optional/Union
  must stop forgetting which member list was written), `CheckAnnotationNames` in
  `src/compiler/IR/IRGenerator/Assign.cs` (the union refusal moves off returns), the
  `VisitReturn` paths in `src/compiler/IR/IRGenerator/Statements.cs` (both the inline
  refusal and the silent real-subroutine `Return(NoneVal)`), the `is None` fold in
  `src/compiler/IR/IRGenerator/Expr.cs`, field layout in `Scan.cs`, `extensions/
  pymcu-sdk/csharp/IR/Tacky.cs` (`Return`/`Call`/`Function`), and every backend's
  return-lowering (in `pymcu-avr`, `CompileReturn` and the call sequence).
- Builds on: the 2026-09-14 `Optional[X]`-is-`X` decision (RFC-adjacent, recorded in
  `docs/language/limitations.md`) and the T-flag exception model of RFC 0003, which owns
  the SREG T bit today.

## 0. Decisions this RFC encodes

1. **The tag is a byte, not a flag.** A function declared `-> Optional[X]` (or
   `-> Union[..., None]`) whose None-ness is decided at run time returns `payload` in the
   ordinary result registers and `tag` in the next register of the return run: R25 for an
   8-bit payload, R22 for a 16-bit one, R20 for a 32-bit/float one. The SREG T bit is the
   exception channel and stays that: a `try` body puts a `BRTS` after every call it
   contains (emitted before `CanFail` is known), so a None signalled through T would be
   read as an exception, and a CanFail callee has no T left to signal with. The headline
   demandant raises `RuntimeError`, so a T-flag wire cannot serve it at all.
2. **The tag is spent only when the None-ness is a run-time fact.** When every reached
   path is provable at compile time -- the guard folds, the callee's `return None` is on
   a dead arm, the field is per-instance constant -- the value stays `T` and the code is
   what today emits, byte-identical. That is the gate.
3. **`is None` narrows.** Inside `if r is not None:` (and after `if r is None:
   return/raise`, on the fall-through), `r` reads as its payload type. Arithmetic or
   attribute access on a runtime-tagged name outside a narrowing arm is a CompileError:
   CPython raises TypeError there, and PyMCU's version of a provable run-time type error
   is a refusal at compile time (same rule as a compile-time `assert` that is false).
4. **`Union[A, B]` of two real types on a return is the same mechanism**, not a new one:
   the tag carries the member index, the payload is the widest member, `isinstance(r, T)`
   folds to a tag compare. A union of ZCA instance types is out of scope: an instance is
   not a value, and two instance types do not share a representation (RFC 0006 section
   3.4 covers the aliased case; a union adds nothing to it).
5. **`return None` on a reached path of a function declared `-> X`** (no `None` member)
   becomes a CompileError on a real subroutine exactly as it already is on an inline
   callee. Today it silently returns whatever R24 held -- measured below, this is the
   worst behaviour in the matrix, and the fix costs nothing because the check already
   exists for the inline shape.
6. **Sentinels are an optimization, never the representation.** Where range analysis
   proves a payload value free (`r & 0x7F` leaves 0x80-0xFF), the tag may fold into it;
   where it cannot, every value of `T` must stay distinguishable from `None`. Never
   emitted by default.
7. **A read site that can represent both outcomes consults the tag.** `print(r)` and
   f-string interpolation `{r}` are the first such sites: each real member prints by
   its own repr (the int member through the decimal writer, the float member through
   the float writer, a bool member as `True`/`False`), and the None member writes the
   literal `None` -- the text CPython produces. The tag is the cost of that notation,
   the same one a `Nullable<T>` pays in C# and a `std::optional<T>` in C++: a marker
   beside the payload saying which member is live. The difference is decision 2 --
   those languages pay it on every value of the type, and PyMCU pays it only where the
   None-ness is a run-time fact, so a compile-time-decidable Optional still emits
   byte-identical code. Amended (implemented): sites that cannot represent both do not
   refuse either -- they dispatch on the tag. Arithmetic (`+ - * / // % **`), unary `-`
   and `~`, the ordering comparisons, `len()`, a subscript, and an argument bound for a
   parameter that is not Optional each lower to one leaf per member combination: a live
   member runs the operation with the payload read at its own width, and a leaf that
   lands on the None member raises TypeError at run time through the RFC 0005
   deferred-print channel, worded exactly as CPython words it
   (`unsupported operand type(s) for *: 'NoneType' and 'float'`). That is the same
   bargain C# and C++ strike -- arithmetic on an empty `Nullable<T>`/`std::optional` is
   a run-time fault, not a compile error -- paid only where the None-ness is a run-time
   fact: a proven non-None operand still emits byte-identical code. Sites that stay
   refused: a member read or a call on the Optional itself (`r.field`, `r()`), a format
   spec on an unnarrowed value (`{r:.1f}` reads the payload), and bitwise ops.

## 1. The measured problem

Today `Optional[X]` is read as `X` and None-ness is a compile-time property
(`noneValuedNames`, `IsNoneValued`): a `None` literal, default, or argument binds the name
as None-known, and `x is None` folds. That is correct and zero-cost everywhere it can be
decided at compile time. It runs out in exactly one place: **a `return` whose None-ness
is a run-time fact**, and behind it, a field or local whose None-ness is a run-time fact.

What the user sees today, measured with scratch projects built by
`~/Repos/pymcu-avr-rescue-measure/.venv/bin/pymcu` (pymcu-compiler 0.1.0b1):

| shape | today |
|---|---|
| real subroutine `def read() -> Optional[uint8]` with a reached `return None` | **no diagnostic; silent miscompile.** The `.mir` carries `{"$t":"ret","value":{"$t":"none"}}`; `CompileReturn` (`pymcu-avr` `AvrCodeGen.cs:2026`) skips the load for a `NoneVal`, so `RET` runs with R24 holding whatever the previous expression left -- in the test program, the pin bit it just read. At the caller, `if r is None:` folds to **False** (the returned temp is not None-known) and the arm is deleted. Wrong on both sides, no word said. |
| `@inline` callee, same body | `CompileError` at the return: "PyMCU reads Optional[X] as X, and this return gives None at run time, which has no width to put in the result of 'read'. Return a value on this path, or let the caller decide before calling (the compiler folds `is None` on an argument it can see)." -- correct as far as it goes; the feature this RFC adds is what the sentence says does not exist. |
| `-> Union[int, float, None]` (the dht spelling) | `CompileError` at the `def`: "a union type annotation is not supported. PyMCU needs one concrete type, because the storage for a value is decided at compile time and two types do not share a size" |
| `self._x: Optional[uint8] = None`, then `self._x = <runtime>` / `= None` | **no diagnostic; silent miscompile.** Both stores emit nothing (the field never gets storage), and `s._x is None` folds on whichever write the compiler walked last. |
| `def f() -> int:` (no Optional) with a reached `return None` | **no diagnostic; silent garbage**, same mechanism as row 1. |
| `v = r or 99` on that return | silent; reads the residual register. |
| `x: Optional[uint8] = None` parameter | works at zero cost, per the 2026-09-14 decision; unchanged by this RFC. |

So the present state is: the inline path refuses honestly, and **every real-subroutine
shape compiles to silently wrong code**. The measured cost of fixing it is in section 4.

## 2. The principle, one paragraph

Rust's `Option<T>` is a discriminant plus a payload; C spells it
`struct { bool has; T v; }`; Zig's `?T` picks a sentinel bit pattern when the type has a
free one and a tag byte when it does not. All three agree on the shape: **the absence of a
value is itself a value, stored next to the payload**. PyMCU's model ("None is a
compile-time value") is the same statement one level up: when the compiler can prove the
state, the tag *is* the proof and costs nothing; when it cannot, the tag is a byte that
travels with the payload. This RFC adds the second half of that sentence. What it
deliberately does not add is a heap box, a tagged pointer, or `Option` as a library type:
the tag lives in the value's own storage, which is what keeps a folded Optional free.

## 3. Representation

A name of type `Optional[X]` / `Union[...]` whose state is a run-time fact occupies
`1 + sizeof(widest member)` bytes of storage: the payload bytes plus a **tag byte holding
the member index, with `None` always the last state**.

- `Optional[uint8]`: tag 0 = has value, tag 1 = None.
- `Union[int, float, None]`: tag 0 = int, tag 1 = float, tag 2 = None. On the wire each
  member returns in its own natural registers (R24:R25 for the int member, R22:R25 for
  the float member -- no repacking); the tag tells the reader which registers are live.
  In storage the payload field is sized to the widest member and a narrower member
  occupies its low bytes.
- `Union[int, float]` (no None): tag 0 = int, tag 1 = float. Same machinery; the union
  refusal lifts from return/storage positions, not only `X | None`.

Where the tag lives:

| position | tag storage |
|---|---|
| local (`r = read()`) | a second byte in the local's home: a sibling register while register-resident, a sibling stack byte when spilled |
| field (`self._x: Optional[uint8]`) | one extra byte in the flattened layout, after the payload bytes (Model B slot / RFC 0006 layout; a folded instance still pays nothing) |
| module global | one extra byte in the static slot |
| parameter of a real subroutine | the byte after the payload inside the argument run: an 8-bit `Optional` arg costs nothing extra (args occupy 2-register slots already); a 16-bit one consumes the next slot's low byte |
| return value | the register after the payload in the return run (section 4) |
| `Optional[Tuple[...]]`, `Optional[list]`/`Optional[bytes]` | out of scope for phase 1: tuple returns are already multi-slot per call site and buffers travel as names; both are noted under open questions |

Assignment rules follow the storage: `r = read()` copies payload and tag; `r = None`
stores the None state into the tag (the payload is then don't-care); `r = 5` stores
payload and tag 0. A name whose tag can differ across the arms of an `if`, or across a
loop back-edge, is runtime-tagged exactly where a constant-known name would be forgotten
today.

## 4. Calling convention, measured

The measured unit is the demanding shape:

```python
def read() -> Optional[uint8]:
    if <PINB.0>:        # a run-time fact the compiler cannot fold
        return None
    return 42

r = read()
if r is None:
    <none arm>          # DDRB5/PORTB5 set (led on)
else:
    <use arm>           # PORTB = r (writes 42)
```

Hand-written GAS for each candidate (the method of RFC 0006 section 7 -- mechanism cost,
not the compiler's actual output), assembled with `avr-as`/`avr-ld` (binutils 2.45.1),
byte counts from `nm` label deltas, cycles measured on avr8sharp
(`Avr8Sharp.TestKit 1.1.0-beta1`, `Cpu.Cycles` between the same labels). Both arms were
run and verified semantically (value arm leaves PORTB=0x2A, none arm leaves
DDRB=PORTB=0x20).

| candidate | callee bytes | caller bytes | code bytes | cycles (val/none) | delta vs base |
|---|---:|---:|---:|---:|---|
| base: `-> uint8`, no None anywhere | 14 | 6 | 44 | 13 / 14 | -- |
| (a) T-flag, tag materialized (`MOV`+`BLD`, `TST`+`BRNE`) | 16 | 20 | 60 | 20 / 22 | +16 B, +7/+8 cyc |
| (a') T-flag, consumed at once (`BRTS`) | 16 | 14 | 54 | 17 / 19 | +10 B, +4/+5 cyc |
| **(b) tag byte in return pair (u8: R24 payload, R25 tag)** | 16 | 18 | 58 | 19 / 21 | +14 B, +6/+7 cyc |
| (b) same, 16-bit payload (R24:R25, tag R22) | 18 | 18 | 60 | 20 / 21 | +16 B, +7/+7 cyc |
| (c) sentinel (0xFF = None, provable range only) | 14 | 16 | 54 | 17 / 20 | +10 B, +4/+6 cyc |
| (d) out-param (caller passes `&{payload,tag}`) | 24 | 28 | 76 | 28 / 27 | +32 B, +13/+15 cyc |
| `Union[int, float, None]` under (b): payload R22:R25, tag R20, 3 states | 22 | 26 | 72 | 25 / 21 | +28 B over base |

Reading the table:

- **Callee side, (a) and (b) are identical**: one extra instruction per return path
  (`CLT`/`SET` vs `CLR tag`/`LDI tag,1`), 2 bytes each. The difference is entirely on the
  caller side and in what each wire forbids.
- **(b) beats (a) once materialized** (18B vs 20B caller): `MOV r_tag, R25` is one
  instruction; getting T into a byte takes two (`MOV r19, R1` + `BLD r19, 0`). (a) only
  wins when the check is the very next instruction (`BRTS` alone, a') -- and that form is
  illegal exactly where it matters: inside a `try` body every call is followed by a
  `BranchOnError`/`BRTS` emitted before `CanFail` is known, so a None return would
  dispatch to the exception handler; and a CanFail callee cannot signal None through T at
  all. `adafruit_dht.measure()` raises `RuntimeError`, so the demanding program needs (b)
  regardless. (a) stays as an open question for a later pass that strips dead
  `BranchOnError`s after provably-non-CanFail calls.
- **(c) is the cheapest read** but is not a representation: it exists only where a range
  proof leaves a payload value free. Note it as an optimization, never the default.
- **(d) is the cost of persistence, not of the call**: pointer setup plus indirect stores
  doubles the wire cost, and it is also exactly what a tag field costs (the store side of
  the `f_field` measurement: `ST`/`STD` into a slot). Where the value must live in memory
  anyway -- a field, a spilled local, a module global -- (b)'s register tag and (d)'s
  slot byte are the same byte.
- The union row is (b) with a wider payload and one more `CPI`+`BREQ` per extra member;
  the tag machinery itself is unchanged.

SRAM: a tag costs one byte wherever the value persists (field, spilled local, global);
zero while the pair is register-resident.

## 5. Reading the tag (user-visible semantics)

| construct | lowering on a runtime-tagged name |
|---|---|
| `r is None` / `r is not None` | tag compare: `TST tag` + `BRNE`/`BREQ` for Optional (None is the last state); `CPI tag, N-1` for a union |
| `if r:` | falsy when the tag says None, else the payload's own truthiness: `TST tag; BRNE false_arm` then the payload test |
| `r or default` / `a = r if r is not None else d` | tag test selecting payload or default |
| `isinstance(r, T)` on a union | `CPI tag, <index of T>` + branch; tuple-of-types is a compare per member |
| `match r:` with `case None:` / `case <type>():` | dispatches on the tag; note for phase 3 |
| `r + 1` (arithmetic, ordering compare), unary `-`/`~`, `len(r)`, `t[i]`, `f(r)` to a non-Optional param | member dispatch: each live member runs the op at its own width; a leaf landing on None raises TypeError at run time (RFC 0005 deferred print, CPython's wording). Was a CompileError before the decision-7 amendment |
| `r.field`, `r()`, `{r:.1f}` on an unnarrowed Optional | still a CompileError: "r may be None here; narrow it first (`if r is not None:`)" -- a member read, a call, or a format spec cannot name which member to dispatch to |
| inside `if r is not None:` | `r` reads as the payload type; the tag is not consulted again in that arm |
| `print(r)` | one tag compare per real member, then the member's own writer; the None member writes the literal `None` |
| `f"{r}"` | same dispatch at the interpolation site; a format spec (`{r:.1f}`) is a payload read and stays refused unnarrowed |

### The cost table, per decision 7

| what | cost |
|---|---|
| Optional provable at compile time | nothing: no tag storage, no wire register, no dispatch -- byte-identical (decision 2) |
| runtime Optional local or field | payload + one tag byte in storage; a tag copy on each store |
| runtime Optional across a call boundary | the tag byte after the payload in the return/argument run (section 4) |
| `print(r)` / `f"{r}"` on a runtime Optional | a `CPI`/`BRNE` per real member plus the member writer the program already had; `None` is the fall-through |
| a name proven None on this path | the literal `None` write only -- the fold, no tag read |
| arithmetic, unary `-`/`~`, ordering compare, `len`, index, non-Optional parameter on an unnarrowed Optional | a `CPI`/`BREQ` per member combination plus each leaf's own code; the None leaf is a `TypeError` raise through the deferred-print channel. A proven non-None operand never reaches the dispatch |

A return of an already-tagged name (`return self._temperature`, the dht shape) copies the
field's tag byte to the tag register and the payload to the result registers -- the tag is
a value in IR, not a condition code, so forwarding is a `Copy`, not a special case.

## 6. Union[A, B] of two real types

Same tag byte, N states, payload sized to the widest member; narrower members occupy the
low payload bytes and are read in their own width under `isinstance`. This is what turns
`-> Union[int, float]` and `-> Union[int, float, None]` from a refusal into the same
mechanism Optional uses.

Parameters of real subroutines under a union annotation work the same way (tag byte after
the payload inside the argument run), which is phase 2, not phase 1: the two demandants
for union *parameters* are different problems -- `adafruit_ht16k33`'s
`address: Union[int, List[int], Tuple[int, ...]]` is resolved per call site today
(@inline dispatch), and `adafruit_debouncer`'s `Union[ROValueIO, Callable[[], bool]]` is
a union of *behaviours*: `hasattr(x, "value")` picks a protocol member at construction,
which is a call-site/structural question (it currently fails on member matching, a
separate gap), not a run-time tag on a value.

A union whose members are both ZCA instance types is refused: instances are not values
(RFC 0006 gives them a layout but no shared representation across different classes), and
there is no demandant. Same refusal, new wording: it names the two types.

## 6.1 N members inferred from the return statements themselves

A function with no return annotation whose `return` statements produce different types on
different paths (`return 5` on one arm, `return 2.5` on another, `return None` on a third)
is the same object as a declared `Union[int, float, None]`, and the tag is the same byte:
the member INDEX of the type returned, not the index of the path. Two paths that return an
int share a tag; the member list is the set of distinct representations across every
`return` reached, in first-appearance order, deduplicated by representation width and kind
(uint8 and bool are distinct members, as 11.6 says; two `return 5` are one). Today's
inference already gives an unannotated function one return type when every path agrees;
this section only says what happens when they do not: the compiler infers the union
instead of refusing or, worse, picking the first path's type and reading garbage on the
others (the silent case 0.5 measures).

The limit is not the byte (255 members fit) but the READERS. Every use of the value has to
dispatch on the tag, and each arm is compiled for its member: with 2 or 3 members that is a
flat `CPI`/`BREQ` chain and a small body per arm (section 4 measures the 3-state case at
+28 bytes over the base); with ten it is a jump table and ten bodies at every consumer,
the program stops being monomorphic, and the author is asking for a dynamic type system
with extra steps. So:

- **Inferred or declared unions of up to 4 scalar members are accepted.** The tag is one
  byte, the payload the widest member, `isinstance(r, T)` folds to a tag compare, and a
  read outside an `isinstance`/`is None` arm is the same CompileError section 5 names.
- **Five or more members are refused**, with a message that lists the members and the
  return lines that produce them, and says why: "a value with N possible types makes every
  reader dispatch N ways; PyMCU keeps one type per value. Return one type, or split the
  function". This is a design ceiling, not a technical one; raise it only with a demandant
  and a measurement.
- **Members that are ZCA instances are refused** as in section 6 (no shared
  representation), naming the return lines.
- **Provable paths do not count.** A `return` on an arm the compiler folds away (a constant
  guard, a dead `except`) contributes no member; a union that collapses to one member is
  the plain type at zero cost, byte-identical to today. That is the gate of section 10
  applied to inference.
- The diagnostic for a refused union prints the member list with one return line each, so
  the author sees the paths, not a type name.

### 6.2 The `Result` shape: a union as an error channel without exceptions

The N-state tag makes a second idiom cheap: `Union[int, ErrorCode]` (or `Union[T, None]`
with the None arm meaning "failed"), the `Result<T, E>` of Rust and Zig's error unions. On
a microcontroller it is attractive: one tag byte on the wire, no T-flag bookkeeping, no
deferred print, and the caller decides at the read site. It is exactly what section 4's (b)
already provides when `E` is a scalar (an `IntEnum` member or a `uint8` code), so it costs
this RFC nothing to name it.

It stays a LATER phase, not phase 1: the demandants (the Adafruit corpus, the MicroPython
and CircuitPython idioms) write `raise`, and RFC 0003/0005 already give `raise` a working
model. `Result` becomes a recommendation for PyMCU-native code (HAL, drivers written for
PyMCU) once the tag exists; it never replaces exceptions in code written for an
interpreter. Where a PyMCU-native function returns `Union[T, SomeErrorEnum]`, the
documentation should show the two spellings side by side with their measured cost, so an
author picks with numbers.

## 7. Demandants, from the Adafruit corpus

Grepping `~/PycharmProjects/cp-*/src` (20 libraries):

- `adafruit_dht` (`temperature`, `humidity -> Union[int, float, None]`): the None lives in
  `self._temperature`/`self._humidity` fields written by `measure()`; the property
  forwards the field. Needs field tags (phase 2) plus the return tag (phase 1); also
  `CanFail` (raises `RuntimeError`), which is why the wire is (b) and not (a).
- `adafruit_motor.servo` (`fraction`, `angle`, `throttle -> Optional[float]`): None-ness
  from `self._pwm_out.duty_cycle == 0`, decided at run time; `angle` calls `fraction` and
  re-None's -- Optional through a second call boundary.
- `adafruit_bmp280.pressure -> Optional[float]`: the annotation is wider than the body --
  no path returns `None` (the docstring's "None if pressure measurement is disabled" is
  stale), so under decision 2 it compiles as a plain `float` return with zero tag cost.
  It is the gate working in the user's favor, and a good phase-1 fixture for exactly that
  reason.
- `adafruit_ht16k33` `pixel`/`__getitem__`/`_pixel -> Optional[bool]` (None = "get",
  value = "set" argument form) and `matrix.py:232 -> Optional[int]`.
- `adafruit_character_lcd`: `message -> Optional[str]`, `text_direction -> Optional[int]`,
  `backlight -> Optional[bool]` (properties reading optional HAL state).
- `adafruit_irremote`: `_read_pulses_non_blocking`/`read_pulses -> Optional[list]` -- a
  None-able buffer; phase-1 scope excludes `Optional[list]` (a buffer travels as a name,
  not a register value), noted under open questions.
- `adafruit_debouncer`: `Union[ROValueIO, Callable[[], bool]]` parameter -- a union of
  behaviours, resolved at the constructor call site; a different mechanism, unchanged.

## 8. Diagnostics

All on the author's line, none naming a compiler internal. "Today" rows are verbatim
from the measured scratch builds.

| shape | today | proposed |
|---|---|---|
| `-> Optional[X]` real subroutine, reached `return None` | silent garbage | compiles; tag emitted |
| `@inline`, same | "PyMCU reads Optional[X] as X, and this return gives None at run time, which has no width to put in the result of 'read'. ..." | compiles; tag materializes into the caller's local |
| `-> Union[int, float, None]` | "a union type annotation is not supported. PyMCU needs one concrete type, because the storage for a value is decided at compile time and two types do not share a size" | compiles; 3-state tag + float-wide payload |
| `-> X` (no None member), reached `return None` | **silent garbage** | "this return gives None at run time, and 'f' is declared to return X. If the None is real, write `-> Optional[X]`; if this path should not be reached, guard it." |
| `self._x: Optional[X] = None` + runtime write | silently drops both stores | field carries a tag byte |
| `r + 1` on an unnarrowed runtime Optional | n/a (r was garbage) | compiles; the None leaf raises `TypeError: unsupported operand type(s) for +: 'NoneType' and 'int'` at run time -- uncaught, it prints through the RFC 0005 channel and halts like any unhandled raise |
| `Optional[Pin]` (instance member) | reads as `Pin` (compile-time None) | refused on a runtime-tagged path: "Optional of an instance type is not supported; an instance is not a value that can be present or absent in storage" |
| `Optional[list]`/`Optional[bytes]` return | reads as the buffer type | phase 1: refused, naming that buffers travel as names; open question below |
| `@export_c`/`@extern` boundary | n/a | refused like the CanFail export rule: "an exported function cannot return `Optional[X]`; a C caller has no tag to read." `@extern` declarations stay untagged by construction. |
| a `.mir` carrying `returnMembers` fed to a backend that predates tag support | n/a | backend refuses: same contract as `RequireDevice()` -- a version-mismatched .mir stops the build instead of silently dropping the tag |

## 9. IR and pipeline shape

- `AnnotationText.Normalize` keeps the member list instead of erasing it: `Optional[X]`
  normalizes to the payload type plus a recorded member list `["X", "None"]`;
  `Union[...]` likewise. Positions that keep their refusal (a union on an inline
  parameter is already handled per call site; a union local without None is phase 3) are
  unchanged.
- `Function` gains `ReturnMembers: List<string>` (empty = ordinary return). `Return`
  gains `Val? Tag`; `Call` gains `Val? TagDst`. The tag is an ordinary byte `Val` --
  `JumpIfZero`/`JumpIfEqual`/`Copy`/`TST`-equivalent IR already covers every use, so no
  new instruction exists. A backend that sees `ReturnMembers` non-empty lowers payload
  and tag to the section-4 registers; CanFail is orthogonal (T stays the error channel).
- The `is None` fold in `Expr.cs` gains a third answer between "compile-time None" and
  "never None": "runtime tag -- emit the tag test". Narrowing (decision 3) is bookkeeping
  on the same machinery that forgets constants at branch joins.
- The silent real-subroutine hole (decision 5) is one check in `VisitReturn`'s non-inline
  path, mirroring the inline refusal at `Statements.cs:1386`.

No compiler prototype was built for this RFC: the tag's wire half lives in the AVR backend
(`pymcu-avr`, `CompileReturn` and the call sequence), so a front-end-only prototype cannot
lower what it emits and there is nothing end-to-end to measure. What is measured is the
representation itself, in GAS, exactly as RFC 0006 measured its cost model.

## 10. Phases and the gate

- **Phase 1 (AVR)**: `-> Optional[X]` on real subroutines and locals; the tag byte on the
  wire and in local storage; `is None`/`is not None`/`if r`/`r or d`; narrowing; the
  decision-5 diagnostic. Demandant: a scratch `Optional[uint8]` sensor read plus
  `adafruit_bmp280.pressure` compiling unmodified.
- **Phase 2**: Optional fields in Model B slots and Optional parameters on real
  subroutines (tag after the payload in the arg run). Demandant: `adafruit_dht`
  (`self._temperature`) and `adafruit_motor.servo` (`angle` calling `fraction`).
- **Phase 3**: `Union[A, B, ...]` returns incl. `Union[int, float, None]`; `isinstance`
  dispatch; `match` on the tag; union parameters. Demandant: `adafruit_dht.temperature`
  as annotated.
- **Gate, every phase**: every fixture and example that has no runtime-None path is
  byte-identical against `docs/rfcs/0006-baseline-2026-09-15.json`-style corpus counts;
  a program whose Optional is provable keeps emitting exactly today's code (the tag is
  emitted only when the None-ness is a run-time fact); a `try` body's error dispatch is
  unchanged (the tag byte never shares the exception channel).

## 11. Open questions

1. **T-flag as the tag wire for provably-non-CanFail callees.** Measured saving is 2-4
   bytes per call site over (b) and only in the immediate-check shape; it also requires a
   pass that strips dead `BranchOnError`s after non-CanFail calls (today every call in a
   `try` keeps one). Worth it only if the strip lands anyway; left out of all phases.
2. **`Optional[list]`/`Optional[bytes]`** (irremote's `read_pulses`): a buffer's "value"
   is a name, not a register; a runtime-Optional buffer wants "tag + aliased storage".
   Probably fine (tag byte + the existing name-binding machinery) but needs the alias
   story spelled out before promising it.
3. **`Optional[Tuple[int, int]]`**: tuple returns are already N named slots per call site;
   an optional tuple is "tag + N slots". No demandant yet.
4. **Sentinel folding**: where a range proof leaves a payload value free (`x & 0x7F`),
   emitting (c) saves the tag byte in storage as well as the wire. Pure optimization; a
   wrong proof is a miscompile, so it needs the range machinery to be sound first. This
   is the escape hatch Rust already ships as the *niche optimization*: `Option<&T>` is
   pointer-sized because the null bit pattern doubles as the discriminant, and
   `Option<NonZeroU32>` pays nothing for the same reason. The day sentinel folding
   lands, the decision-7 costs that survive are only the ones with no free pattern --
   a `None`-tagged `Optional[uint8]` can hide in 0x80-0xFF when the program masks, and
   the tag byte disappears where the proof reaches.
5. **`Optional` on PIC/ARM/RISC-V**: the IR shape is arch-neutral (`Return.Tag`,
   `Call.TagDst`); each backend picks its own tag register. PIC14's banking may prefer a
   GPIOR-style flag byte over a register; measure when a PIC demandant appears.
6. **`Union` of an int and a bool**, or other members CPython distinguishes but PyMCU
   stores identically (bool is uint8 here): the tag keeps them distinct even when the
   payload width is shared, which is *more* faithful than today.
7. **The 4-member ceiling of 6.1.** Chosen from the reader cost, not measured on a
   demandant: no Adafruit library returns more than three types from one function. Revisit
   with the first program that needs five, and measure its readers.

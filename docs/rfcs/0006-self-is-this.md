# RFC 0006: self is this -- instances always have a representation, methods compile once, the optimizer specializes

- Status: **PROPOSED**. No compiler changes in this RFC; it captures the design and the
  measurements the implementation must be held to. Phase 1 starts from
  `docs/rfcs/0006-baseline-2026-09-15.json`.
- Date: 2026-09-15
- Affects (once implemented): `src/compiler/IR/IRGenerator/Scan.cs` (`IsOutlineSafe`,
  `classFieldLayout`, `RegisterOutlinedMethod`), `Call.cs` (force-inline fallback),
  `Assign.cs` (slot construction), `Expr.cs` (`self.<field>` resolution), the AVR backend in
  `pymcu-avr`, `src/driver/commands/build.py` (size report), `lib/src/pymcu/` (HAL classes
  whose fields are other instances: `DigitalInOut`, `PulseIn`, `I2CDevice`, `machine.Pin`
  wrappers).
- Builds on: [RFC 0001 -- ZCA with runtime state](0001-zca-runtime-state.md), which shipped
  Models A/B/C and the outlining default, but restricted Model A/B to classes whose fields are
  all scalars.

## 0. Decisions this RFC encodes

1. `@inline` on a method **forces** expansion at every call site, unconditionally.
2. Without `@inline`, a method is **compiled once and shared** unless the optimizer proves
   folding is free (exactly one instance, or the field is the same constant in every
   instance) -- then it folds and inlines, exactly as today.
3. `@outline` is **retired**: accepted with a warning and no effect during beta 2, removed at
   0.1.0.
4. Cycle-counted primitives (WS2812, DHT bit-bang, `pulse_in`) with a **non-constant pin are
   never refused**. The cost model combines two mechanisms: cloning by value (a primitive is
   compiled once per distinct constant pin, not once per call site -- a C++ template; phase
   1) and runtime-pin primitives written cycle-exact over a port pointer plus a mask (`ST Z`
   has fixed timing; the Arduino NeoPixel driver is the reference), chosen per call site by
   whether the pin argument is a compile-time constant there.

## 1. The measured problem

Six programs, compiled with the AVR backend on `main` (pymcuc @ `b61279e0`), instrumented by
annotating every emitted instruction with the source `file:line` of the IR node it lowers
from (a copied method body then appears at N distinct `file:line` spans, contiguous, so it is
countable exactly -- AVR opcodes are fixed-width, 2 or 4 bytes, no decoding ambiguity):

| program | declared flash | duplicated bytes | share | dominant cause |
|---|---:|---:|---:|---|
| `adafruit_hcsr04` (HCSR04 sonar, one instance) | 3330 | 310 | 9.3% | `self._trig`, `self._echo` are `DigitalInOut`/`PulseIn` instances |
| `adafruit_pcf8574` (I2C GPIO expander, one instance) | 888 | 320 | 36.0% | `self.i2c_device` is an `I2CDevice` instance |
| `dht.py` DHT11 (MicroPython style, one instance) | 986 | 438 | 44.4% | `self._pin` is a `machine.Pin` instance |
| NEC IR receiver (`pulseio`, scalar pin) | 690 | 14 | 2.0% | none (already outline-safe) |
| ZCA-outline DHT, 3 sensors, scalar `pin: uint8` field | 1240 | 8 | 0.6% | none (already outline-safe) |
| NeoPixel color chase (pin is a `const[str]`, folds away) | 712 | 0 | 0.0% | none (Model C, singleton) |

Source: `analyze.py` and `prog{1..6}.report.txt` in the inline-bloat measurement scratchpad
(2026-09-15, agent `inline-bloat-measure`), reproduced in full in this RFC's worktree history.

The first three programs are ordinary, single-instance drivers -- the shape essentially every
real sensor/actuator library takes. None of them constructs more than one instance of
anything; the duplication is not "N copies for N instances", it is "the same method body
repeated across its own call sites inside ONE object's lifetime", because the method is never
shared at all. The last three avoid it because their classes happen to hold only scalar
fields (a bare `uint8` pin number, or a `const[str]`), which is exactly the shape RFC 0001's
outlining already handles.

**The dominant rule is one paragraph in `IsOutlineSafe`** (`Scan.cs:2994-3127`):

```csharp
// A field whose type is not a scalar is another ZCA instance (e.g. a Pin
// stored as `self.pin`). An outlined body shares one copy across instances
// by passing each field as a runtime parameter, but a ZCA field is
// compile-time per-instance (a Pin is just its const pin name, no runtime
// value) -- it cannot be passed as a parameter. Such methods must stay
// force-inlined so `self.pin.<method>()` resolves at each call site.
var scalarTypes = new HashSet<string>
    { "uint8", "int8", "uint16", "int16", "uint32", "int32", "float", "bool" };
if (layout.Any(f => !scalarTypes.Contains(f.Type))) return false;
```

plus the matching guard inside the expression walk:

```csharp
// Nor can a field that holds another INSTANCE. ... An outlined body then received
// the field as a number and `self` did not exist in it at all ...
if (instanceFields != null && instanceFields.Contains(ma.Member)) safe = false;
```

The comment names the root cause precisely: **`Pin` has no runtime representation**. It is
"just its const pin name". Because of that, a field that is a `Pin` (or an `I2CDevice`, or a
`DigitalInOut`) cannot be passed as a parameter to a shared body, so `IsOutlineSafe` refuses,
and `Call.cs`'s force-inline fallback expands the method at every call site instead --
correctly, since it is the only mechanism that still works, but expensively, since "every
driver wraps its pin in an object" is not an edge case.

## 2. The C++/Rust/Zig principle, one paragraph

In those languages an object always has a representation -- its fields occupy memory or
registers whether or not the compiler can see through them -- and a method is an ordinary
function taking a receiver. Folding a method call to nothing, or a field to a constant, is
something the *optimizer* does after the fact, when it can prove the receiver is known
(`constexpr`, a zero-sized typestate, monomorphization over a small closed set of values); when
it cannot, the object lives in memory and one compiled function reads it. PyMCU's ZCA model
inverted this: representation was the exception (Model B, opt-in, gated behind `@outline` and
an escape check) and "no representation, force-inline" was the default. This RFC restores the
usual order: every instance gets a representation first; the optimizer's job is to prove that
representation is unnecessary and remove it, not to be the only thing standing between a driver
author and cubic code growth.

## 3. Memory representation

Every class gets a **flattened, fixed-size layout**, computed structurally (by walking
`__init__`), independent of which module defines it -- a field that is itself an instance
contributes its own flattened layout inline, not a boxed pointer, unless it is explicitly
shared/aliased (see 3.4). No layout depends on where the class or the field's class was
imported from; two classes with the same field shapes get the same stride.

### 3.1 `Pin`: 2 bytes (port address + mask)

On AVR, `PINx`, `DDRx` and `PORTx` are three consecutive registers in that fixed order at a
per-port base, and every one of them lives in the low 256 bytes of address space (even
memory-mapped: `PORTD` is `0x2B` on the ATmega328P). A `Pin` is therefore:

```
offset 0: port_addr   uint8   # PORTx's address; DDRx = port_addr-1, PINx = port_addr-2
offset 1: mask        uint8   # single bit set, e.g. 0x20 for bit 5
```

Two bytes, not three: `port_addr` is enough to derive all three registers by fixed offset, and
`port_addr` fits in a byte because AVR's I/O-mapped SRAM addresses for these registers never
exceed `0xFF`. Loading it into `Z` for an indirect access is `LDI ZL, port_addr` / `LDI ZH,
0x00` (or a slot load of the same 2 bytes) -- the high byte is always zero for these registers,
so nothing else needs to travel with the pin.

A **constant** `Pin` (the overwhelming common case: `Pin("PB5")` written as a literal) still
folds to nothing at the use site when the optimizer's rule (Section 5) fires -- this is not a
regression relative to today's `SBI`/`CBI` codegen for the singleton case. The 2-byte
representation only becomes real bytes in SRAM when a `Pin` is one field of an instance that
does not fold (Section 5's "shared" case).

### 3.2 `I2CDevice`: pointer to bus slot + 7-bit address

```
offset 0-1: bus_slot_ptr   uint16   # address of the I2C bus's own state (or 0 for the sole/default bus)
offset 2:   addr7          uint8    # 7-bit device address, top bit unused
```

3 bytes. The bus itself is a singleton per physical peripheral (`TWI0` on the ATmega328P), so
`bus_slot_ptr` is almost always a compile-time constant (there is one bus); it is carried as a
field rather than hardcoded so a board with two soft-I2C buses composes the same way.

### 3.3 A field that is another instance: inlined, not boxed

`DigitalInOut` wraps a `Pin` plus a one-bit direction flag:

```
offset 0-1: pin      Pin      # inlined, not a pointer
offset 2:   direction uint8   # 0=input, 1=output (packed into the pin's unused byte in a later pass, not in phase 3)
```

A `HCSR04` (RFC 0001's motivating example, and this RFC's `prog1`) becomes:

```
offset 0-1: _trig   DigitalInOut  (= Pin, 2B, direction folds to OUTPUT -- see 5.3)
offset 2-4: _echo   DigitalInOut  (3B: pin 2B + direction 1B, not foldable -- read AND written)
offset 5:   _timeout float scaled to a fixed-point byte, or 4B if kept as float (open question, 9.2)
```

Nesting composes by concatenation at a fixed offset; there is no indirection cost for a field
that is itself a ZCA instance, cross-module or not -- `adafruit_hcsr04.HCSR04` importing
`digitalio.DigitalInOut` from a different module gets the same flattened layout as if
`DigitalInOut` had been declared in the same file, because layout is a property of the class's
own field types, resolved transitively, never of the importing module.

### 3.4 When a pointer is used instead

A field is represented as a **pointer** to another instance's slot only when that instance is
independently constructed and can be shared/aliased (assigned to more than one owner, put in a
`Class[N]` array of its own, or is the bus in 3.2) -- the same escape analysis RFC 0001 already
computes decides this, unchanged. The default for a field built inline in `__init__`
(`self.pin = Pin(...)`) is inlined-by-value, matching value semantics for a field that nothing
else can alias.

## 4. Calling convention: self as a real pointer

A non-`@inline`, non-folded method takes **`self` as its first argument, in the existing
primary/return register pair `R24:R25`** -- unchanged from where RFC 0001's Model B already put
the slot pointer (`Scan.cs`'s F3-slot phase). This RFC does not introduce a new register
convention; it makes that convention the **default outcome of a class with a non-foldable
receiver**, rather than a rare opt-in gated behind `>= 2 fields` and an explicit `@outline`.

Two representations still coexist, chosen the same way RFC 0001 already chooses A vs B:

- **Non-escaping instance, whole layout fits in the remaining argument registers** (`R22`,
  `R20`, `R18`, ...): fields travel **by value**, spread across those registers -- Model A,
  unchanged from RFC 0001, generalized to accept a field whose type is itself a small
  flattened instance (a 2-byte `Pin` now consumes one 16-bit argument slot the same way a
  `uint16` local would).
- **Escaping instance, or a layout too wide for the remaining registers**: `self` is a pointer
  to a fixed SRAM slot, in `R24:R25`, and the method reads/writes fields with `LDD`/`STD` at
  their fixed offset -- Model B, unchanged in mechanism, generalized to be the fallback for
  ANY non-foldable class, not only ones a user explicitly opted into via `@outline`.

The one change to the ABI itself: **user args shift down by the width of `self`** only when
`self` is passed by value (Model A) with a wide layout; when `self` is a pointer (Model B) the
existing convention (`self` in `R24:R25`, first user arg in `R22`, ...) needs no change at all,
because that is already exactly what RFC 0001's F3-slot phase does today.

## 5. The optimizer's specialization rule

Stated so it is directly testable against the IR, per-class, per-call:

```
for each class C:
  is_folded(C) :=
        every live instance of C is provably the SAME instance (a single construction site,
        reachable from main with no loop or branch that could construct a second one)
     OR every field F used by the method being compiled has the SAME compile-time constant
        value in every instance of C
        (a field-by-field property: a class can have one folded field and one shared field --
         see Section 13.2, measured by the `zca-mixed-fold-and-share` fixture)

for each call site `inst.method(args)` where method is NOT @inline:
  if is_folded(C) for the fields `method` touches:
        Model C: inline `method`'s body at the call site, with `self.<field>` replaced by
        the constant. Zero SRAM, zero call overhead -- identical to today's ZCA behavior.
  elif C is used with more than one DISTINCT constant value of some field across the program,
       AND method's cost is dominated by that field being a compile-time constant used in a
       position an instruction encodes directly (SBI/CBI/SBIS/SBIC's bit index, or a `.org`/
       `.equ` displacement) -- i.e. method is a "cycle-counted primitive" per decision 4:
        clone by value: compile ONE copy of `method` per DISTINCT constant value, memoized
        (a C++-template instantiation cache keyed on the constant), shared across every call
        site that uses that same value.
  else:
        Model A or B (self by value or by pointer, chosen by escape analysis, Section 4):
        compile ONE copy of `method`, shared across every instance and every call site,
        taking self's fields as real runtime values.
```

This is a strict generalization of RFC 0001's existing rule
(`if some method called on I is @inline -> Model C; if I escapes -> Model B; else -> Model A`),
with two additions: the single-instance fold is now explicit and separate from the `@inline`
fold (today's `@inline` fold on a singleton and the NEW single-instance-without-`@inline` fold
produce identical code, but only the first is spelled by the user), and the clone-by-value
branch is new (Section 6, decision 4).

`IsOutlineSafe`'s scalar-only restriction (Section 1) is deleted outright: a field that is
another instance is no longer disqualifying, because Section 3 gives it a representation a
shared body CAN receive (by value if it fits, by nested pointer if it does not).

## 6. `@inline` semantics and `@outline` retirement

`@inline` keeps its current meaning and gains the force it was missing: a method decorated
`@inline` is **always** expanded at its call site, full stop -- no analysis, no folding
question, exactly like a C `static inline` with `always_inline`. This is unconditional
regardless of instance count, which is what makes it the escape hatch for the rare method the
conservative analysis in Section 5 cannot see through (control flow the walker refuses,
virtual dispatch through an overridden sibling -- the same cases RFC 0001's `IsOutlineSafe`
comment already lists as `default: safe = false`).

`@outline` is retired in two steps:

- **Beta 2**: `@outline` is parsed, accepted, and does nothing -- a compiler warning at the
  decorator site: `@outline has no effect; outlining is now the default for every method
  without @inline. This decorator will be removed at 0.1.0.` A program that used `@outline` to
  force sharing keeps compiling, byte-identical, because sharing is now automatic wherever
  `@outline` used to force it (Section 5's Model A/B branch covers every case the old
  scalar-only outlining did, plus the ones it refused).
- **0.1.0**: the decorator is removed from the parser; using it becomes a syntax error naming
  the removal.

## 7. Cost model, measured

Four tiny AVR programs, isolating the mechanism (not the compiler's actual output -- these are
hand-written GAS snippets assembled with `avr-as`/`avr-ld`/`avr-objcopy` for the byte count, and
run under avr8sharp -- the same cycle-accurate classic-core AVR simulator the project already
uses for driver timing -- for the cycle count):

| pattern | bytes | cycles | represents |
|---|---:|---:|---|
| constant `SBI` (+ 1 `NOP` to give the sequence a fixed end) | 4 | 3 | Model C: folded, one instance, cost as today |
| runtime `LD`/`OR`/`ST` with the port address already in `Z`, mask in a register | 8 | 6 | Model A: shared body, non-constant pin, no ISR sharing |
| self-pointer in `Y`: load port lo/hi + mask from the slot (3x `LDD`), then `LD`/`OR`/`ST` | 14 | 12 | Model B: escaping instance, fields read from its slot |
| same as above, wrapped in `IN SREG` / `CLI` ... `OUT SREG` (save-and-restore, not blind `SEI`) | 20 | 15 | Model B on a port an ISR also touches (Section 8) |

(`NOP` costs 2 bytes / 1 cycle and is included in every row to anchor the count at a real
instruction boundary; subtract 2 bytes / 1 cycle from each row for the bare mechanism.)

Reading the table: going from a folded singleton to a shared runtime-pin body costs **+4 bytes,
+3 cycles** per toggle (Model A). Making that instance escape into a slot adds another **+6
bytes, +6 cycles** for the three field loads (Model B). Sharing the port with an ISR adds a
further **+6 bytes, +3 cycles** for the save/restore of `SREG`. None of this is paid by a
folded singleton (the common case, unchanged), and all of it is paid at most once per toggle,
not once per call site the way today's force-inline pays it.

This replaces the RFC's earlier, unmeasured estimate (`SBI 2B/2cyc vs LD/OR/ST 6B/5cyc`,
recorded 2026-09-15 before this measurement) with the numbers above.

## 8. Atomicity for a port shared with an ISR

A constant-pin `SBI`/`CBI` is a single instruction: inherently atomic, needs nothing extra,
exactly as today. A **runtime**-pin read-modify-write (`LD`/`OR`/`ST` through `Z`) is not
atomic -- if an ISR touches the same port between the `LD` and the `ST`, its write is lost. The
rule: wrap the sequence in a save-and-restore of the `I` bit (`IN` from `SREG`, `CLI`, ...,
`OUT` back to `SREG` -- not a blind `SEI`, which would wrongly re-enable interrupts if they were
already off, e.g. inside another critical section or inside the ISR itself) **exactly when**
the specific port address is one the compiler's existing ISR-shared-volatile analysis (already
used to decide which globals move to `volatile`/`GPIOR0-2`, per the ISR-shared-volatile-gpior
design) has marked as touched by a live interrupt handler. A runtime-pin write to a port no ISR
touches pays only the Model A/B cost from Section 7, never the atomicity tax.

## 9. SRAM accounting and `pymcu build` reporting

### 9.1 Per-instance cost

An instance's SRAM cost is the flattened size from Section 3: sum of each field's
representation width, recursively, with no per-instance overhead (no vtable pointer, no
refcount -- inheritance is already fully devirtualized in this codebase, so a subclass instance
costs exactly its own flattened fields plus its base's, nothing more). A folded (Model C)
instance costs zero SRAM, as today. A Model A instance costs zero SRAM (its fields live in
registers/stack at the call, never resident). Only a Model B (escaping) instance occupies a
static slot -- one instance, one fixed address, no heap, exactly RFC 0001's existing
`instanceSlots`.

### 9.2 A real gap in today's build report

**Today `pymcu build` reports Flash only.** `src/driver/commands/build.py`'s
`_flash_report_lines` prints `Flash: N / total bytes (...)` and the vector-table breakdown;
there is no SRAM line anywhere in the CLI output (`grep -n "SRAM" src/driver/commands/build.py`
returns nothing). Since this RFC makes Model B slots the default outcome for any driver whose
fields are not provably singleton, SRAM stops being a rounding error most users never see and
becomes something `pymcu build` must report. Phase 3 must add a line in the same format as the
Flash report:

```
SRAM:  <static bytes> / <chip total> bytes (<pct>% of static RAM)
       <N> bytes across <M> instances + <K> bytes of other globals/stack reserve
```

sourced from the backend's own slot list (it already emits one `.equ`/`.byte` per slot; the
report only has to sum widths it is not summing today).

## 10. Diagnostics

- **`@outline` used** (beta 2): warning, exact text in Section 6, points at the decorator.
- **`@outline` used** (0.1.0): compile error, "the @outline decorator was removed in 0.1.0;
  outlining is now automatic for every method without @inline", naming the line.
- **A cycle-counted primitive called with a non-constant pin**: informational note (not a
  warning -- decision 4 says this must never be refused), e.g. `note: WS2812.show() is called
  here with a pin that is not a compile-time constant; this call compiles to the runtime
  cycle-exact path (Section 7), not the folded SBI path`. Silenceable; exists so a user
  surprised by a few extra bytes on one call site among many folded ones has a place to look.
- **A method the conservative walker cannot see through** (unchanged from RFC 0001's existing
  fallback, still needed for the `@inline`-required cases in Section 6): the force-inline
  fallback fires silently today; this RFC adds a `--explain` line naming which methods took
  that path and why (bare `self`, a call through an overridden sibling, unsupported control
  flow), so a user who wants sharing back has a concrete reason instead of only a byte-count
  surprise.
- **SRAM over capacity**: same shape as `_check_flash_capacity` (`build.py:608`), now checking
  the new SRAM total against the chip's declared SRAM size -- a real gap today, since nothing
  currently rejects an SRAM-overflowing build.

## 11. Phases

Each phase's effect on the six programs from Section 1 is an **estimate**, not a measurement --
no compiler changes exist yet. It is derived from the duplicated-byte figures already measured
(Section 1), assuming a shared `CALL`/`RCALL` (2-4 bytes) replaces each eliminated duplicate
copy.

1. **Cloning by value, with the size gate wired up.** Implements decision 4's first
   mechanism and Section 11's own gate (below) as CI infrastructure, using
   `0006-baseline-2026-09-15.json` as the pre-image. No change to `IsOutlineSafe` yet, so
   Section 1's six programs are estimated **unchanged (+/-0 bytes each)**: none of them
   constructs multiple instances of the same class with distinct constant fields today.
   This phase also lands `zca-mixed-fold-and-share` (Section 13.2) as a corpus fixture, now
   committed in `pymcu-avr` with its NUnit coverage -- two `SoftUart` instances sharing one
   outlined body today at **1032 bytes** (602 B was the same program before it printed
   anything; the fixture prints the five values a shared, unfolded body actually produces,
   verified to match CPython running the identical class, so a wrong fold shows up as a wrong
   number, not only a byte-count change), with `baud` passed as a redundant runtime parameter
   despite being `9600` in both -- so the per-field fold's effect (baud disappearing from the
   parameter list, and `1000000 // self.baud` folding to the constant `104` and dropping its
   `__div32` call entirely) is measured
   against this number in Phase 3, not reasoned about after the fact.
2. **`Pin` representation (Section 3.1) and runtime-pin primitives (Section 7/8).** Estimated
   **~0 bytes** on the six programs specifically, because `prog4` (NEC) and `prog6`
   (NeoPixel) already avoid a `Pin`-typed field (a bare pin string/scalar is folded upstream
   in the current HAL); this phase's payoff is decision 4's *guarantee* (a variable pin is
   never refused), not a size change on this corpus. New fixtures exercising a genuinely
   runtime pin are needed to measure it (not present in today's baseline).
3. **Instance fields as slot pointers/flattened layout (Section 3.3), `IsOutlineSafe`'s
   scalar-only check deleted.** This is the phase that fixes Section 1's actual finding.
   Estimated, at roughly 80-90% of the measured duplicate bytes recovered (some duplicate
   bytes become a `CALL`/`RCALL`, not zero):
   - `adafruit_hcsr04`: 3330 -> ~3070 B (-260)
   - `adafruit_pcf8574`: 888 -> ~610 B (-280)
   - `dht.py` (MicroPython): 986 -> ~610 B (-375)
   - the other three: unchanged (already at or near zero duplication).
   These six numbers are estimates derived at 80-90% of the measured duplicate bytes from
   Section 1, nothing more; Phase 3 replaces every one of them with a real measurement off
   the rebuilt `pymcuc`, and that measurement -- not this estimate -- is what the Phase 5
   fixture bounds are written from. `zca-mixed-fold-and-share`'s per-field fold (1032 B
   today) is measured the same way, in the same phase.
4. **Retire `IsOutlineSafe`'s force-inline fallback for every case Section 5 now covers**,
   leaving `@inline` as the only forced-expansion path (Section 6). Estimated **+/-0** on the
   six programs (phase 3 already produced their steady-state code); this phase is cleanup and
   closes the "decorator means nothing" gap RFC 0001 first identified.
5. **Land the six programs as AVR integration-suite size fixtures with explicit byte
   bounds**, using the post-phase-3 numbers as the checked-in expectation (test-only, 0 bytes
   of firmware change).

## 12. The gate

**The gate is `0006-baseline-2026-09-15.json` itself.** Every project in it whose classes have
a single instance must be **byte-identical**, hex-for-hex, before and after every phase --
Section 5's fold rule explicitly preserves today's single-instance behavior, so any diff on
such a program is a regression, not an improvement, and must block the phase. This is not
narrowed to a hand-picked subset: it is every single-instance-per-class entry in the baseline
file, checked mechanically against the rebuilt `pymcuc`'s output.

Two blink programs are pinned by name in that set, so "the blink gate" stops being ambiguous:

- `examples/blink` (HAL-native, `Pin("PB5")`, `delay_ms`): **150 bytes**
  (`Flash: 150 / 32768`, measured 2026-09-15, `pymcu-avr @ 26b58ba9`).
- `compat-mp-blink-toggle` (MicroPython layer, `machine.Pin(13, Pin.OUT)`, `.toggle()`,
  `time.sleep_ms(500)`): **142 bytes**. This is the website's own canonical blink -- the
  exact source in `~/Repos/website-copy/src/components/widgets/Playground.astro` (also
  quoted in `FirmwareSizes.astro`'s "Why 142 bytes?" copy) -- landed as a fixture in
  `pymcu-avr` (`CompatMpBlinkToggleTests`: flash size plus PORTB5 toggling every 500 ms in
  avr8sharp) specifically so this RFC's earlier, unmeasured "142 B" reference resolves to a
  real, tested corpus entry instead of a number that appeared in a design note with no
  fixture behind it.

Both numbers are single-instance, single-`Pin` programs, so both belong to the byte-identical
set above; neither is a target the implementation is free to change.

Full corpus: 414 projects (355 fixtures + 59 examples) in `pymcu-avr` (352 built 2026-09-15
through the real `pymcu build` CLI against `pymcuc @ b61279e0`, at `pymcu-avr @ 26b58ba9`;
`compat-mp-blink-toggle` and `zca-mixed-fold-and-share` added and committed the same day after
review, with NUnit coverage, at `pymcu-avr @ 970e8c8`) built clean, hex byte count read as the
sum of Intel HEX data-record byte counts. See `0006-baseline-2026-09-15.json` for the full
per-program table.

## 13. Open questions

1. **Mixed folded/shared fields on one class -- now measured, not just described.** Section
   5's rule allows a class to have one field folded (constant in every instance) and another
   shared (varies) -- e.g. a UART whose baud rate never changes but whose pin does. The
   `zca-mixed-fold-and-share` fixture (Section 11, Phase 1) is exactly this: two `SoftUart`
   instances, `pin` different (2 and 5), `baud` the same (`9600`) in both. Built today it
   already outlines to one shared body (RFC 0001's existing scalar-only rule already permits
   it, since both fields are scalars) at **1032 bytes** (verified in `pymcu-avr`'s
   `ZcaMixedFoldAndShareTests`, printing the same five values CPython prints for the
   identical class), with `baud` carried as a redundant runtime parameter and
   `1000000 // self.baud` compiled as a genuine runtime `__div32` call despite the divisor
   never varying. What is still open is the slot-layout question this
   number cannot answer by itself: once `baud` folds out of the parameter list per Section 5,
   does a Model B version of the same class (one that escapes) still reserve a byte for
   `baud` in its slot, or is the slot narrower than the class's full field list because a
   folded field never needed storage in the first place? Phase 3 must answer this with the
   rebuilt compiler's actual slot layout, not by further reasoning about it here.
2. **`_timeout: float` in `HCSR04` (Section 3.3).** Whether a `float` field that is read but
   never fractionally meaningful across the program's actual constant call sites should be
   representable as a scaled fixed-point byte in the slot, or must always cost the full 4
   bytes, is left open; the RFC's byte estimates in Section 11 assume the conservative 4-byte
   answer, but a scaled representation would change Phase 3's `adafruit_hcsr04` number.

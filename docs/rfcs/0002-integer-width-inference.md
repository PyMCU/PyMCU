# RFC 0002 — Integer width inference over a constant trip count

- Status: **PROPOSED.** Not implemented.
- Issue: [#364](https://github.com/PyMCU/PyMCU/issues/364)
- Date: 2026-09-14
- Affects: `src/compiler/IR/IRGenerator/Assign.cs` (the local store), `Statements.cs`
  (`CollectLiteralOnlyLocalWidths`, `WidthOf`), `Expr.cs` (`VisitBinary` promotion),
  `IR/Optimizer.cs` (`UnifyVariableWidths`), and every program that writes an unannotated
  integer.

---

## 1. Problem

PyMCU decides the width of an unannotated integer from its **first store** and never revisits
it. A loop whose trip count is a constant is where that assumption breaks, because the value
grows by a byte per iteration and the first store is the one that knows least about it.

Measured on atmega328p in avr8sharp against CPython running the same file, with
`buf = bytearray([0x00, 0x12, 0x34, 0x56])`:

| program | CPython | atmega328p |
|---|---|---|
| module level, `reg = 1`, 3 iterations of `reg = (reg << 8) \| buf[i]` | 22426642 | **18** |
| the same inside a `def` | 22426642 | **16777216** |
| 2 iterations, then `reg -= 2 * sign_bit`, `sign_bit = 1 << 15` | -25345 | **0** |

Three different wrong answers, all plausible, none reported. `18` is the last byte on its own.
`16777216` is the seed shifted into place with every OR dropped. `0` is a sign conversion done
at a width that cannot hold the sign.

This is the decode loop at the centre of every register driver. `adafruit_register`'s
`i2c_bits.RWBits.__get__` is exactly it, so a sensor reads a number that is not the number on
the wire, and nothing in the build says so.

### 1.1 Why the current model produces it

Three mechanisms decide a width and not one of them reads a loop.

**The local store is first-write-wins.** `Assign.cs` takes the type from the first RHS and every
later store to that name reuses it, truncating. The widening hook exists only on the
module-global path, and it fires once: a global widened from `uint8` to `uint16` on iteration
one is then truncated on iteration two.

**Promotion covers four operators, not six.** `+ - * <<` promote by value range; `| & ^` do not.
So `(reg << 8)` correctly produces a wider temporary and `| buf[i]` hands it to a store that
narrows it back. The widening and the truncation are two lines apart.

**The one cross-site join needs a pre-existing disagreement.** `UnifyVariableWidths` only acts on
a name that already reaches it at two different widths. A name that is uniformly too narrow --
the seeded-accumulator case -- is never repaired, and the pass ignores temporaries, so the
`Binary` instructions that consumed a widened variable keep their stale narrow destination.

### 1.2 What must land first

[#359](https://github.com/PyMCU/PyMCU/issues/359) makes the **first** iteration of this
accumulator lose its store outright: `reg = 0` followed by `reg = (reg << 8) | buf[i]` folds
through the identity `0 | x == x`, retargets the load straight at `reg`, and never records that
`reg` has stopped being a compile-time constant. Until that is fixed, no width measured on this
program can be believed, because the value under measurement is not the value the program
computed. **#359 is a prerequisite of this RFC, not part of it.**

---

## 2. The rule

> When a loop's trip count is a constant, the width of an unannotated integer assigned in its
> body is the narrowest width that holds every value the loop can produce, computed over the
> whole loop rather than from the first store.

Stated as a procedure, for an unannotated integer local or module global `v`:

1. Collect every assignment to `v` in the function, **including those inside loops whose trip
   count folds**. A loop whose trip count does not fold contributes nothing and takes `v` out of
   scope of this rule (§5).
2. Give each assignment a value range. A loop body is evaluated once per iteration with `v`
   carrying the range it had at the end of the previous iteration, to a fixed bound (§2.1).
3. `v`'s width is `NarrowestTypeFor(min, max)` over the union of those ranges — the same
   primitive the compiler already uses, applied to a range that now includes the loop.
4. If the range cannot be computed, `v` is refused by name (§5), never silently widened to
   `int32` and never silently narrowed to `uint8`.

Signedness falls out of the same range: a union whose minimum is negative is signed. `reg -= 2 *
sign_bit` with a constant `sign_bit` is an ordinary subtraction whose result range is negative,
so the name becomes signed without a special case for the idiom.

### 2.1 Termination

Step 2 is a fixpoint over an interval lattice, and intervals do not terminate on their own. Two
bounds, both of which are chosen so the rule stays predictable rather than clever:

- **The trip count is the bound.** A constant trip count means the body is evaluated exactly
  that many times; there is no widening operator and no fixpoint to diverge. A trip count above
  `WidthInferenceTripLimit` (proposed: 64) is treated as not folding, and `v` falls back to §5.
- **The width lattice is the ceiling.** Once a range exceeds `int32`, inference stops and the
  program is refused (§5). PyMCU has no 64-bit integer, so a program that needs one must be told
  so rather than given a truncated one.

Nested loops multiply trip counts and are subject to the same limit.

---

## 3. What folds

Unchanged. This RFC widens the range of what the compiler *knows*; it does not make anything
stop being a constant.

- A value whose range collapses to a single number is still a `Constant`, and the assignment
  still folds away. An accumulator over a loop whose every input is a literal is computed at
  compile time and costs nothing, as today.
- `range()` bounds, `len()` of a fixed buffer, `struct.calcsize()`, and `const()` values continue
  to fold before the rule is applied. The trip count in step 1 is read after that folding, so
  `range(self.register_width, 0, -1)` with `register_width` a field holding a literal counts as
  a constant trip count.
- A loop that the IR generator unrolls (trip count ≤ 8) is unaffected in kind: the rule gives the
  same width the unrolled body already produces, which is how the rule is checked against itself.

---

## 4. What widens

Only an **unannotated** integer, and only where a range demands it.

| program | today | under this RFC |
|---|---|---|
| `reg = 0`, 2 iterations of `(reg << 8) \| byte` | `int32` in a `def` | `uint16` |
| `reg = 0`, 3 iterations of `(reg << 8) \| byte` | truncated | `uint32` |
| `reg = 0`, 1 iteration | `int32` in a `def` | `uint8` |
| `reg = <byte>`, 2 iterations | `uint8`, truncated | `uint16` |
| `reg -= 2 * sign_bit`, `sign_bit = 1 << 15` | unsigned, wrong | `int32` |
| `n = 200` (no loop) | `uint8` | `uint8` |

Two consequences worth stating plainly:

- **The rule narrows as often as it widens.** The `int32` fallback is today's answer whenever the
  compiler cannot see every assignment to a name, and a two-byte accumulator in a function is
  exactly that case: correct, and 4 bytes wide on an 8-bit part with the 32-bit decimal writer
  behind it. Replacing it with `uint16` is a size win, not a cost.
- **An annotation always wins.** `reg: uint16 = 0` is the author saying what they want, and this
  rule does not override it. A value that overflows an annotation is the existing overflow
  diagnostic, unchanged.

`| & ^` join the promoting operators, because a rule that widens a store and leaves the
expression feeding it narrow has not fixed anything.

---

## 5. What is refused

A width that cannot be decided is a refusal, located, naming the variable and the width it would
need. It is never a silent `uint8` and never a silent `int32`.

1. **A loop whose trip count is not a constant**, feeding an unannotated accumulator whose range
   therefore has no bound:

   > `'reg' has no width the compiler can choose: it grows by up to 8 bits per iteration of the
   > loop on line N, whose trip count is only known at run time. Annotate it (`reg: uint32 = 0`)
   > to say how wide the register is.`

2. **A range that exceeds `int32`**:

   > `'reg' would need 40 bits: 5 iterations of the loop on line N each shift it 8 bits left.
   > PyMCU's widest integer is 32 bits. Read the register in two halves, or narrow it inside the
   > loop.`

3. **A trip count above the limit** is case 1, with the limit named.

Each refusal names the variable, the loop, and the width, because the reader's next action is to
write an annotation and they need to know which one.

---

## 6. The zero-cost gate

The rule changes how every unannotated integer in the language is sized. It ships only if it is
free for programs that do not hit it, and that is measured, not argued.

**Gate 1 — the fixtures are byte-identical.** The AVR corpus at
`~/Repos/pymcu-avr/tests/integration/fixtures/` and `~/Repos/pymcu-avr/examples/` (300+ programs
on atmega328p) is built twice, at the commit before and the commit after, and the `.hex` of every
program that does not contain a constant-trip-count accumulator must be **identical byte for
byte**. Not "same size": identical. A program whose bytes change is listed by name with the
reason, and a reason that is not "this program hits the rule" is a defect in the rule.

**Gate 2 — ROM does not grow.** For the programs that do change, total flash must not increase.
The expectation is a decrease, because the `int32` fallback is what the rule mostly replaces.
Measured with `tests/tools/rom_snapshot.py check` and, for the changed fixtures, the byte count
of the Intel HEX.

**Gate 3 — CPython is the oracle.** Every program in the width corpus is run under CPython and on
a simulated Uno, and the UART output must match. A width is right when the number that comes out
of the device is the number CPython printed; nothing else counts as evidence. The three rows in
§1 are the first three entries of that corpus and must go from DIFF to ok.

**Gate 4 — both front ends agree.** Every diagnostic in §5 and every firmware produced under the
rule must be identical under `PYMCU_PY_PARSER=1` and under the default C# parser.

Behind the gate, the rule ships under `PYMCU_NO_WIDTH_INFERENCE=1` for one release, following the
`PYMCU_NO_OPT` / `PYMCU_NO_PEEPHOLE` pattern, so a miscompile can be bisected to the rule rather
than argued about. The flag is read in `Pipeline/Phases/IrGenerationPhase.cs` next to the existing
two, and the differential harness drives it as a fourth axis.

---

## 7. Implementation sketch

Four changes, in this order, each with its own regression test verified red beforehand.

1. **Land [#359](https://github.com/PyMCU/PyMCU/issues/359).** An identity-folded assignment must
   clear the target's constant tracking. Without it the accumulator under measurement is not the
   accumulator the program wrote.
2. **A per-name value range that survives a statement.** Today a range lives on a temporary
   inside one expression. The rule needs `Variable` to carry one across statements, which is a
   dictionary keyed by qualified name alongside `variableTypes`.
3. **Evaluate a constant-trip loop body against that map.** Where the IR generator already
   computes `RangeTripCount`, run the body's assignments once per iteration for their ranges
   only, no IR emitted, and join.
4. **Replace the first-store decision with the join,** for unannotated names only, and extend
   promotion to `| & ^`. `UnifyVariableWidths` stays as the backstop for names this rule does not
   reach.

---

## 8. Alternatives rejected

**Make everything `int32`.** Correct and unaffordable: 4 bytes per value on a part with 2 KB of
SRAM, and it pulls in the 32-bit decimal writer — 756 bytes of flash, 37% of an ATtiny2313 —
for a choice the author did not make. This is already the fallback and already the complaint.

**Require an annotation on any accumulator.** Honest, and it breaks the premise of the whole
Adafruit campaign: the libraries are compiled **unmodified**, and `adafruit_register` has no
annotation to add. A rule that only works on code we are allowed to edit does not solve this.

**Infer from the format string / register width alone.** Narrow enough to look attractive and
wrong in kind: it special-cases one library's idiom instead of naming the property that makes the
idiom safe, which is the constant trip count. The next library spells it differently.

**A general abstract interpretation over the whole function.** More than this needs, and it
terminates only with a widening operator whose behaviour is hard to explain in a refusal message.
The constant trip count bounds the problem exactly, which is why it is the rule.

---

## 9. Open questions

1. **`WidthInferenceTripLimit`.** Proposed 64. 8 aligns with the existing unroll limit but refuses
   a 16-iteration decode that is perfectly bounded; 64 costs compile time on nested loops.
2. **A module-level accumulator** has a second, older width path (`ScanGlobals` +
   `NarrowLiteralOnlyGlobals`) whose `WidthOf` returns `UNKNOWN` for any subscript. Does the rule
   replace that path or run beside it? Replacing it is cleaner and touches more.
3. **A field accumulated across method calls** (`self.reg = (self.reg << 8) | b` in a method called
   N times from a loop) has a constant trip count that is not in the same function. Out of scope
   for this RFC; it should refuse with §5 case 1 rather than truncate.
4. **The gate flag's lifetime.** One release is proposed. It is only worth keeping while the
   differential harness runs it as an axis.

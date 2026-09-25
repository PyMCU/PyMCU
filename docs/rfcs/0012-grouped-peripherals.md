# RFC 0012: grouped peripherals (a stable register surface)

- Status: **IMPLEMENTED** for Timer/Counter1 on the ATmega328P; the generalisation to every
  peripheral of every chip is planned here and not done in this branch.
- Date: 2026-09-25
- Affects: `src/compiler/IR/IRGenerator/Scan.cs` (class-level register declarations),
  `src/compiler/IR/IRGenerator/Assign.cs` (the class-dict accumulator's guard),
  `src/compiler/IR/IRGenerator/ControlFlow.cs` (a class constant as a bit index),
  `lib/src/pymcu/chips/atmega328p.py` (the `TIMER1` group),
  `pymcu-avr`: `tests/integration/fixtures/grouped-peripheral-timer1{,-loose}`,
  `tests/integration/Tests/AVR/GroupedPeripheralTimer1Tests.cs`,
  `tests/oracle/probes/277_grouped_peripheral_registers.py`.
- Builds on: the `ptr[T]` register declaration (`lib/src/pymcu/types.py`) and the MMIO
  lowering that turns `.value` and `[bit]` into `LDS`/`STS`/`IN`/`OUT`/`SBI`/`CBI`.

## 0. The problem this solves

A chip definition exposes every special function register as a loose module-level name:

```python
TCCR1A:  ptr[uint8] = ptr(0x80)
TCCR1B:  ptr[uint8] = ptr(0x81)
```

Those names are how the HAL reaches the silicon. The set, the spelling and the widths
follow whatever the HAL needs and change with it, which is exactly why reaching for the
native HAL raw is discouraged: it can change from one day to the next. A program written
against `TCCR1B` is written against a surface the project does not promise.

That is a stability problem, not a syntax problem. The answer is a named object the project
CAN promise and keep still while the definitions underneath it move.

## 1. Decisions this RFC encodes

1. **A group is a class, and the class is the surface.** One peripheral, one class named
   after it, its registers as class attributes:

   ```python
   from pymcu.chips.atmega328p import TIMER1

   TIMER1.TCCR1A.value = 0x82
   TIMER1.ICR1.value = 19999
   TIMER1.TCCR1B[TIMER1.CS10] = 1
   ```

   Evaluated against two alternatives and chosen on evidence:

   - *A ZCA instance with one field per register* would make a group a value: it could be
     passed, returned, stored in a field. Every one of those is a cost (`[[outline-cannot-carry-instances]]`,
     `[[inline-bloat-self-as-this-2026-09-15]]`), and none of them is a thing to do with a
     peripheral, which is a singleton bolted to the silicon. It also needs a constructor,
     so a program that merely reads a flag pays for one.
   - *A module-level singleton per peripheral* is the same object with an extra import
     level and no place to hang the bit positions.
   - *A class with class-level constants* has no runtime existence at all: measured
     byte-identical firmware, no constructor, no instance, nothing to pass by accident.

2. **The register names are the datasheet's.** `TIMER1.TCCR1A`, not `TIMER1.TCCRA` and not
   `TIMER1.control_a`. Two reasons, and the second is the decisive one: a name that matches
   the datasheet is greppable against the datasheet, and it makes the grouped form a pure
   re-grouping of the loose list, which a generator can do with no per-chip mapping table.

3. **Bit positions live in the same class.** `TIMER1.CS10`, `TIMER1.TOV1`. One import brings
   the whole peripheral, and no bit name reaches module scope. This is the answer to XC8's
   `T1CONbits.TMR1ON` at zero cost: `TIMER1.TCCR1B[TIMER1.CS10] = 1` lowers to the same
   `SBI` the literal bit index lowers to. A `TCCR1Bbits.CS10 = 1` accessor would read better
   still, and it is NOT proposed here: it needs a two-level member access to lower to a
   single bit operation, which is new machinery, for a spelling that saves one subscript.

4. **Per instance, not per kind.** `TIMER0`, `TIMER1`, `TIMER2` are three classes, because on
   the ATmega328P they are three different register sets at three different widths. A
   `Timer(n)` abstraction over them is the HAL's job (`pymcu.hal.avr.timer`), and it already
   exists; this layer is the registers, not a driver.

5. **The addresses ARE the loose names, not copies of them.**

   ```python
   class TIMER1:
       TCCR1A: ptr[uint8] = ptr(TCCR1A)
   ```

   One copy of every address in the file, so the grouped surface cannot drift from the loose
   one. This is what makes decision 2 pay: the group is mechanically derivable and
   mechanically checkable.

6. **The loose names stay, and they stay undocumented.** They are not deprecated with a
   diagnostic: the HAL itself uses them, on every chip, so a diagnostic would fire on our own
   stdlib on every build. The contract is stated instead, in the chip file and in the
   language docs: **the grouped classes are the surface the project keeps stable; the
   module-level register names are an implementation detail of the HAL and may change in any
   release.**

## 2. The bar, and how it was measured

A program rewritten from loose names to the grouped form must produce **byte-identical
firmware**. Not "about the same size": the same bytes.

Measured three ways, all green:

- A probe covering the whole access surface in one program (8-bit and 16-bit `.value` read
  and write, byte halves of a 16-bit pair, read-modify-write, augmented assignment, constant
  bit set and clear, a runtime bit VALUE, a runtime bit INDEX, the bit read in a condition
  and in an expression, `ptr(BASE + 1)` address arithmetic, the register touched from an
  `@inline` helper and from an ISR): 416 bytes both ways, identical `.hex`.
- `tests/integration/fixtures/grouped-peripheral-timer1` against its `-loose` twin: 244 bytes
  both ways, identical `.hex`, asserted inside the AVR suite so it cannot rot.
- `tests/unit/IR/GroupedPeripheralRegisterTests`: the two spellings compared instruction by
  instruction in the IR.

## 3. What had to change in the compiler

Three holes, each of which swallowed a program in silence. None of them was a syntax gap;
all three let the program compile and do the wrong thing.

1. **A class-level `ptr` declaration was folded to a plain integer.** It lost the width, and
   the write side then met a `Constant` target it refuses outright. Module level has
   recognised this declaration since the first chip definition; a class body is the same
   declaration one scope deeper.

2. **The compile-time class-dict accumulator claimed every `Class.attr[k] = v`.** The
   receiver naming a class was the whole test, so `TIMER1.TIFR1[TIMER1.TOV1] = 1` built a
   phantom dict entry and emitted nothing. The same hole swallowed `Store.buf[0] = 5` on a
   class-level `bytearray`, which is a pre-existing silent-wrongcode bug of its own: the
   matching read folded to the value just "written" while the array in SRAM kept its zeros,
   so the program agreed with itself at that one subscript and read zeros from a loop, a
   runtime index or another function.

3. **A class constant as a bit index missed the direct bit test.** `if TIFR1[TIMER1.TOV1]:`
   materialized the bit into a register and compared it, ten bytes where `SBIS` answers in
   one, while the same condition written `if TIFR1[0]:` was free.

An address the scan cannot resolve is now a located error naming the attribute, instead of
the group being filed as a dead SRAM variable.

## 4. Generalising

The grouped form is mechanically derivable from the loose list: a group is a name, a set of
register names, and the bit positions of those registers. The chip files are hand-written
today, and there is no generator; the input a generator needs already exists on this machine
in the avr-libc headers that the `avr-wasi` toolchain wheel ships
(`avr/include/avr/iom328p.h` and siblings), where each register is a
`_SFR_MEM8/_SFR_MEM16/_SFR_IO8` macro with its address and each bit is a `#define` of its
position, grouped by the register it belongs to.

So the plan is a generator with a per-chip **grouping table** as its only hand-written input
(peripheral name -> the register names it owns), plus a cross-check pass that compares every
address and width in the chip file against the vendor header. That cross-check is worth
building on its own: run over the AVR chip files today it reports **zero** discrepancies on
`atmega328p`, `atmega328`, `atmega168p`, `atmega88p` and one on `atmega48p`, and a wall of
them on the ATtiny parts (see the findings in the campaign report). The generator is not
written in this branch.

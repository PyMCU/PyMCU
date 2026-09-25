# RFC 0012: grouped peripherals (a stable register surface)

- Status: **IMPLEMENTED** for Timer/Counter1 on the ATmega328P; the generalisation to every
  peripheral of every chip is planned here and not done in this branch.
- Date: 2026-09-25
- Affects: `src/compiler/IR/IRGenerator/Scan.cs` (class-level register declarations),
  `src/compiler/IR/IRGenerator/Assign.cs` (the class-dict accumulator's guard),
  `src/compiler/IR/IRGenerator/ControlFlow.cs` (a class constant as a bit index),
  `lib/src/pymcu/chips/atmega328p.py` (the `Timer1` group),
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
   from pymcu.chips.atmega328p import Timer1

   Timer1.TCCR1A.value = 0x82
   Timer1.ICR1.value = 19999
   Timer1.TCCR1B[Timer1.CS10] = 1
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

2. **The naming follows PEP 8, and this is settled.** The group is a class, and PEP 8 says
   "Class names should normally use the CapWords convention", so it is `Timer1`, `Timer0`,
   `Timer2`, and CapWords for every group the generalisation adds later. The ALL_CAPS
   convention PEP 8 gives is for constants, "usually defined on a module level and written
   in all capital letters with underscores", which is exactly what the loose register names
   are and why they keep that spelling. The register attributes INSIDE the group stay
   ALL_CAPS for the same reason, which also keeps them identical to the datasheet names, so
   the group stays a pure regrouping.

   The counter-argument to CapWords is that it makes a namespace look instantiable, and
   `Timer1()` reads like a constructor to anyone. That is answered by decision 5, which
   refuses the call with a message that says exactly that and what to write instead. Without
   that refusal the ALL_CAPS spelling would have been the safer one; with it, PEP 8 wins.

3. **The register names are the datasheet's.** `Timer1.TCCR1A`, not `Timer1.TCCRA` and not
   `Timer1.control_a`. Two reasons, and the second is the decisive one: a name that matches
   the datasheet is greppable against the datasheet, and it makes the grouped form a pure
   re-grouping of the loose list, which a generator can do with no per-chip mapping table.

4. **Bit positions live in the same class.** `Timer1.CS10`, `Timer1.TOV1`. One import brings
   the whole peripheral, and no bit name reaches module scope. This is the answer to XC8's
   `T1CONbits.TMR1ON` at zero cost: `Timer1.TCCR1B[Timer1.CS10] = 1` lowers to the same
   `SBI` the literal bit index lowers to. A `TCCR1Bbits.CS10 = 1` accessor would read better
   still, and it is NOT proposed here: it needs a two-level member access to lower to a
   single bit operation, which is new machinery, for a spelling that saves one subscript.

5. **A group cannot be instantiated, and says so.** `Timer1()` used to be accepted and
   produce nothing at all: the name still resolved to the group, so the register accesses
   around the call worked and the meaningless call went unsaid. A group is a namespace over
   the silicon, not a type, so the call is now a located error naming the group and saying
   what to write instead. The refusal is keyed on the class declaring at least one register,
   so an ordinary class is untouched. This is what decision 1 buys instead of a metaclass:
   the enforcement a singleton metaclass would provide, as a diagnostic, at no runtime cost
   and with no compile-time object model (see 3c).

6. **Per instance, not per kind.** `Timer0`, `Timer1`, `Timer2` are three classes, because on
   the ATmega328P they are three different register sets at three different widths. A
   `Timer(n)` abstraction over them is the HAL's job (`pymcu.hal.avr.timer`), and it already
   exists; this layer is the registers, not a driver.

7. **The addresses ARE the loose names, not copies of them.**

   ```python
   class Timer1:
       TCCR1A: ptr[uint8] = ptr(TCCR1A)
   ```

   One copy of every address in the file, so the grouped surface cannot drift from the loose
   one. This is what makes decision 2 pay: the group is mechanically derivable and
   mechanically checkable.

8. **The loose names stay, and they stay undocumented.** They are not deprecated with a
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
   receiver naming a class was the whole test, so `Timer1.TIFR1[Timer1.TOV1] = 1` built a
   phantom dict entry and emitted nothing. The same hole swallowed `Store.buf[0] = 5` on a
   class-level `bytearray`, which is a pre-existing silent-wrongcode bug of its own: the
   matching read folded to the value just "written" while the array in SRAM kept its zeros,
   so the program agreed with itself at that one subscript and read zeros from a loop, a
   runtime index or another function.

3. **A class constant as a bit index missed the direct bit test.** `if TIFR1[Timer1.TOV1]:`
   materialized the bit into a register and compared it, ten bytes where `SBIS` answers in
   one, while the same condition written `if TIFR1[0]:` was free.

An address the scan cannot resolve is now a located error naming the attribute, instead of
the group being filed as a dead SRAM variable. A fourth silence, `Timer1()` compiling to
nothing, is refused by decision 4 above.

## 3b. The ptr-over-a-base-address view, and why it is the ARM shape

A C header describes a peripheral as a struct at a fixed address, and the obvious question is
whether a group should be that: a `ptr` view over a base, so that Timer0, Timer1 and Timer2
become ONE type at three bases instead of three hand-written classes. Measured, the answer
splits by architecture, and the split is not a matter of taste.

**On AVR no peripheral is a contiguous block.** Timer/Counter1's fourteen registers run from
TIFR1 at 0x36 to OCR1BH at 0x8B, an 86-byte span holding 14 registers and 72 holes, with
TIMSK1 at 0x6F sitting between TCNT0's block and TCCR1A's. Timer0 spans 58 bytes for 8
registers, Timer2 128 bytes for 9. Only TWI (0xB8..0xBD), SPI (0x4C..0x4E) and each PORT
triple are dense. Worse for the "one type" claim, the three timers do not have the same
SHAPE at different offsets: TCCR1C, ICR1 and a 16-bit TCNT1 exist only on Timer1, ASSR only
on Timer2, the counters are 8 bits on Timer0 and Timer2 and 16 on Timer1, and all three share
one GTCCR. There is no base and no stride that turns one into another, so on AVR "the same
type at three base addresses" is not a spelling problem; it is not expressible.

**On the RP parts it is exactly the right model, and the chip file already writes it that
way.** `lib/src/pymcu/chips/rp2040.py` carries 60 `*_BASE` constants and spells every register
as `ptr(BASE + offset)`, with real strides (IO_BANK0 is 8 bytes per pin) and the atomic
XOR/SET/CLR aliases at +0x1000/+0x2000/+0x3000. There a group IS a view over a base and the
"one type, N instances" buy is real.

The grouped-class spelling accommodates both without change, because the address expression is
free: `ptr(0x80)`, `ptr(TCCR1A)`, `ptr(TCCR1A + 1)` and `ptr(T1_BASE + 0x04)` are all resolved
by the same scan-time evaluator. What decides the cost is WHERE the base lives:

| Base held as | Result | Measured |
|---|---|---|
| A module-level `const` | Folds to a constant address | Byte-identical to the loose program |
| A `const[uint16]` parameter of an `@inline` function | Folds to a constant address | Byte-identical to the loose program, 146 B, one body serving any base |
| A field of a ZCA instance (`ptr(self._base + 4)`) | Does NOT fold; degrades to a runtime pointer | 156 B against 146 B on a three-access program, an `LDI` pair and an indirect `LD`/`ST` per access |

So "one body, N bases" is available today at zero cost through a `const` parameter, and only
"one TYPE, N instances" is blocked. The blockage has one cause and one location:
`TryEvalConstAddress` (`src/compiler/IR/IRGenerator/Call.cs:9857`) has cases for
`IntegerLiteral`, `BinaryExpr`, `UnaryExpr` and `VariableExpr`; a `MemberAccessExpr` matches
none of them and the whole address expression returns null. Closing it means folding a field
whose value is a compile-time constant, and only such a field, which is a change with a real
risk of folding a field that is not one. It is not done here, and it is the first thing to do
if the RP backends want peripheral instances rather than peripheral classes.

## 3c. Metaprogramming, and what the ecosystem actually uses

The Python way to express a singleton is a metaclass, and in an AOT compiler a metaclass runs
at class-creation time, which is compile time, so the idea deserves a measurement rather than
a preference. Every claim below is one.

**Nothing of the machinery exists.** `class X(metaclass=M):` does not parse on the C# front
end, which refuses the keyword in the class header ("Expected ')'"). On the Python front end
it parses, because CPython's `ast` accepts it, and then fails in the IR generator on the
metaclass itself: `class M(type)` reports that the base `type` is not defined. Two front ends,
two different refusals, one conclusion.

**`CompileTimeEvaluator` is not a general evaluator.** It is 303 lines that syntactically match
a fixed table of shapes: `__CHIP__.*`, `__FREQ__`/`F_CPU`, `__name__`, `sys.platform`,
`sys.implementation.*` and `os.uname().*`, combined with and/or/not, equality, ordering, `in`
over a uname call and `.startswith`. What it returns is whether a CONDITION is true, so a dead
branch can be removed. It has no environment, no values other than the ints and strings in that
table, no function call, no dict and no class object. A metaclass body needs a class object as
a first-class value, dispatch through `type.__call__`, `super().__call__(*args, **kwargs)`, a
mutable dict keyed by class objects and identity over them. None of that is a step away from
this evaluator; it is a compile-time object model, and the roadmap already records the adjacent
limits (`@staticmethod` unsupported because calling through the class object is what is
missing, and `cls` is not a runtime object).

**The cheaper spellings, measured on the same program** (two register writes and a read in a
loop):

| Spelling | Bytes | Verdict |
|---|---|---|
| Class with class-level `ptr` constants | 146 | Byte-identical to the loose program. Zero |
| Module-level instance with `ptr` fields | 158 | Twelve bytes, and a spurious `TCCR1B = 0` store before the real one, which momentarily stops the timer |
| Module-level instance with methods returning `ptr[uint8]` | refused | `f().value = x` is not an assignable target |
| `__new__` returning a cached instance | compiles | NEVER RUNS. The probe prints 0 where CPython prints 1, with no diagnostic |
| `__init_subclass__` counting its subclasses | compiles | NEVER RUNS. The probe prints 0 where CPython prints 2, with no diagnostic |

The last two are a finding of their own and are not fixed here: both hooks compile clean and
do nothing, so a program that relies on either is silently wrong. They deserve a refusal.

**The two things a metaclass would buy, weighed.** ENFORCEMENT is buying a guard against a
thing that cannot happen: a class of class-level constants has no instantiation to control, and
measured, `t = Timer1()` followed by `t.TCCR1A.value = 0x82` compiles and still writes 0x80,
because the call produces nothing at all. The only wart is that the meaningless call is accepted
in silence, and the answer to that is a located refusal of a call on a register group, which is
a diagnostic and not an object model. FAITHFULNESS is buying compatibility with programs that
do not exist: see the counts.

**Who demands it.** Counted over 493 Python files of library and layer source, the 265 in the
23 `cp-*` library projects plus 159 in `pymcu-circuitpython` and 69 in `pymcu-micropython`,
excluding virtualenvs, uv caches and site-packages (those hold pytest, packaging and pygments,
which are host tools and not code this compiler ever sees):

| Construct | Files | Occurrences |
|---|---|---|
| `metaclass=` | 0 | 0 |
| `type(name, bases, dict)` | 0 | 0 |
| `__new__` | 0 | 0 |
| `__init_subclass__` | 0 | 0 |
| `__set_name__` | 0 | 0 |
| `__slots__` | 0 | 0 |
| `abstractmethod` / `ABCMeta` | 0 | 0 |
| `__get__` | 19 | 25 |
| `__set__` | 15 | 27 |
| `namedtuple` | 4 | 13 |

Every descriptor is in the six modules of `adafruit_register` (`i2c_bit`, `i2c_bits`,
`i2c_struct`, `i2c_struct_array`, `i2c_bcd_datetime`, `i2c_bcd_alarm`) and their copies in the
library projects. Those already compile: `cp-register`, whose program reaches a PCA9685 through
`Struct(0x06, "<HH")` as a class attribute and assigns to it, builds unmodified at 2386 bytes
with seven TWCR accesses in the emitted assembly.

So the decision this RFC takes: a metaclass is the right Python answer to the question "how do
I express a singleton", and a poor use of effort as a compiler feature, because the shape it
would control does not occur in the code we compile and the group it would guard has nothing to
instantiate. Descriptors are where the ecosystem is, and they work.

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

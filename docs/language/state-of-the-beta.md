# State of the beta

Beta 1 (`0.1.0b1`) covers six packages: the compiler frontend and stdlib
(this repo), the AVR backend (`pymcu-avr`), the CircuitPython compatibility
layer (`pymcu-circuitpython`), and the MicroPython compatibility layer
(`pymcu-micropython`, added to the beta-1 scope 2026-09-26). ARM/RP2040/RP2350,
PIC, and RISC-V stay alpha on purpose. See [Supported targets](https://github.com/PyMCU/PyMCU#supported-targets)
for the per-backend maturity labels and why.

This page collects the numbers from the five suites that back that claim,
each with a link to the page that explains what it measures and how to
reproduce it. **Measured 2026-09-30** against `pymcu-compiler`/`pymcu-stdlib`
main at `11e8bbe5`, `pymcu-avr` main at `740fe5c`, `pymcu-circuitpython` main
at `4fa38d6`, `pymcu-micropython` main at `9f602f0`, both compiler front ends
where the suite runs both. Five measurements over two days now; each earlier
one found something the next fixed (the corpus size-gate failure and a
CircuitPython blink-size gap, the descriptor protocol P0 fix, five silent
bugs from deciding by a name's spelling rather than what it resolves to, and
now a real-silicon hang a user found and confirmed on their own Arduino
Uno), so treat this as the current state, not a delta worth re-deriving from
the earlier ones (see the CHANGELOG for the full history). The most
user-visible fixes of this window: every beta-1 program that prints a
`float` used to get two fixed decimals silently; a class attribute named
`value` defining `__get__`/`__set__` (exactly how `adafruit_register`/
`digitalio` spell a descriptor) never called it; and `self.i2c = i2c` (the
`I2CDevice`/`busio.I2C` aliasing pattern nearly every Adafruit driver uses)
created a separate copy instead of sharing the original's storage, which
could hang a board waiting on an I2C lock that never agreed with itself.

## The five suites

| Suite | What it measures | Result | Docs |
|---|---|---|---|
| User-program corpus | 51 user-style AVR programs, each with an expected build outcome and a size gate | 51 of 51 pass (re-baselined three times this window: the `e.args[0]` `IndexError` fix, the float print policy fix, and the RFC 0013 boot zero-init, which alone costs a program with static SRAM state about +24 B once and 2 B per register that homes static state; all accepted, see the CHANGELOG) | [`pymcu-circuitpython/docs/corpus.md`](https://github.com/PyMCU/pymcu-circuitpython/blob/main/docs/corpus.md) |
| CircuitPython API parity | Every `digitalio`/`analogio`/`busio`/`pwmio`/… symbol upstream defines, checked against this layer | 240 symbols (180 provided, 60 allowlisted with a reason), 0 unexpected failures (unchanged by this window's commits), part of 522 tests passing across this layer's full suite (`pytest tests/`, independently re-run and confirmed 2026-09-30 after rebuilding this pass's own compiler, which had gone stale mid-day and briefly misreported 2 corpus failures until caught and rebuilt) | [`pymcu-circuitpython/docs/parity.md`](https://github.com/PyMCU/pymcu-circuitpython/blob/main/docs/parity.md) |
| MicroPython API parity | Every `machine`/`utime`/`uasyncio`/… symbol the real firmware surface defines, checked against this layer | 374 symbols measured, 0 failures, part of 888 tests passing across this layer's full suite (`pytest tests/`, confirmed 2026-09-29). (`tests/parity/report.py`'s own doc-generation pass separately walks the full CPython/typeshed `asyncio` stub with no filter and reports 393 symbols/52 "missing" for `uasyncio`; that is the generator over-counting, not a gap -- `test_uasyncio_parity.py` restricts itself by design to the ~70-name surface a real MicroPython board actually exposes, and every one of those cases passes) | [`pymcu-micropython/docs/parity.md`](https://github.com/PyMCU/pymcu-micropython/blob/main/docs/parity.md) |
| HAL parity (`tests/stdlib/test_hal_parity.py`) | The register-level HAL's own API, compared across all seven backend targets (avr, pic12/14/18, riscv, rp2040, rp2350) | 253 facade/API deviations checked: 5 pass strict, 248 currently allowlisted, each tracked | [`docs/library/hal-parity.md`](../library/hal-parity.md) |
| Differential oracle (`tests/oracle/test_oracle.py`, in the `pymcu-avr` repo) | 440 probes compiled and run on the AVR emulator, diffed against CPython running the same source, both front ends (4 new probes since the descriptor protocol fix, 528-531) | C# front end: 415 of 426 run pass, 11 tracked as filed compiler bugs (`xfail(strict)`, suite green), 14 skipped (front-end-restricted the other way). Python front end: 412 of 426 run pass, 14 tracked, 14 skipped. Probe `080_descriptor_get_set.py` moved from tracked to passing this window: see below | [`docs/language/oracle.md`](oracle.md) |

The compiler-repo gates on `11e8bbe5`, independently re-run by this pass after
rebuilding both this repo's and `pymcu-avr`'s binaries: `just test-unit` 3352
passed (exact match with the gate the commits' own author ran), `tools/verify_ir.py`
0 regressions, `pymcu-avr`'s full integration suite on `740fe5c` 3966 passed,
0 failed (also an exact match). `just test-stdlib` and `pytest tests/driver`
passed with 0 failures both times but collected fewer tests in this pass's own,
narrower venv (2115/956 against a venv with only the AVR backend and the two
compat layers installed) than the fuller dev venv the commits' own author
measured from (2146/1027, likely more backends installed unlocking more
parametrized cases): a venv difference, not a regression, since neither run
had a single failure.

**Test harness guarantee, now permanent**: the AVR integration suite's cold
boot starts every run with `R0`-`R31` (except `R1`) and all of SRAM filled
with `0xFF` by default (RFC 0013), instead of the previous always-zero-on-
fresh-state the emulator gave for free. A program that only worked by
accident of implicit zeroing now fails a test instead of passing one a real
chip would not; see the CHANGELOG for the bug this caught before the default
flipped.

**The corpus size gate is clean again.** `42_except_as_e_args.py` and the
CircuitPython-blink exception-tail gap this section used to flag here were
both explained and fixed by commits that landed after the first measurement
today (`fix(ir): print(e.args[0]) does not ask twice...` and `fix(ir): a raise
the optimizer already proved dead stops rooting the exception tail`, both in
`pymcu-compiler`); see its CHANGELOG for the mechanism.

## What the oracle knows is wrong

The differential oracle does not just count matches. Its 14 tracked probes
(measured 2026-09-29 straight from the `# tracked: #N` headers under
`tests/oracle/probes/` in the `pymcu-avr` repo, both front ends) cover 11
distinct filed issues, each probe an `xfail(strict)` case so the suite stays
green without hiding them. Three probes are `# frontend: py-parser`-scoped
(only run, and only xfail, under `PYMCU_PY_PARSER=1`): the C# front end sees
11 xfails, the Python front end sees all 14.

| Kind | Issues | What it means for a beta-1 program |
|---|---|---|
| Silently wrong value (no diagnostic, wrong answer) | [#364](https://github.com/PyMCU/PyMCU/issues/364) OPEN, [#394](https://github.com/PyMCU/PyMCU/issues/394) OPEN (three probes: nested comprehension, filter, instance comprehension), [#395](https://github.com/PyMCU/PyMCU/issues/395) OPEN, [#401](https://github.com/PyMCU/PyMCU/issues/401) OPEN (two probes: `match` sequence pattern on a real array, and its star-pattern sibling under the Python front end), [#426](https://github.com/PyMCU/PyMCU/issues/426) OPEN, [#449](https://github.com/PyMCU/PyMCU/issues/449) OPEN, [#521](https://github.com/PyMCU/PyMCU/issues/521) OPEN (Python front end only), [#522](https://github.com/PyMCU/PyMCU/issues/522) OPEN, [#525](https://github.com/PyMCU/PyMCU/issues/525) OPEN, [#439](https://github.com/PyMCU/PyMCU/issues/439) OPEN (Python front end only) | An unannotated loop accumulator, list comprehensions with more than one clause, a ZCA `__add__`/`__lt__`, `match/case` on a real array (phantom variables, and never checking arity), a method attached to a class after its definition, a type annotation reached through a module alias, PEP 695 type parameters dropped by the Python front end, an `int8`/`uint8` runtime comparison, and (Python front end only) a `match` tuple pattern taking the wrong branch can each compute the wrong answer with no error. Each has a fixture pinning today's wrong output so a silent fix does not regress. |
| Correctly refused, misleading reason | [#400](https://github.com/PyMCU/PyMCU/issues/400) OPEN | Reading an `Enum` member outside a plain assignment RHS gives a diagnostic that says the enum class is undefined rather than naming the real limitation. |

**Fixed today, during this release's own prep.** Probe `080_descriptor_get_set.py`
cited [#391](https://github.com/PyMCU/PyMCU/issues/391) (CLOSED, unrelated title) for
a still-reproducing bug this page's earlier draft flagged as a stale citation with no
real issue tracking it: a class attribute literally named `value` whose class defines
the descriptor protocol never called `__get__`/`__set__`, silently, because `.value`
was recognized earlier and unconditionally as the MMIO/collapsed-scalar shortcut,
exactly the name `adafruit_register` and `digitalio` use. Fixed the same day this
page flagged it; the probe now matches CPython and is no longer tracked. Three new
shapes that used to hit the same silent path are now refused with a diagnostic
instead (probes `529`-`531`): writing a non-data descriptor, reading one through the
class itself, and a descriptor defining `__set_name__`.

Closed since 2026-09-15 and no longer tracked: `#390` (a field read through
`with ... as`), `#392` (`bytearray()` assigned to `self.field`), `#396`
(`len(instance)`), `#397` (two-index `__setitem__`), `#393` (folded
`hex`/`bin`/`str`), `#398` (`list[T].append()` on the heap), and `#399` (a
one-character index) -- all fixed and untracked between the 2026-09-15
freeze and today.

The distinction from "silent" as the term is used for the beta-1 exit bar:
every probe above is filed, disclosed here, and enforced by a test that
fails the moment the bug disappears without a matching fixture update. What
made three bugs found the night before freeze (silent-write-loss in
`@inline`, a factory's field going stale, `super()` with constant
constructor args: [#427](https://github.com/PyMCU/PyMCU/issues/427),
[#429](https://github.com/PyMCU/PyMCU/issues/429),
[#430](https://github.com/PyMCU/PyMCU/issues/430)) block the freeze instead
of joining this table is that nothing had caught them yet. That was the standard applied at the 2026-09-15 freeze. It did not
survive contact with the second instrument: the 23 issues of 2026-09-25
were all outside the table, and they were found by pointing a new kind of
program at the compiler rather than by any suite already running. So the
claim this page makes is the narrower and truer one. Beta 1 ships with
every bug we know about written down on this page, and the honest reading
of one day that produced 23 of them is that more instruments will find
more.

Maintaining this row by hand is how it goes wrong in both directions: on
2026-09-26 it listed three bugs that were already fixed and omitted three that
were not. The list of probes the oracle still tracks is the live answer, in the
`# tracked: #N` headers under `tests/oracle/probes/` in the `pymcu-avr` repo.
Regenerate this row from those headers rather than editing it by hand.

## What a second instrument found

The oracle above compares probes we wrote. A different instrument compiles
libraries we did not write: the unmodified upstream Adafruit CircuitPython
and MicroPython drivers, straight from PyPI, with the transactions they put
on the bus diffed against the same driver running under the real
interpreter. It found bugs the probe corpus could not, for the reason that
makes it worth running: a probe exercises the construct an author already
suspected, and a real driver exercises the combination nobody chose.

On 2026-09-25 that harness opened 23 issues in one day. Eleven are fixed and
in the tree, and a string campaign closed five older ones alongside them.

What follows is what is still open and can reach a beta-1 program, with the
ones that give a wrong answer and no diagnostic listed first, because that is
the category this page exists to disclose. Three of the rows below came from
the day after, on 2026-09-26, when this list was cross-checked against the
probes the oracle still tracks rather than against the issues opened that
week. Two of those three had been open for longer than any issue in the
first draft of this table, and were missed for the dull reason that the
draft was written by looking at recent work instead of at the measurement.

| Issue | What it does to a program | Reaches |
|---|---|---|
| [#490](https://github.com/PyMCU/PyMCU/issues/490) | The ATtiny register maps are shifted against the vendor headers, so `analog_read()` on an ATtiny85 writes `ADMUX` where `ADCL` lives | ATtiny only |
| [#494](https://github.com/PyMCU/PyMCU/issues/494) | `int()` is a cast to `int16`, so `int(46051.7)` prints `-19485` | every target |
| [#506](https://github.com/PyMCU/PyMCU/issues/506) | A `str` is stored in a one-byte slot, so returning one from a function, or taking one as a bare parameter, truncates its id | every target |
| [#510](https://github.com/PyMCU/PyMCU/issues/510) | A method with a single call site is inlined, and the filled ellipse it expands draws the wrong pixels | every target |
| [#495](https://github.com/PyMCU/PyMCU/issues/495) | A compile-time chip predicate stops folding once it is bound to a name in another module, so both branches are compiled into the firmware | every target |
| [#500](https://github.com/PyMCU/PyMCU/issues/500) | Two per-chip HAL modules exist twice; the facade wires one copy and the other has drifted, so reading the unwired copy describes behaviour the compiler does not have | every target |
| [#449](https://github.com/PyMCU/PyMCU/issues/449) | A type annotation reached through a module alias, `t.uint8` after `import pymcu.types as t`, wraps where the bare name promotes: the same program prints `44` one way and `300` the other, with nothing said | every target |
| [#446](https://github.com/PyMCU/PyMCU/issues/446) | A constructor reading a field off another instance it was handed reads `0` instead of the value: a driver whose `__init__` takes a configured object and copies one field out of it gets a zero | every target |
| [#439](https://github.com/PyMCU/PyMCU/issues/439) | `match` on a tuple pattern compiles under the Python front end where the C# one refuses it, and takes the wrong branch | `PYMCU_PY_PARSER=1` |

Two more are about the toolchain rather than a program, and belong here
because they decide whether the numbers on this page can be trusted at all.
[#496](https://github.com/PyMCU/PyMCU/issues/496): the IR handed to a
backend is a wire format with no version field, so a backend wheel built
against an older frontend decodes an operator as a different operator and
miscompiles in silence. [#504](https://github.com/PyMCU/PyMCU/issues/504):
the diagnostic text format is an undeclared public interface that two
shipped editor plugins already parse.

Two are quality of diagnostics, not correctness:
[#505](https://github.com/PyMCU/PyMCU/issues/505) reports an internal
compiler error as a diagnostic on line 1 of the user's file, and
[#509](https://github.com/PyMCU/PyMCU/issues/509) emits one diagnostic per
invocation and drops a warning when the program also fails.

One is deferred by decision rather than left open by omission:
[#507](https://github.com/PyMCU/PyMCU/issues/507), where `enumerate(s)`
unrolls on length alone and eight characters of `display.text()` cost 12736
bytes while sixteen cost 4746. The cause is measured and the one-condition
fix is verified; what is not done is the program-by-program account of
which corpus programs change size, and that is not work to land days
before a release.

## Two more, known and not yet filed

Measured on the AVR emulator during this release's own prep, both compiler front ends,
against `main`. Neither has an oracle probe or a fixture pinning it yet, and neither has
a filed issue. Both are compile-time refusals, not silent wrong values.

- **`x.value += n` refuses to compile in a class with more than one field, in one that
  declares `@property value` with a setter, and through a non-simple receiver
  (`w.sensor.value += n`), while the identical program with the field renamed to
  anything other than `value` compiles and runs.** This is the same `.value`
  MMIO/collapsed-scalar shortcut the descriptor protocol fix above had to
  out-prioritize, in a shape that fix does not cover: an augmented assignment, not a
  plain read or a plain `=` (a simple `=` through a non-simple receiver still works). A
  class with exactly one field named `value` still compiles and matches CPython.
  ```python
  class Sensor:
      def __init__(self):
          self.value: uint8 = 3
          self.step: uint8 = 1
  s = Sensor()
  s.value += 8   # CompileError here
  ```
  `augmented assignment to .value requires a pointer or register target`
  (`Assign.cs` around line 9880-9935). Renaming `value` to `amount` (keeping the second
  field) compiles and prints `11`, matching CPython, on both front ends.
- **A user's own function named `claim` or `asm` is refused as soon as the program
  imports anything from a chip module** (`from pymcu.chips.atmega328p import GPIOR0` is
  enough; the program need not import `pymcu.types` itself, the chip module does it
  transitively), because the compiler's own intrinsics of those names take over the
  call.
  ```python
  from pymcu.chips.atmega328p import GPIOR0
  def claim(x: uint8) -> uint8:
      return x + 1
  print(claim(5))   # CompileError here
  ```
  `claim() takes claim(key, value, owner="", hint="")` (the intrinsic's own signature).
  `def asm(x): ...` called the same way gives `asm() argument must be a compile-time
  string literal`. Both confirmed on both front ends.

## What ran on real hardware for this release

On 2026-10-04 the release candidate's compiler (`main` at `11e8bbe5`) was run on
a real Arduino Uno (ATmega328P) with two unmodified Adafruit CircuitPython
libraries, through the CircuitPython layer:

- `adafruit_ssd1306`, with `adafruit_framebuf` and `adafruit_bus_device`, driving
  an SSD1306 128x32 OLED over I2C: two Game of Life programs (one drawing with a
  `pixel()` loop, one with `fill_rect` and `time.sleep`) and a text program using
  `display.text()` with the `font5x8.bin` font. All three run correctly.
- `adafruit_hcsr04`, its own `hcsr04_simpletest.py` with only the pins changed
  (trigger on D5, echo on D2). It reports correct distances.

These are the only two Adafruit libraries that ran on silicon for this release.
Every other Adafruit figure on this page and in the CHANGELOG (simpletests and
programs that compile, bus transactions compared against the real interpreter)
comes from compilation and the AVR emulator, not from a board.

## Five silent wrong values found after the candidate was cut

Measured on the AVR emulator, both compiler front ends, against this release
candidate itself. None of the five has an oracle probe, a fixture, or a filed
issue in the candidate, and none announces itself: every program below builds
clean and produces a wrong value. Two are already fixed on `fix/p2-avr-gaps`,
two are fixed or refused on `fix/silent-list-tuple`, and one is fixed on
`fix/name-collision`; all five land in beta 2.

- **A top-level name reassigned from a string literal to a run-time-built
  string keeps the old text.** The first `print` is correct; the second prints
  the original literal again instead of the new contents.
  ```python
  from pymcu.chips.atmega328p import GPIOR0
  from pymcu.types import uint8

  text = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
  print(text)                  # the literal
  n: uint8 = GPIOR0.value      # a run-time value
  text = f"{n}"
  print(text)                  # prints the literal again, not "0"
  ```
  PyMCU prints the literal twice; CPython prints it once, then `0`. Avoid it by
  binding the run-time-built string to a different name (`text2 = f"{n}"`),
  which prints `0`. Fixed on `fix/p2-avr-gaps` (`3f60a96d`), landing in beta 2.
- **An f-string whose only interpolated part is a string literal prints the
  compiler's internal string id instead of the text.**
  ```python
  text = f"{'literal string'}"
  print(text)                  # prints 257, not "literal string"
  ```
  PyMCU prints `257`; CPython prints `literal string`. Avoid it by assigning
  the plain literal (`text = "literal string"`). Interpolating a name bound to
  a literal (`f"{name}"`) goes down a different path that is refused with a
  diagnostic rather than answered wrongly. Fixed on `fix/p2-avr-gaps`
  (`ab6aa218`), landing in beta 2.
- **`list.append()` loses the fields of the instances it appends.** Every
  element read back from the list reports `0` for its fields whatever the
  constructor stored, whether the appends run inside `for i in range(N)` or
  unrolled at top level.
  ```python
  from pymcu.types import uint8


  class Counter:
      def __init__(self, n: uint8) -> None:
          self._n = n

      @property
      def n(self) -> uint8:
          return self._n

  xs = []
  for i in range(2):
      xs.append(Counter(i))
  a0 = xs[0]
  a1 = xs[1]
  print(a0.n, a1.n)            # prints "0 0", not "0 1"
  ```
  PyMCU prints `0 0`; CPython prints `0 1`. Avoid it by building the list as a
  literal (`xs = [Counter(0), Counter(1)]`, prints `0 1`) or by keeping the
  instances in separate names. Unrolling the appends does NOT help:
  `xs.append(c0); xs.append(c1)` with named instances still prints `0 0`. On
  `fix/silent-list-tuple` every one of these shapes is refused at compile time
  with a diagnostic instead, landing in beta 2.
- **Unpacking a `(bytearray, scalar)` tuple returned by a function never
  delivers the buffer.** The scalar element lands; the bytearray name reads
  back as the zeroed buffer it already was.
  ```python
  from pymcu.types import uint8


  def search_rom(seed: uint8):
      new_rom = bytearray(8)
      new_rom[0] = 40
      return new_rom, seed

  rom = bytearray(8)
  diff: uint8 = 7
  rom, diff = search_rom(9)
  print(rom[0], len(rom), diff)   # prints "0 8 9", not "40 8 9"
  ```
  PyMCU prints `0 8 9` (`diff` does update; only the buffer is lost); CPython
  prints `40 8 9`. A tuple of two scalars unpacks correctly, and indexing a
  named result hits the same broken path (`r = search_rom(9); rom = r[0]` is
  equally wrong). Avoid it by returning the buffer alone
  (`rom = search_rom(9)` returning just `new_rom`, prints `40 8`) and passing
  the scalar separately. Fixed on `fix/silent-list-tuple`, landing in
  beta 2.
- **Two calls to the same `@inline` function that returns a tuple, indexed in
  one expression, read the second call's result twice.** Every expansion at the
  same nesting depth kept its tuple result in the same compiler slots, so the
  second call overwrote the first one's result before it was read.
  ```python
  from pymcu.types import inline, uint8
  from pymcu.chips.atmega328p import GPIOR0

  @inline
  def pair(v: uint8) -> (uint8, uint8):
      return v, v + 1

  s = GPIOR0.value
  r: uint8 = pair(s + 3)[0] + pair(s + 8)[0]
  print(r)                     # prints 16, not 11
  ```
  PyMCU prints `16`; CPython prints `11`. The same slots make
  `print(add2(pair(1)[0], pair(9)[0]))` print the tuple `(9, 10)` instead of
  `10`. Avoid it by binding each call's element to its own name first
  (`a: uint8 = pair(s + 3)[0]` and `b: uint8 = pair(s + 8)[0]`, then `a + b`),
  or by unpacking each call (`a, a2 = pair(s + 3)`); both print `11`. Fixed on
  `fix/name-collision`, landing in beta 2.

## What "beta" does and does not claim

- **Beta** means: the language surface is implemented and test-covered on
  the AVR backend, and it is validated on real silicon (Arduino Uno, logic
  analyzer differential harness). For libraries, silicon coverage in this
  release is two Adafruit drivers; see
  [What ran on real hardware for this release](#what-ran-on-real-hardware-for-this-release). It does **not** mean every API symbol
  above is implemented: the parity suites above exist precisely to make
  the gap explicit and trackable, row by row, rather than asserted.
- Every allowlisted deviation and every `tracked:#N` / `xfail` entry in
  these suites names a GitHub issue. None of them are silent.
- `keypad.KeyMatrix` ([pymcu-circuitpython#13](https://github.com/PyMCU/pymcu-circuitpython/issues/13))
  and the storage/os modules ([pymcu-circuitpython#16](https://github.com/PyMCU/pymcu-circuitpython/issues/16))
  are out of beta 1 by decision, not by omission.
- The integer-width inference RFC (RFC 0002, tracked as
  [PyMCU#364](https://github.com/PyMCU/PyMCU/issues/364)) ships only behind
  its feature flag if it lands before release, off by default, and
  documented as experimental. It is not part of the beta-1 surface claim
  above.

See also: [Language Limitations](limitations.md) for what stops each
individual Adafruit CircuitPython library, and the
[release checklist](../release/beta1-checklist.md) for how this beta is
built, smoke-tested, and rolled back.

# State of the beta

Beta 1 (`0.1.0b1`) covers three packages: the compiler frontend and stdlib
(this repo), the AVR backend (`pymcu-avr`), and the CircuitPython
compatibility layer (`pymcu-circuitpython`). ARM/RP2040/RP2350, PIC, and
RISC-V stay alpha on purpose. See [Supported targets](https://github.com/PyMCU/PyMCU#supported-targets)
for the per-backend maturity labels and why.

This page collects the numbers from the five suites that back that claim,
each with a link to the page that explains what it measures and how to
reproduce it.

## The five suites

| Suite | What it measures | Result | Docs |
|---|---|---|---|
| User-program corpus | 49 user-style AVR programs, each with an expected build outcome and a size gate | 49 programs | [`pymcu-circuitpython/docs/corpus.md`](https://github.com/PyMCU/pymcu-circuitpython/blob/main/docs/corpus.md) |
| CircuitPython API parity | Every `digitalio`/`analogio`/`busio`/`pwmio`/… symbol upstream defines, checked against this layer | 230 symbols (175 provided, 55 allowlisted with a reason) | [`pymcu-circuitpython/docs/parity.md`](https://github.com/PyMCU/pymcu-circuitpython/blob/main/docs/parity.md) |
| MicroPython API parity | Every `machine`/`utime`/`network`/… symbol upstream defines, checked against this layer | 292 symbols (74 provided, 218 allowlisted with a reason) | [`pymcu-micropython/docs/parity.md`](https://github.com/PyMCU/pymcu-micropython/blob/main/docs/parity.md) |
| HAL parity (`tests/stdlib/test_hal_parity.py`) | The register-level HAL's own API, compared across all seven backend targets (avr, pic12/14/18, riscv, rp2040, rp2350) | 191 facade/API deviations currently allowlisted, each tracked | [`docs/library/hal-parity.md`](../library/hal-parity.md) |
| Differential oracle (`tests/oracle/test_oracle.py`, in the `pymcu-avr` repo) | 109 probes compiled and run on the AVR emulator, diffed against CPython running the same source | 109 probes: 78 match (7 documented divergences), 11 correctly refused, 20 tracked as filed compiler bugs (`xfail(strict)`, suite green) | [`docs/language/oracle.md`](oracle.md) |

## What the oracle knows is wrong

The differential oracle does not just count matches. Its 20 tracked probes
(as of 2026-09-15; re-verify at freeze, since this list is regenerated from
`docs/language/oracle.md`'s "Compiler bugs, by cause" table) are 13 filed,
open compiler bugs, each an `xfail(strict)` case so the suite stays green
without hiding them:

| Kind | Issues | What it means for a beta-1 program |
|---|---|---|
| Silently wrong value (no diagnostic, wrong answer) | [#364](https://github.com/PyMCU/PyMCU/issues/364), [#390](https://github.com/PyMCU/PyMCU/issues/390), [#394](https://github.com/PyMCU/PyMCU/issues/394) (one of its three probes), [#395](https://github.com/PyMCU/PyMCU/issues/395), [#396](https://github.com/PyMCU/PyMCU/issues/396), [#397](https://github.com/PyMCU/PyMCU/issues/397), [#398](https://github.com/PyMCU/PyMCU/issues/398), [#401](https://github.com/PyMCU/PyMCU/issues/401) | An unannotated loop accumulator, a field read through `with ... as`, a nested comprehension, a ZCA `__add__`/`__lt__`, `len(instance)`, two-index `__setitem__`, `list[T].append()` on the heap, and `match` on an array can each compute the wrong answer with no error. Each has a fixture pinning today's wrong output so a silent fix does not regress. Two that were here, `#393` (folded `hex`/`bin`/`str`) and `#399` (a one-character index), were fixed on 2026-09-26 and their probes now match CPython. |
| Correctly refused, misleading reason | [#391](https://github.com/PyMCU/PyMCU/issues/391), [#392](https://github.com/PyMCU/PyMCU/issues/392), [#400](https://github.com/PyMCU/PyMCU/issues/400) (plus two of #394's three probes) | The build fails with a `CompileError` rather than shipping a wrong answer, but the message names the wrong cause (a class with no explicit `__init__`, `bytearray()` assigned to `self.field`, and an `Enum` member read outside a plain assignment all give a diagnostic that points somewhere other than the real limitation). |

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

## What "beta" does and does not claim

- **Beta** means: the language surface is implemented and test-covered on
  the AVR backend, and it is validated on real silicon (Arduino Uno, logic
  analyzer differential harness). It does **not** mean every API symbol
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

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
| Differential oracle (`tests/oracle/test_oracle.py`) | 109 probes compiled and run on the AVR emulator, diffed against CPython running the same source | 109 probes: 78 match (7 documented divergences), 11 correctly refused, 20 tracked as filed compiler bugs (`xfail(strict)`, suite green) | [`docs/language/oracle.md`](oracle.md) |

## What the oracle knows is wrong

The differential oracle does not just count matches. Its 20 tracked probes
(as of 2026-09-15; re-verify at freeze, since this list is regenerated from
`docs/language/oracle.md`'s "Compiler bugs, by cause" table) are 13 filed,
open compiler bugs, each an `xfail(strict)` case so the suite stays green
without hiding them:

| Kind | Issues | What it means for a beta-1 program |
|---|---|---|
| Silently wrong value (no diagnostic, wrong answer) | [#364](https://github.com/PyMCU/PyMCU/issues/364), [#390](https://github.com/PyMCU/PyMCU/issues/390), [#393](https://github.com/PyMCU/PyMCU/issues/393), [#394](https://github.com/PyMCU/PyMCU/issues/394) (one of its three probes), [#395](https://github.com/PyMCU/PyMCU/issues/395), [#396](https://github.com/PyMCU/PyMCU/issues/396), [#397](https://github.com/PyMCU/PyMCU/issues/397), [#398](https://github.com/PyMCU/PyMCU/issues/398), [#399](https://github.com/PyMCU/PyMCU/issues/399), [#401](https://github.com/PyMCU/PyMCU/issues/401) | An unannotated loop accumulator, a field read through `with ... as`, folded `hex`/`bin`/`str`, a nested comprehension, a ZCA `__add__`/`__lt__`, `len(instance)`, two-index `__setitem__`, `list[T].append()` on the heap, a one-character index, and `match` on an array can each compute the wrong answer with no error. Each has a fixture pinning today's wrong output so a silent fix does not regress. |
| Correctly refused, misleading reason | [#391](https://github.com/PyMCU/PyMCU/issues/391), [#392](https://github.com/PyMCU/PyMCU/issues/392), [#400](https://github.com/PyMCU/PyMCU/issues/400) (plus two of #394's three probes) | The build fails with a `CompileError` rather than shipping a wrong answer, but the message names the wrong cause (a class with no explicit `__init__`, `bytearray()` assigned to `self.field`, and an `Enum` member read outside a plain assignment all give a diagnostic that points somewhere other than the real limitation). |

The distinction from "silent" as the term is used for the beta-1 exit bar:
every probe above is filed, disclosed here, and enforced by a test that
fails the moment the bug disappears without a matching fixture update. What
made three bugs found the night before freeze (silent-write-loss in
`@inline`, a factory's field going stale, `super()` with constant
constructor args: [#427](https://github.com/PyMCU/PyMCU/issues/427),
[#429](https://github.com/PyMCU/PyMCU/issues/429),
[#430](https://github.com/PyMCU/PyMCU/issues/430)) block the freeze instead
of joining this table is that nothing had caught them yet. Beta 1 ships
with the table above and zero bugs outside it.

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

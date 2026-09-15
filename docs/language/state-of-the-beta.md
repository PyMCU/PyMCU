# State of the beta

Beta 1 (`0.1.0b1`) covers three packages: the compiler frontend and stdlib
(this repo), the AVR backend (`pymcu-avr`), and the CircuitPython
compatibility layer (`pymcu-circuitpython`). ARM/RP2040/RP2350, PIC, and
RISC-V stay alpha on purpose — see [Supported targets](../../README.md#supported-targets)
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

## What "beta" does and does not claim

- **Beta** means: the language surface is implemented and test-covered on
  the AVR backend, and it is validated on real silicon (Arduino Uno, logic
  analyzer differential harness). It does **not** mean every API symbol
  above is implemented — the parity suites above exist precisely to make
  the gap explicit and trackable, row by row, rather than asserted.
- Every allowlisted deviation and every `tracked:#N` / `xfail` entry in
  these suites names a GitHub issue. None of them are silent.
- `keypad.KeyMatrix` ([pymcu-circuitpython#13](https://github.com/PyMCU/pymcu-circuitpython/issues/13))
  and the storage/os modules ([pymcu-circuitpython#16](https://github.com/PyMCU/pymcu-circuitpython/issues/16))
  are out of beta 1 by decision, not by omission.
- The integer-width inference RFC (RFC 0002, tracked as
  [PyMCU#364](https://github.com/PyMCU/PyMCU/issues/364)) ships only behind
  its feature flag if it lands before release, off by default, and
  documented as experimental — it is not part of the beta-1 surface claim
  above.

See also: [Language Limitations](limitations.md) for what stops each
individual Adafruit CircuitPython library, and the
[release checklist](../release/beta1-checklist.md) for how this beta is
built, smoke-tested, and rolled back.

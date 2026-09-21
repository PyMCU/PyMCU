# RFC 0010: profile-guided optimisation (workload -> emulator -> profile -> optimizer)

- Status: **EXPERIMENTAL**, implemented on the `pgo-land` branches of this repo and
  `pymcu-avr`, gated behind `[tool.pymcu.experimental] pgo = true` (or
  `PYMCU_EXPERIMENTAL_PGO=1`). Everything below past the design sections is the
  measured result of the `pgo-spike` feasibility study this landed from.
- Date: 2026-09-21
- Affects: `src/driver/core/workload.py` (new), `src/driver/core/project_config.py`,
  `src/driver/commands/profile.py`, `src/driver/commands/build.py`,
  `src/driver/core/compiler.py`, `src/driver/backends/__init__.py`,
  `src/compiler/Common/Models/CompilerOptions.cs`,
  `src/compiler/Infrastructure/Cli/CompilerCliBuilder.cs`,
  `src/compiler/IR/PgoProfile.cs` (new), `src/compiler/Pipeline/Phases/IrGenerationPhase.cs`,
  `src/compiler/IR/Optimizer.cs`;
  pymcu-avr: `src/csharp/lib/Targets/AVR/AvrBlockMap.cs` (new),
  `src/csharp/lib/Targets/AVR/AvrCodeGen.cs`, `src/csharp/cli/Program.cs`,
  `src/csharp/profiler/{Program,Workload,PgoRunner}.cs`,
  `tests/integration/{PymcuCompiler,Differential/TraceComparison}.cs`,
  `tests/integration/Tests/AVR/PgoDifferentialTests.cs`,
  `tests/integration/fixtures/pgo-hot-veto/`.
- Builds on: the TestKit stimulus surface (`SetPinValue`, `Serial.InjectByte`,
  `ProfilingDecoder`), the existing `--emit-symbols`/`--emit-linemap` machinery, the
  ELF symtab reader in `build.py`, and the `@inline` outlining pass in `Optimizer.cs`.

## 0. Decisions this RFC encodes

1. The workload is **declared data, not code**: a `workload.yaml` at the project root
   that the Python driver translates to JSON. No YAML parser ships in the C# profiler;
   chip and frequency come from `pyproject.toml`, not the workload. When the file is
   absent the driver says so and uses one default scenario, `run: {ms: 200}`.
2. The profile format keys blocks and edges by **the MIR label names the backend
   emitted**, verbatim (`L_279`, not a re-derived index). The compiler looks a region up
   by the label text it already knows; there is no second naming scheme to keep in sync.
   A profile whose keys share no name with the program is a foreign profile: warn and
   ignore, never fail the build.
3. Block addresses are **resolved post-link from the ELF symtab**, never counted at
   codegen time. `-mrelax` lets the linker shrink CALL/JMP after the backend has
   finished; only the symtab knows where a label landed. Labels the peephole deleted
   (unreferenced `L_*` are dead-label-eliminated) resolve to `null` and their PCs merge
   into the preceding block at profile time.
4. Branch markers (`_pgob_N`) are emitted into the asm stream as **comments** and turned
   into real labels only after the peephole has run. A label is a barrier to every
   peephole pattern (any label is a potential jump target); measured on
   `fixtures/yield-from`, markers-as-labels cost +216 B of lost jump threading.
   As comments they are inert; as post-peephole labels they still reach the symtab.
5. There is exactly **one consumer**, in `Optimizer.OutlineInlineExpansions`, and it
   is **veto-only**: a region whose enclosing block burns >= 1% of the workload's
   cycles is never outlined. The spike also carried a cold override (a region every
   site of which ran zero times was outlined even when the static word-cost model
   could not prove the win); it was dropped on landing because it only ever adds
   bytes -- the measured corpus delta is in section 6. Only a flipped decision is
   attributed to the profile (`pgo: kept N region(s) inline` DebugLine) -- a decision
   the cost model already made is not the profile's work and keeps the normal
   `__pymcu_outline_N` name.
6. `pymcu profile --pgo` **delegates the build to the real `pymcu build --debug`**
   rather than re-implementing compile+assemble+link. The first draft of the spike
   duplicated that path and silently dropped the preamble injection (print/input UART
   setup), the FFI C compilation, and the AVR math-runtime splice (`__div32` and
   friends) -- three separate latent bugs that only showed up as missing symbols or
   missing UART setup on real programs. Delegation makes drift structurally
   impossible.
7. The gate is a **differential axis, not a size assertion**: every corpus program is
   built with and without the profile and must produce identical UART bytes, GPIO
   transitions and BREAK checkpoints under the same scenario. Speed and size are
   measured, not gated -- the consumer is allowed to trade bytes for correctness of
   the mechanism, and the measurement reports what it actually did.
8. The whole path is **opt-in behind a feature flag**:
   `[tool.pymcu.experimental] pgo = true` in pyproject.toml, mirrored by
   `PYMCU_EXPERIMENTAL_PGO=1` for CI and scripts. With the flag off, `pymcu profile
   --pgo` and `pymcu build --profile` (or `PYMCU_PROFILE`) refuse with a one-line
   message that names the flag, and the driver never passes `--profile` to `pymcuc`
   nor `--emit-blockmap` to `pymcuc-avr`. The flag is a driver policy: `pymcuc
   --profile` itself keeps working, because the compiler is a tool and the knob on
   it is documented input, not a secret.

## 1. Workload format

```yaml
scenarios:
  - name: idle
    run: {ms: 200}                       # exactly one of ms | cycles | until | until_uart_bytes | until_i2c_transactions
    stimuli:
      - {at_us: 1000, uart_rx: "A"}      # text, hex "41 42", or a byte list
      - {at_us: 5000, pin: PD2, level: 1}
      - {every_us: 20000, pin: PD3, toggle: true}
      - {responder: hc_sr04, trig: PD6, echo: PD5, distance_cm: 9.7}
      - {i2c_slave: 0x3C}                # a bus device, not a timed event -- ACKs its address, reads 0xFF
    expect:
      uart_tx: "OK\r\n"                  # prefix match on emitted bytes
      i2c_tx: "3c 3c 80 af"              # prefix on the flattened transaction stream
```

`run.until: break`, `run.until_uart_bytes` and `run.until_i2c_transactions` bound a
run by an event instead of a duration; `run.max_ms` (default 5000) is the safety cap
so a broken scenario cannot sim forever. `expect.uart_tx` / `expect.i2c_tx` turn a
scenario into a test: a miss makes the profiler exit 1 with the mismatch, so a
workload can assert that the profiled run actually did the thing it was declared to
do. `until_i2c_transactions` closes a run when the Nth TWI transaction ends, which
is how the SSD1306 workloads bound themselves to the oracle's transaction count.

Validation lives in the driver (`workload.py`): pin names must look like `P[BD][0-7]`,
a stimulus needs one payload (`uart_rx`, `pin`, `responder`, `i2c_slave`) and one
time (`at_us`, `every_us`) -- `i2c_slave` is the exception, a device attachment that
needs no time -- `run` takes exactly one bound, and `i2c_slave` is a 7-bit address
given as int, hex or decimal string. Errors say `workload.yaml: <what>` and exit 1.

## 2. Block map

`pymcuc-avr --emit-blockmap <path>` (emitted by `pymcu build --debug` to
`dist/_debug/blockmap.json`) writes:

```json
{"Format": 1,
 "Blocks":   [{"Function": "main", "Label": "L_279", "Entry": false, "WordAddr": 101}],
 "Branches": [{"Id": 12, "Function": "main", "Sym": "_pgob_12",
               "Taken": "L_35", "Fallthrough": "L_36", "WordAddr": 92}]}
```

`WordAddr` is the link-time **word** address (`byte_addr // 2`), filled in by the
driver from the ELF symtab; the backend's own file writes `null` because it cannot
know post-relaxation addresses. `Entry` marks function and outlined-subroutine entry
points (the latter appear under their `__pymcu_outline_*` names, so their PCs stay
attributable after outlining moves them out of the caller's label space).

## 3. Profile format

```json
{"format": 1, "chip": "atmega328p", "freq": 16000000,
 "scenarios": [{"name": "idle", "cycles": 3200000, "instructions": 1234567}],
 "functions": {"main": {"cycles": 1000, "entries": 1}},
 "blocks":    {"L_279": {"count": 0, "cycles": 0}},
 "edges":     {"L_278->L_279": 1},
 "loops":     {"L_35": {"iterations": 4000, "entries": 1}}}
```

Blocks/edges/loops accumulate across every scenario; `scenarios[]` keeps per-scenario
cycle/instruction totals plus `crashed`/`expectMet` diagnostics. A block the run never
reached is simply absent from `blocks` -- absent means zero, which is the datum the
consumer actually needs. Loops count a backward PC inside a block as an iteration
(`L_35: ... BRNE L_35` never crosses a block boundary, so cross-block edges alone
would miss the commonest delay loop in the corpus).

## 4. The one consumer

`Optimizer.OutlineInlineExpansions` already groups identical `@inline` expansion
regions and proves a word-cost win (`nSites * body` vs `body + 1 + params + nSites *
(params + 2)`). The profile participates at exactly that decision point, per group,
as a veto only:

- `GroupIsHotByProfile` computes each region's blocks as its own internal labels
  plus the label that falls through into it (the block that owns the branch check),
  and sums the cycles the profile recorded against them.
- hot (enclosing block cycles >= 1% of `TotalCycles`) + statically accepted =>
  refuse, and record `pgo: kept N region(s) inline` as a `DebugLine` at the top of
  the function. Timing-critical loops keep their straight-line code.
- A veto that merely agrees with the cost model is not the profile's decision and
  gets no marker; a group the model rejects stays inline, profile or not.

The spike's second direction -- `allCold` (every site at count 0) + statically
rejected => outline anyway, named `__pymcu_outline_pgo_N` -- was removed before
landing. Its premise was that a CALL nobody executes is free, but the CALL's words
are still in the image: an override by construction pays more static words than it
saves, so its direct size contribution is always >= 0 (section 6 has the measured
ledger). What remains is the veto, which cannot grow the image: it only ever
declines an outlining the static model had already proved a win.

## 5. The gate

`PgoDifferentialTests` runs the whole corpus through `BuildFixtureProfiled` /
`BuildProfiled` (which do `pymcu profile --pgo` then `PYMCU_PROFILE=... pymcu build` in
a scratch copy, with `PYMCU_EXPERIMENTAL_PGO=1` set explicitly) and
`BehaviorRecorder`-compares UART bytes, pin-change sequences and BREAK checkpoints
against the plain build under the same trace budget. A guard test
(`ProfileSwitch_ActuallyChangesTheEmittedImage`) compiles `pgo-hot-veto` both ways
and fails if the images are identical -- without it the axis could pass vacuously on
a profile that never reached the optimizer. The fixture's `@inline` sites sit in the
hot loop, where the static model outlines them and the veto keeps them in.

## 6. Measurements (the corpus run this RFC cites)

These numbers are the spike's, measured with BOTH directions of the consumer live
(cold override + hot veto). They are kept because the +286 B net is the reason the
override did not survive landing: a consumer that can only ever add bytes is a
pessimiser with extra steps.

487 ATmega328P programs (`examples/*` + `tests/integration/fixtures/*` that build for
the Uno class, default 200 ms scenario each): **23 changed, 464 identical images, 0
errors**. Of the changed: 8 shrank (-54 B total), 2 changed bytes at identical size,
13 grew (+340 B). Net across changed: +286 B. Largest single delta:
`examples/bmp280` 848 -> 910 (+62 B, a cold multi-site group); largest win:
`fixtures/dsp-stress` 1586 -> 1570 (-16 B). Scenario cycles differed on 9 programs by
exactly +-1 cycle -- the `ms` bound can overshoot by the last in-flight instruction;
that is simulator granularity, not a codegen effect.

Size floor, held both ways: `examples/blink` 150 B -> 150 B; the MicroPython-layer
blink 142 B -> 142 B with `<main>` still at 0x68 and the loop `rjmp` still at 0x8c.

Differential axis: **484/484 pass**, 0 failures.

With the override removed the ledger is one-sided by construction: a veto can only
ever keep bytes the static model would have cut, so a profiled build is the same
size or larger, and the only changed images are the ones where the profile refused
a hot outlining. What the spike proved -- and what stays -- is the plumbing and the
gate: declared workload -> emulator run -> profile -> different firmware ->
identical behaviour -> measured delta, end to end on real programs.

## 7. What the profile must never do

- Fail or block a build. Unreadable file, wrong format version, or zero shared label
  names all degrade to an ordinary build with a `pgo:` warning on stderr.
- Change observable behaviour: UART byte order/content, GPIO sequence, timing of any
  block the workload showed hot (the 1% veto exists for this), or the ABI.
- ~~Reach the backend allocator directly.~~ Amended by section 9: the profile
  now reaches `pymcuc-avr` for one purpose only -- the ORDER of register homes.
  It still may not change what the backend is allowed to do, only which
  eligible variable sits in which callee-saved register.

## 8. Not done / deliberately out of scope

- Cold-region override: done on the spike, removed on landing. Outlining a region
  the workload never ran only moves cost from executed cycles to image bytes; the
  corpus paid +286 B net for it (section 6). The plumbing to bring it back exists
  (`ClassifyGroupByProfile` kept the per-region block sum as `GroupIsHotByProfile`),
  the ledger says do not.
- ~~Second consumer~~: implemented 2026-09-21 -- see section 9.
- Runtime-helper attribution: hand-emitted runtime subs (`__dly_*`, `__div*`,
  interrupt stubs) have no block-map entries, so their cycles fold into the last
  preceding user block. Cycle *shares* stay approximately right (helpers serve the
  blocks around them) but a helper-heavy block can look hotter than it is.
- Per-scenario consumer input: the compiler sees one aggregated profile; a workload
  with both a hot and an idle scenario cannot yet say "cold under load only".
- Speedscope mode and PGO mode share the binary but not a code path; `--emit-profile`
  with neither `--blockmap` nor `--workload` is an error by design.

## 9. Amendment (2026-09-21): the second consumer -- register priority

The profile now reaches the backend for ordering only. `pymcuc-avr --profile
<profile.json>` loads the same `format:1` profile the optimizer consumes and feeds
its block execution counts to `AvrRegisterAllocator.Allocate`. Per-variable weight
= sum over MIR instructions of (uses of the variable in the instruction x
execution count of the block containing it), where a use's block is the last
`Label` seen in the function body, else the function name -- the same boundaries
`--emit-blockmap` records. Blocks the profile does not mention count 1, never 0:
a variable the workload never touched still competes for a home. Only the ORDER
of the R2-R15 homes changes; eligibility (<= 2-byte ints, no GC_REF/FUNCREF, no
address-taken names) and the ordinal-name tiebreak are exactly as before. An
unreadable, wrong-version or foreign (zero shared block names) profile degrades
to the static order -- the profile is a hint, never a build blocker -- and the
backend prints one `[PGO]` line saying what it did, which the driver relays into
build output.

Driver plumbing: `pymcu build --profile <p>` (experimental flag on) forwards
`--profile` to `pymcuc-avr` alongside `pymcuc`. A backend binary whose `--help`
does not declare the flag is refused rather than silently building unprofiled.
Flag off or no profile: nothing reaches the backend and the image is
byte-identical to before this amendment (ROM gate: 508/508 examples + fixtures).

Measured, flag on, each program built plain and then with its own profile;
cycles come from re-profiling the profiled image on the same workload
(`pymcuc-avr-profiler <hex> --workload --blockmap --emit-profile`), per-frame
from the I2C trace's span between consecutive frame-write transactions:

| program | flash B | scenario cycles | last `show()` frame |
|---|---|---|---|
| fixtures/adafruit-ssd1306-unmodified (128x32) | 4,386 -> 4,484 (+98) | 171,815 -> 164,948 (-4.0%) | 44,632 -> 42,476 (-4.8%) |
| fixtures/adafruit-ssd1306-unmodified-64 | 13,888 -> 14,450 (+562) | 449,083 -> 369,593 (-17.7%) | 149,092 -> 131,708 (-11.7%) |
| examples/ssd1306 | 628 -> 628 (image differs) | 78,361 -> 78,361 | init only, no `show()` |
| examples/stopwatch | 562 -> 586 (+24) | ms-bound: flat (instr +0.00%) | -- |
| fixtures/fstring-bool | 1,020 -> 1,020 (image differs) | ms-bound: flat | -- |
| examples/error-handling | 884 -> 884 (image differs) | ms-bound: flat | -- |
| examples/blink | 150 -> 150 (byte-identical) | ms-bound: flat | -- |
| fixtures/pgo-hot-veto | 186 -> 210 (+24) | ms-bound: +7.3% instructions in-window | -- |

Honest reads: the two Adafruit fixtures pay +98/+562 B of flash for -4.0%/-17.7%
cycles; the bytes cost comes from static per-*site* counting happening to favour
code size (each LDS/STS replaced by a register access saves a word per static
site), so the dynamic order can trade words for cycles. The ms-bound scenarios
run to the time cap either way, so "cycles" cannot move on them -- the honest
metric there is instructions completed in the window: flat for stopwatch
(+24 B paid for nothing measurable), fstring-bool and error-handling (order
flipped, size unchanged, work-rate flat); only blink came out byte-identical.
pgo-hot-veto's +7.3% instructions-in-window is the section-4 hot veto doing its
job, not the allocator; it is listed because a profiled build now exercises
both consumers. The 128x64 row's baseline moved since the spike measured it
(the fixture's *unoptimized* leg no longer fits the 2048 B SRAM -- fba8fb0 sits
it out of the differential axes for that reason; the optimized build the table
measures still fits and runs) -- the -17.7% is a like-for-like delta on today's
sources, not comparable to the spike's -3.9%.

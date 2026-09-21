# RFC 0009: profile-guided optimisation (workload -> emulator -> profile -> optimizer)

- Status: **PROPOSED**, implemented on the `pgo-spike` spike branches of this repo and
  `pymcu-avr` as a feasibility proof. Everything below past the design sections is the
  measured result of that spike.
- Date: 2026-09-21
- Affects: `src/driver/core/workload.py` (new), `src/driver/commands/profile.py`,
  `src/driver/commands/build.py`, `src/driver/core/compiler.py`,
  `src/driver/backends/__init__.py`, `src/compiler/Common/Models/CompilerOptions.cs`,
  `src/compiler/Infrastructure/Cli/CompilerCliBuilder.cs`,
  `src/compiler/IR/PgoProfile.cs` (new), `src/compiler/Pipeline/Phases/IrGenerationPhase.cs`,
  `src/compiler/IR/Optimizer.cs`;
  pymcu-avr: `src/csharp/lib/Targets/AVR/AvrBlockMap.cs` (new),
  `src/csharp/lib/Targets/AVR/AvrCodeGen.cs`, `src/csharp/cli/Program.cs`,
  `src/csharp/profiler/{Program,Workload,PgoRunner}.cs`,
  `tests/integration/{PymcuCompiler,Differential/TraceComparison}.cs`,
  `tests/integration/Tests/AVR/PgoDifferentialTests.cs`,
  `tests/integration/fixtures/pgo-cold-outline/`.
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
5. There is exactly **one consumer**, in `Optimizer.OutlineInlineExpansions`: a region
   every site of which ran zero times under the workload is outlined even when the
   static word-cost model cannot prove the win, and a region whose enclosing block
   burns >= 1% of the workload's cycles is never outlined. Only a flipped decision is
   attributed to the profile (`__pymcu_outline_pgo_N` name, `pgo: kept N region(s)
   inline` DebugLine) -- a decision the cost model already made is not the profile's
   work and keeps the normal `__pymcu_outline_N` name.
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
   measured, not gated -- the spike's own consumer is allowed to trade bytes for
   correctness of the mechanism, and the measurement reports what it actually did.

## 1. Workload format

```yaml
scenarios:
  - name: idle
    run: {ms: 200}                       # exactly one of ms | cycles | until | until_uart_bytes
    stimuli:
      - {at_us: 1000, uart_rx: "A"}      # text, hex "41 42", or a byte list
      - {at_us: 5000, pin: PD2, level: 1}
      - {every_us: 20000, pin: PD3, toggle: true}
      - {responder: hc_sr04, trig: PD6, echo: PD5, distance_cm: 9.7}
    expect:
      uart_tx: "OK\r\n"                  # prefix match on emitted bytes
```

`run.until: break` and `run.until_uart_bytes` bound a run by an event instead of a
duration; `run.max_ms` (default 5000) is the safety cap so a broken scenario cannot
sim forever. `expect.uart_tx` turns a scenario into a test: a miss makes the profiler
exit 1 with the mismatch, so a workload can assert that the profiled run actually did
the thing it was declared to do.

Validation lives in the driver (`workload.py`): pin names must look like `P[BD][0-7]`,
a stimulus needs one payload (`uart_rx`, `pin`, `responder`) and one time (`at_us`,
`every_us`), `run` takes exactly one bound. Errors say `workload.yaml: <what>` and exit 1.

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
(params + 2)`). The profile participates at exactly that decision point, per group:

- `ClassifyGroupByProfile` computes each region's blocks as its own internal labels
  plus the label that falls through into it (the block that owns the branch check).
- `allCold` (every site at count 0) + statically rejected => outline anyway, named
  `__pymcu_outline_pgo_N`. The CALL it pays for is never executed; the size cost is
  the price of not leaving the dead shape inline.
- `anyHot` (enclosing block cycles >= 1% of `TotalCycles`) + statically accepted =>
  refuse, and record `pgo: kept N region(s) inline` as a `DebugLine` at the top of
  the function. Timing-critical loops keep their straight-line code.
- A veto that merely agrees with the cost model, or an override the model did not
  need, is not the profile's decision and gets no marker.

Measured interaction, all of it intended: the override fires only where the static
model said no, so on `pgo-cold-outline` it emits `__pymcu_outline_pgo_1` for a 2-site
x 2-word group (+2 B), and on `dsp-stress` taking an extra cold group early reorders
the fixpoint so a different group becomes profitable later (-16 B net). The profile
can shrink only through such second-order effects: an override by construction pays
more static words than it saves, so its direct size contribution is always >= 0.

## 5. The gate

`PgoDifferentialTests` runs the whole corpus through `BuildFixtureProfiled` /
`BuildProfiled` (which do `pymcu profile --pgo` then `PYMCU_PROFILE=... pymcu build` in
a scratch copy) and `BehaviorRecorder`-compares UART bytes, pin-change sequences and
BREAK checkpoints against the plain build under the same trace budget. A guard test
(`ProfileSwitch_ActuallyChangesTheEmittedImage`) compiles `pgo-cold-outline` both ways
and fails if the images are identical -- without it the axis could pass vacuously on a
profile that never reached the optimizer.

## 6. Measurements (the corpus run this RFC cites)

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

Read of the numbers: the loop closes end to end on real programs -- declared workload
-> emulator run -> profile -> different firmware -> identical behaviour -> measured
delta -- but one consumer that only ever flips *rejected* groups cannot net-win on
flash: it either adds bytes (cold override) or keeps them (hot veto). The value the
spike proves is the plumbing and the gate, not this consumer's ledger.

## 7. What the profile must never do

- Fail or block a build. Unreadable file, wrong format version, or zero shared label
  names all degrade to an ordinary build with a `pgo:` warning on stderr.
- Change observable behaviour: UART byte order/content, GPIO sequence, timing of any
  block the workload showed hot (the 1% veto exists for this), or the ABI.
- Reach the backend allocator directly. The `pymcuc-avr` binary never sees the
  profile; anything the backend is to do differently has to be encoded in the MIR
  first.

## 8. Not done / deliberately out of scope

- Second consumer: R2-R15 named-variable homes are already ordered by *static* IR use
  count (`AvrRegisterAllocator.Allocate`, `OrderByDescending(useCount)`). Swapping in
  dynamic counts needs the profile (or a per-name weight table) threaded through the
  MIR into `pymcuc-avr`, which has no profile input today. Read and rejected for this
  spike: the static proxy correlates with dynamic use on the surveyed corpus, the
  plumbing crosses a process boundary, and the one-consumer rule keeps the gate's
  signal clean.
- Runtime-helper attribution: hand-emitted runtime subs (`__dly_*`, `__div*`,
  interrupt stubs) have no block-map entries, so their cycles fold into the last
  preceding user block. Cycle *shares* stay approximately right (helpers serve the
  blocks around them) but a helper-heavy block can look hotter than it is.
- Per-scenario consumer input: the compiler sees one aggregated profile; a workload
  with both a hot and an idle scenario cannot yet say "cold under load only".
- Speedscope mode and PGO mode share the binary but not a code path; `--emit-profile`
  with neither `--blockmap` nor `--workload` is an error by design.

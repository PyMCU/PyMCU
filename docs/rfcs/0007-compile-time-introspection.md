# RFC 0007: compile-time introspection (`os.uname`, `sys.implementation`, `sys.platform`,
`os.getenv`, optional-module guards)

- Status: **PROPOSED**, implementation follows in this same branch.
- Date: 2026-09-15
- Affects: `extensions/pymcu-sdk/csharp/Models/DeviceConfig.cs`,
  `src/compiler/Common/Models/CompilerOptions.cs`, `src/compiler/Frontend/CompileTimeEvaluator.cs`,
  `src/compiler/Frontend/ConditionalImportExtractor.cs`, `src/compiler/Frontend/ConditionalCompilator.cs`,
  `src/driver/commands/build.py` (pass `--stdlib` through to `pymcuc`),
  `pymcu-circuitpython`: `src/pymcu_circuitpython/os.py` (new), `sys.py` (extended),
  `pymcu-micropython`: `src/pymcu_micropython/os.py` (new), `sys.py` (new).
- Closes: PyMCU#266 (dead-branch elimination admits only `__CHIP__` conditions).
- Builds on: the existing `__CHIP__` / `__FREQ__` compile-time-constant machinery
  (`CompileTimeEvaluator.Resolve`), the optional-import folding of #351/#367
  (`ConditionalImportExtractor`, `TryStmt.IsOptional`), and
  `[[feedback_shims_api_fidelity]]` (shim = upstream API, no invented attributes).

## 0. Decisions this RFC encodes

1. `sys`/`os`/`microcontroller` in the CircuitPython and MicroPython flavors are, from the
   compiler's point of view, **another pair of compile-time dunders**, resolved the same way
   `__CHIP__`/`__FREQ__` already are: a hardcoded table keyed by (stdlib flavor, chip, board),
   matched **syntactically** against the known attribute chains
   (`sys.implementation.name`, `sys.implementation.version[i]`, `sys.platform`,
   `uname().<field>` / `os.uname().<field>`). The compiler does **not** walk the shim
   module's own source to derive these values, for the same reason it does not walk
   `chips/__init__.py`'s `_ChipInfo` class body: the shim file exists for humans and IDEs,
   the compiler substitutes the real answer directly. Measured (`tests/unit`, this RFC):
   every use of these names in the surveyed libraries is inside an `if`/`match`/`try`
   condition, never bound to a variable and inspected later, so this narrower rule loses
   nothing observed.
2. Which flavor is active is a **build-wide fact**, exactly like `__CHIP__`: one project has
   at most one stdlib flavor (the driver already refuses two at once, `build.py:782`), so it
   is passed to `pymcuc` as a new `--stdlib {circuitpython,micropython}` option (empty when
   the project uses neither), not derived by sniffing which file an import resolved to.
3. **Values are the upstream string a real board of that generation would report**, not
   PyMCU's own internal simplified chip id and not the layer's own package version, on
   boards where an upstream port exists (`rp2040`, `rp2350` today). Two consequences worth
   naming because they read as inconsistencies and are not:
   - CircuitPython's `raspberry_pi_pico2` port reports `os.uname().sysname ==
     "rp2350a"` (the exact silicon variant), not PyMCU's own `__CHIP__.name == "rp2350"`
     (which folds every RP2350 stepping into one id). This layer reports `"rp2350a"`, because
     that is what a library comparing against it is actually testing for.
   - CircuitPython's own `sys.platform` (`"RP2040"`) and `os.uname().sysname`
     (`"rp2040"`) already disagree in case upstream. This is not cleaned up here; the
     shim reproduces the disagreement, because "fixing" it would be an invented
     improvement no board makes.
4. `sys.implementation.version` / `sys.version_info` report **the upstream API surface
   version this layer's own parity suite is pinned against**
   (`circuitpython-stubs>=10.3.1` -> `(10, 3, 1)`; `micropython-rp2-stubs>=1.29.0.post1` ->
   `(1, 29, 0)`), the same on every board that flavor supports. This is the layer's one
   substantive design call, argued in section 3.
5. Boards with **no upstream port** (every AVR part, CH32V003) get an **honest generic
   value**, stated as policy rather than invented per board: `sysname == __CHIP__.name`,
   `platform == __CHIP__.name`, `machine == "<board display name> with <chip>"`. Every
   `sysname == "rp2040"` / `"samd21"` / `"esp32"` / `"Linux"` guard in the survey takes its
   generic (false) branch there, which is correct: none of those parts is any of those
   things.
6. `os.getenv()` is **refused with a diagnostic**, not stubbed. Zero uses in the 20-library
   sample (section 1); `/settings.toml` is a filesystem read CircuitPython re-scans per call, and
   there is neither a filesystem nor a re-scan here. A constant-folded
   `[tool.pymcu.env]` table is left to a future RFC with a real driver behind it -- nothing
   measured today asks for it.
7. `microcontroller.cpu` needs no change. Zero uses of it (or `board.board_id`) in the
   sample; the existing `Processor` (`frequency`/`temperature`/`voltage`/`uid`/
   `reset_reason`) already covers what CircuitPython documents, faithfully, per
   `[[feedback_shims_api_fidelity]]`.
8. The `try: import X except ImportError: X = None` idiom needs **no change**: it already
   folds today (#351/#367), unconditionally of whether the guard is `__CHIP__` or
   `sys.implementation` flavored, because it is decided by whether the loader can find the
   module at all, not by evaluating a condition. PyMCU#266 is specifically about `if`
   conditions on `sys.implementation.name`; conflating the two would have meant re-solving
   an already-solved problem.

## 1. Survey: what the libraries actually read

Grepped the 20 vendored Adafruit CircuitPython libraries under the ada harness scratchpad
(`bmp280`, `busdevice`, `charlcd`, `debouncer`, `dht`, `ds18x20`, `hc595`, `hcsr04`, `ht16k33`,
`ina219`, `irremote`, `mcp3xxx`, `motor`, `neopixel`, `onewire`, `pcf8574`, `register`,
`seesaw`, `ssd1306`, `tcs34725`, `ticks`, `veml7700`) for every attribute this RFC's scope
names:

| symbol | hits | where | comparison value |
|---|---:|---|---|
| `os.uname` | 1 | `adafruit_dht.py:31,77` | `"Linux" not in uname()` (membership over the 5-tuple, not substring) |
| `sys.implementation` | 1 | `neopixel.py:126` | `sys.implementation.version[0] >= 7` |
| `sys.platform` | 0 | -- | -- |
| `sys.version` | 0 | -- | -- |
| `os.getenv` | 0 | -- | -- |
| `microcontroller.cpu` | 0 | -- | -- |
| `board.board_id` | 0 | -- | -- |
| `try/except ImportError` around a module | 22 | 16 files | see below, already handled (section 0.8) |

The `try/except ImportError` guards are all one of two shapes: typing-only imports
(`circuitpython_typing`, `typing_extensions`, `typing`) that resolve to nothing at runtime and
are already routed to `prog.TypingOnlyNames` by `ConditionalCompilator.ProcessStatement`, or a
real optional dependency (`busio`, `microcontroller`, `pulseio`, `micropython`) that the
loader already resolves per #351. None of the 22 needs this RFC's machinery; they are listed
to show the survey did not miss them, not because they are in scope.

`adafruit_requests` (not in the 20-library sample, but the library PyMCU#266 names) has the
`sys.implementation.name` guard the issue measured:

```python
if not sys.implementation.name == "circuitpython":
    from typing import Optional
    from types import TracebackType
```

## 2. Upstream shapes, read from source

### 2.1 `os.uname()`

Both `circuitpython/shared-bindings/os/__init__.c` and `micropython/extmod/modos.c` return a
5-field named tuple (`sysname`, `nodename`, `release`, `version`, `machine`), but they are
**not** built the same way -- this is the fidelity trap a naive shared implementation would
fall into:

| field | CircuitPython (`shared-bindings/os/__init__.c`) | MicroPython (`extmod/modos.c`) |
|---|---|---|
| `sysname` | `MICROPY_HW_MCU_NAME` (e.g. `"rp2040"`, `"rp2350a"`) | `MICROPY_PY_SYS_PLATFORM` (e.g. `"rp2"`, `"samd"`) |
| `nodename` | same as `sysname` | same as `sysname` |
| `release` | `MICROPY_VERSION_STRING` (the CircuitPython version) | `MICROPY_VERSION_STRING` (the MicroPython version) |
| `version` | `MICROPY_GIT_TAG " on " MICROPY_BUILD_DATE` | `MICROPY_GIT_TAG " on " MICROPY_BUILD_DATE` |
| `machine` | `MICROPY_HW_BOARD_NAME " with " MICROPY_HW_MCU_NAME` | `MICROPY_HW_BOARD_NAME " with " MICROPY_HW_MCU_NAME` |

CircuitPython's `sysname` is the MCU name; MicroPython's is the **port** short name, a
different concept that only coincides by accident on parts where the MCU name and the port
name are written the same. A shim that used `MICROPY_HW_MCU_NAME` for both, because "it's the
same field in both structs", would answer MicroPython's `sys.platform`-style guards
(`if sys.platform == "rp2":`, which MicroPython libraries write far more often than
`os.uname()` ones) with the wrong string.

Board data used (`ports/*/boards/*/mpconfigboard.h`, `ports/*/mpconfigport.h`):

| board | CircuitPython `MICROPY_HW_MCU_NAME` | CircuitPython `MICROPY_HW_BOARD_NAME` | MicroPython `MICROPY_HW_MCU_NAME` | MicroPython `MICROPY_HW_BOARD_NAME` | MicroPython `MICROPY_PY_SYS_PLATFORM` |
|---|---|---|---|---|---|
| Pico (RP2040) | `rp2040` | `Raspberry Pi Pico` | `RP2040` | `Raspberry Pi Pico` | `rp2` |
| Pico 2 (RP2350, Arm) | `rp2350a` | `Raspberry Pi Pico 2` | `RP2350` | `Raspberry Pi Pico2` | `rp2` |

(MicroPython's `mpconfigport.h` also defines `RP2350-RISCV` for the Hazard3 RISC-V build;
PyMCU's `rp2350` target is the Arm Cortex-M33 codegen, so the Arm string is the correct match.)

### 2.2 `sys.implementation`

Both are the same `MP_DEFINE_ATTRTUPLE` shape (`py/modsys.c`) with fields `name`, `version`,
`_machine`, and (build-dependent) `_mpy`/`_build`/`_thread`/`_v2`. CircuitPython's one-line
diff from upstream MicroPython is `MP_QSTR_circuitpython` in place of `MP_QSTR_micropython`
(`py/modsys.c`, `SYS_IMPLEMENTATION_ELEMS_BASE`) -- everything else about the object is
identical C. `version` is `(MICROPY_VERSION_MAJOR, MICROPY_VERSION_MINOR,
MICROPY_VERSION_MICRO, "" | "preview")`.

`sys.platform` (`MICROPY_PY_SYS_PLATFORM`) is `"RP2040"` / `"RP2350"` on CircuitPython's
`raspberrypi` port (`ports/raspberrypi/mpconfigport.h`) -- uppercase, and **not** the same
string as `os.uname().sysname` on the same port (section 0.3). On MicroPython's `rp2` port it is
`"rp2"` for both RP2040 and RP2350 (`ports/rp2/mpconfigport.h`), matching `os.uname().sysname`
there.

The underscore-prefixed fields (`_machine`, `_mpy`, `_build`, `_thread`) are private,
build-introspection detail no surveyed library reads; per section 0.1 (fold only what is read) they
are intentionally left out, the same call `pymcu_circuitpython/sys.py` already made for the
rest of `sys` (documented in that file today).

## 3. The version-tuple decision, argued

`neopixel.py:126` is the one library in the survey that reads `sys.implementation.version`,
and it does not read it as a Python-language-level fact -- it uses it as a **feature-detection
proxy**: "does this CircuitPython build know about `board.NEOPIXEL_POWER` /
`NEOPIXEL_POWER_INVERTED`", a feature that shipped in CircuitPython 7. Two candidate answers:

- **(a) the layer's own package version** (`pymcu-circuitpython` is `0.1.0a2` today). Simple,
  honest about identity, and **wrong for this call site forever**: `version[0] >= 7` never
  becomes true, because a 0.x layer version never reaches 7, so every board this layer ever
  ships stays on the pre-7 branch even where its `board.py` genuinely defines
  `NEOPIXEL_POWER` (Pico W's `board_chips.py`-mapped boards already do). A program built
  against this layer would silently diverge from the same program on a real CircuitPython 7+
  board -- the exact failure `[[feedback_shims_api_fidelity]]` exists to prevent.
- **(b) the upstream CircuitPython API-surface version this layer claims fidelity to.** The
  layer already makes this claim in one place, machine-checked: `pyproject.toml` pins
  `circuitpython-stubs>=10.3.1` and `tests/parity/report.py` diffs the layer's public surface
  against exactly that stub package. Reporting `(10, 3, 1)` is not a new claim; it is stating,
  in the one place a library asks, the version the parity suite already holds the layer to.
  `version[0] >= 7` reads `10 >= 7` and takes the same branch a real board does.

(b) is the decision. The version number is **not** meant to track `pymcu-circuitpython`'s own
releases automatically -- bumping it is a deliberate act tied to re-running the parity suite
against a newer stub package and re-auditing the API diff, not a side effect of a routine
`0.1.0a2` -> `0.1.0a3` bump. `pymcu-micropython` mirrors this against its own pin
(`micropython-rp2-stubs>=1.29.0.post1` -> `(1, 29, 0)`).

## 4. The compiler side

### 4.1 Why this is not full cross-module constant propagation

`__CHIP__` is already a precedent for "a name backed by a real stub file
(`chips/__init__.py`'s `_ChipInfo`) that the compiler never actually parses": `Resolve()` and
`EvaluateCondition()` in `CompileTimeEvaluator` pattern-match `VariableExpr { Name: "__CHIP__"
}` and its member accesses directly against `DeviceConfig`, and `tests/unit/Frontend/
ConditionalCompilatorTests.cs` confirms every existing use of `__CHIP__` is inside an `if` /
`match` / `try` condition -- never assigned to a variable and read back later. The 20-library
survey (section 1) shows `sys.implementation` and `os.uname()` used exactly the same way. Extending
`CompileTimeEvaluator` with more hardcoded, syntactically-matched attribute chains is there
fore the smaller, precedented change; building general "resolve `sys` to the file it imports,
walk that file's AST, evaluate `_Implementation.__init__`" constant propagation would be a
materially bigger and riskier feature to buy nothing more than what is already covered by
extending the existing mechanism the same way `__CHIP__` already works.

### 4.2 New surface

- `CompilerOptions.Stdlib` (new, default `""`) -- `"circuitpython"` or `"micropython"`,
  passed by `build.py` from the `stdlib_flavors` list it already computes and already
  refuses to have more than one of (`build.py:782`).
- `DeviceConfig.Stdlib` (new, default `""`) -- copied from `CompilerOptions.Stdlib` in
  `InitializationPhase`, next to `TargetChip`/`Board`/`Frequency`.
- A new static table (`IntrospectionTable`, `extensions/pymcu-sdk/csharp/Models/`) with one
  method per fact needed, keyed on `(config.Stdlib, config.Arch, config.Chip, config.Board)`:
  `Uname(config)` -> `(sysname, nodename, release, version, machine)`,
  `SysPlatform(config)` -> `string`, `ImplementationName(config)` -> `string`,
  `ImplementationVersion(config)` -> `(int, int, int)`. Empty `Stdlib` makes all four throw
  (there is no flavor, so `sys`/`os` do not resolve to one of these shims at all -- an
  ordinary "module not found" is the correct failure, not a silent wrong answer).
- `CompileTimeEvaluator.Resolve` / `EvaluateCondition` / `TryResolveNumber` grow cases for:
  `MemberAccessExpr(MemberAccessExpr(Var("sys"), "implementation"), "name")`,
  `IndexExpr(MemberAccessExpr(MemberAccessExpr(Var("sys"), "implementation"), "version"),
  IntegerLiteral)`, `MemberAccessExpr(Var("sys"), "platform")`, and a call-result member chain
  for `uname()` / `os.uname()`: `MemberAccessExpr(CallExpr(Var("uname") | MemberAccessExpr(
  Var("os"), "uname"), []), field)`.
- `BinaryOp.In` / `BinaryOp.NotIn` against a `CallExpr` resolving to the `uname()` 5-tuple:
  `"Linux" not in uname()` evaluates as membership over the 5 resolved strings, matching
  Python's own tuple-membership semantics (this is genuinely new to the evaluator -- today it
  only knows `Equal`/`NotEqual`/relational ops -- and is the one piece with no `__CHIP__`
  precedent, needed because `adafruit_dht.py` is written exactly this way rather than as
  `uname().sysname == "Linux"`).
- `ConditionalImportExtractor.ChooseBranch` needs no change: it already calls
  `eval.EvaluateCondition`, so extending the evaluator is sufficient for #266's dead-branch
  case (`if not sys.implementation.name == "circuitpython": import typing`) to fold the same
  way `if __CHIP__.arch != "avr":` already does.

### 4.3 Per-board table (implementation-ready)

| stdlib | chip | board | `uname().sysname` | `.nodename` | `.machine` | `sys.platform` | `.release` |
|---|---|---|---|---|---|---|---|
| circuitpython | rp2040 | `raspberry_pi_pico` / `pico` | `rp2040` | `rp2040` | `Raspberry Pi Pico with rp2040` | `RP2040` | `10.3.1` |
| circuitpython | rp2350 | `raspberry_pi_pico2` / `pico2` (+ `_w` variants) | `rp2350a` | `rp2350a` | `Raspberry Pi Pico 2 with rp2350a` | `RP2350` | `10.3.1` |
| circuitpython | avr (any) | any / none | `__CHIP__.name` | same | `<board display name or __CHIP__.board> with <__CHIP__.name>` | `__CHIP__.name` | `10.3.1` |
| circuitpython | riscv (ch32v003) | any / none | `__CHIP__.name` | same | same pattern | `__CHIP__.name` | `10.3.1` |
| micropython | rp2040 | `raspberry_pi_pico` / `pico` | `rp2` | `rp2` | `Raspberry Pi Pico with RP2040` | `rp2` | `1.29.0` |
| micropython | rp2350 | `raspberry_pi_pico2` / `pico2` (+ `_w`) | `rp2` | `rp2` | `Raspberry Pi Pico2 with RP2350` | `rp2` | `1.29.0` |
| micropython | avr (any) | any / none | `__CHIP__.name` | same | same pattern | `__CHIP__.name` | `1.29.0` |
| micropython | riscv (ch32v003) | any / none | `__CHIP__.name` | same | same pattern | `__CHIP__.name` | `1.29.0` |

`.version` (the free-text build stamp, `"<git tag> on <date>"` upstream) is not a compile-time
fact any surveyed library reads (section 1: zero hits), so it is not modeled as a board fact at all;
`os.uname().version` resolves to a fixed, honestly-labelled string
(`"PyMCU <compiler version>"`) rather than a fabricated git tag and build date.
`sys.implementation.version` is `(10, 3, 1)` / `(1, 29, 0)` on every board of that flavor
(section 3), independent of chip.

## 5. `#266` disposition

Closed by section 4: the missing admissible condition was `sys.implementation.name` (and, by the
same mechanism, `sys.platform`), not the `try/except ImportError` idiom (already generic,
section 0.8). The issue's own measurement -- `adafruit_requests`'s guard has no `else` and the total
compiling count does not move -- still holds; this closes the general gap `sys.implementation`
guards, `adafruit_dht`'s `os.uname` guard included.

## 6. Test plan

- `tests/unit/Frontend/CompileTimeEvaluatorTests.cs` (new cases alongside the existing
  `__CHIP__` ones): `sys.implementation.name`, `.version[0]`, `sys.platform`,
  `"Linux" not in uname()`, one per `(stdlib, chip)` pair in section 4.3, plus the empty-`Stdlib`
  throw.
- `tests/unit/Frontend/ConditionalCompilatorTests.cs`: the `#266` shape end to end -- the dead
  branch's import is gone from `prog.Imports` after folding, both for `if
  sys.implementation.name == ...` and `if not sys.implementation.name == ...`.
- `pytest tests/driver`, both front ends (`PYMCU_PY_PARSER=0/1`): a fixture program per board
  in section 4.3 printing `os.uname()` and `sys.implementation.name`/`.version`/`sys.platform`,
  diffed against the table.
- `pymcu-circuitpython/tests/parity`, `pymcu-micropython/tests/parity`: extend to cover `os`
  and the newly-covered `sys` attributes against `circuitpython-stubs` /
  `micropython-rp2-stubs` (shape/name parity -- the stub can't know PyMCU's board values, so
  parity here means "same field names, same types", not value equality).
  `pymcu-avr` fixtures: one program per section 4.3 AVR row that `if`-branches on
  `sys.implementation.name` / `os.uname().sysname` and asserts (by absence of the dead
  branch's marker string in the `.hex`, per the existing `#266`-style measurement) that the
  branch not taken is gone from the image.

## 7. `adafruit_dht` re-measurement

With `os.uname` provided, `adafruit_dht.py:31`'s `from os import uname` resolves and
`"Linux" not in uname()` folds to a constant per section 4.3 (always `True` on every PyMCU board,
since none reports `"Linux"` in any of the 5 fields -- matching every real embedded
CircuitPython board, only Blinka-on-Linux ever takes the other branch). `pulseio` already
exists in this layer for every arch (`pymcu_circuitpython/pulseio.py`, HAL-backed, no
per-arch gap), so `DHT11(pin)`'s default `use_pulseio=_USE_PULSEIO=True` keeps the
`self._use_pulseio` branch and the `"Linux" not in uname() and not self._use_pulseio` guard
never raises. The next blocker, if any, is measured in the final report against the ada
harness rather than predicted here.

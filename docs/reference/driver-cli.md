# Driver CLI — `pymcu`

The `pymcu` command-line tool manages PyMCU projects: creation, building, and flashing.

---

## `pymcu new <project_name>`

Creates a new PyMCU project.

```bash
pymcu new my_project
```

**Interactive prompts:**

- Target microcontroller (ATmega328P, PIC16F84A, etc.)
- Package manager (`uv`, `poetry`, or `pip`)

**Generated files:**

| File | Contents |
|---|---|
| `src/main.py` | Starter firmware with a blink template |
| `pyproject.toml` | Project config with `[tool.pymcu]` section |
| `.vscode/tasks.json` | VS Code Build / Flash tasks |
| `.gitignore` | Git ignore rules |

### AVR pyproject.toml

```toml
[tool.pymcu]
chip      = "atmega328p"
frequency = 16000000

[tool.pymcu.flash]
programmer = "avrdude"
```

### PIC pyproject.toml

```toml
[tool.pymcu]
chip      = "pic16f84a"
frequency = 4000000

[tool.pymcu.toolchain]
name = "gputils"

[tool.pymcu.flash]
programmer = "pk2cmd"
```

---

## `pymcu build`

Compiles the project to an Intel HEX file.

```bash
pymcu build
pymcu build -v    # verbose — prints assembler output and full build log
```

**Output files:**

| File | Description |
|---|---|
| `dist/firmware.hex` | Intel HEX — flash this to the MCU |
| `dist/firmware.asm` | AVR assembly listing with source annotations |
| `dist/firmware.mir` | Mid-level IR (useful for debugging code-gen issues) |

**Requirements:**

- Valid `pyproject.toml` in the project root
- All dependencies installed (`uv sync` or `pip install pymcu-compiler`)

### Compiler error output

When the compiler detects an error, it prints a human-readable diagnostic with source
context, line numbers, and a `^~~` underline pointing to the offending token:

```
src/main.py:12:9: error: TypeError: cannot assign float to uint8
11 | count: uint8 = 0
12 | count = 3.14
         ^~~~
13 | led.toggle()
```

- The **header line** (`file:line:col: severity: ErrorType: message`) matches the VS Code
  problem matcher pattern — errors appear inline in the editor.
- Lines N−1 and N+1 are shown as context (dimmed).
- The `^` points to the start of the token; `~` spans the rest of its length.
- **No caret is drawn when the compiler does not know the column.** Many checks run long
  after parsing and know only which statement is at fault, not which character. Those
  diagnostics print the header and the source context and stop, rather than aim the caret
  at column 1: an arrow under a character is read as a claim about that character, and a
  wrong one costs the reader more than its absence. The header keeps a column field in
  every case, because the editor integrations require one to match.
- Where the line is indented with tabs, the caret line is padded with the same tabs, so it
  lands under the right character whatever tab width the terminal uses.
- All formatting uses ANSI colour when stderr is a TTY (red header and underline, dim
  context lines). Plain text is output when stderr is redirected (e.g. in CI logs).

---

## `pymcu flash`

Uploads the firmware built by `pymcu build` to the connected device.

```bash
pymcu flash
pymcu flash --port /dev/cu.usbmodem*    # macOS
pymcu flash --port /dev/ttyACM0         # Linux
pymcu flash --port COM3                 # Windows
```

### Firmware artifact

The file uploaded depends on the target, and must exist before flashing:

| Target | Artifact |
|---|---|
| AVR, PIC | `dist/firmware.hex` |
| RP2040, RP2350 | `dist/firmware.uf2` (packed by the build), falling back to `dist/firmware.bin` |

### Supported programmers

The programmer defaults to the one for the target family (`avrdude` for AVR,
`pk2cmd` for PIC, `rp2040` for the RP boards) and can be overridden. For PIC,
`pk2cmd` drives a PICkit 2 and `ipecmd` drives a PICkit 3:

**AVR (Arduino Uno):**

```toml
[tool.pymcu.flash]
programmer = "avrdude"
port       = "/dev/cu.usbmodem14101"   # optional; --port overrides it
baud       = 115200                    # optional
```

**PIC (PICkit 2):**

```toml
[tool.pymcu.flash]
programmer = "pk2cmd"    # auto-downloaded on first use
```

**PIC (PICkit 3):**

```toml
[tool.pymcu.flash]
programmer = "ipecmd"          # MPLAB X IPE command line
# ipecmd_power = "5.0"         # only if the PICkit powers the target board
```

`ipecmd` ships with MPLAB X. Install **v6.20 or older** — v6.25 dropped PICkit 3
support, so the latest MPLAB X cannot talk to it. PyMCU looks for `ipecmd` in
`PYMCU_IPECMD`, then on `PATH`, then inside the MPLAB X installations it can
find, preferring versions that still support the PICkit 3. Omit `ipecmd_power`
when the board has its own supply.

**Raspberry Pi Pico / Pico 2:**

```toml
[tool.pymcu.flash]
programmer = "rp2040"    # picotool, or UF2 drag-and-drop to the RPI-RP2 volume
```

:::{note}
`[tool.pymcu.programmer]` with a `name` key is the pre-0.15 spelling. It is still
honoured as a fallback, with a deprecation warning — move it to
`[tool.pymcu.flash]`.
:::

---

## `pymcu clean`

Removes the `dist/` directory and all build artifacts.

```bash
pymcu clean
```

---

## `pymcu install <library>`

Installs a third-party library into this project. Names are resolved against the curated
PyMCU index, not against PyPI at large: PyPI is full of Python that cannot compile for a
microcontroller, and an install that succeeds and then breaks the build is worse than one
that refuses.

```bash
pymcu install dht11
```

What it does, in order:

1. resolves the name in the index (cached under `~/.pymcu/`, `--refresh` to update);
2. refuses, **before downloading anything**, if the measured matrix says the library does
   not build for this project's chip, if it belongs to a compat layer this project does
   not declare, or if it needs a newer language level;
3. installs into the project's `.venv` with `uv` or `pip` — never globally;
4. re-checks the manifest that actually landed on disk, and with `--verify` (the default
   when the library ships an example) compiles that example for this chip;
5. records the dependency in `pyproject.toml`.

Anything that fails after step 3 rolls the installation back, so a refused library leaves
neither files nor a dependency line behind.

| Option | Effect |
|---|---|
| `--from-pypi` | Skip the index and install this distribution directly. The manifest is still required. |
| `--no-verify` | Skip the verification build. |
| `--refresh` | Re-download the index before resolving. |
| `--no-pre` | Exclude pre-release versions. |

## `pymcu uninstall <library>`

Removes the library from the project's environment and from `pyproject.toml`.

## `pymcu libraries`

Lists the libraries installed in this project, with a verdict for the current chip.
`--all` also shows the ones that do not apply to it, and why.

## `pymcu search [text]`

Searches the index. Without `--all`, only libraries usable on this project's chip are
listed.

---

## `pymcu lint --library <package_dir>`

Checks a library package before publication: manifest validity, ASCII-only sources
(non-ASCII inside a string is an error — the lexer accepts it and then encodes it as
ASCII, corrupting the byte), a `match __CHIP__.arch` whose default branch raises instead
of returning a sentinel, and the public API surface against `api-surface.lock`.

```bash
pymcu lint --library src/pymcu_lib_dht11                   # check
pymcu lint --library src/pymcu_lib_dht11 --write-surface   # record the surface
```

The surface check is what catches a package growing a public function without its version
moving — two different wheels shipping under one version number.

See {doc}`../library/authoring` for the full authoring guide.

---

## `pymcu index` (index maintainers)

Builds the curated index by **compiling**, not by reading declarations. Run by the CI of
the `pymcu-libraries` repository; useful locally when working on the index itself.

```bash
pymcu index build --from libraries.txt --output index.json   # install, measure, write
pymcu index verify --venv .venv                              # measure what is installed
```

Each library's example is compiled for one chip per architecture — including architectures
it does not declare, because "does not build there" is exactly what the index has to be
able to state, and because a library that builds somewhere it never claimed means nobody
is maintaining its `supports.arch`. The build runs with `PYMCU_LIBRARY_FILTER=0` so the
usual compatibility filter does not pre-empt the compiler; filtering first would only
measure the manifest.

`--strict` exits non-zero when a measurement contradicts a manifest, which is what turns
the submission check into a gate.

---

## `pymcu profile`

Compiles the project, assembles it, simulates it with the cycle-accurate AVR simulator,
and writes a [Speedscope](https://speedscope.app) flamegraph JSON.

```bash
pymcu profile                              # simulate 100 ms, write profile.speedscope.json
pymcu profile --ms 500                     # simulate 500 ms
pymcu profile --cycles 800000              # simulate exactly 800,000 cycles
pymcu profile -o my_run.speedscope.json   # custom output path
pymcu profile --open                       # open speedscope.app in the browser afterwards
pymcu profile --freq 8000000              # override clock (e.g. 8 MHz Lilypad)
pymcu profile --assert-cycles-lt 50000    # fail with exit code 1 if ≥ 50,000 cycles
pymcu profile -v                           # verbose build + simulation output
```

**Output files:**

| File | Description |
|---|---|
| `profile.speedscope.json` | Speedscope evented flamegraph — drag to [speedscope.app](https://speedscope.app) |
| `dist/firmware.symbols.json` | Symbol map used to annotate frames (auto-generated) |

**Options:**

| Flag | Default | Description |
|---|---|---|
| `--cycles N` | — | Simulate exactly N clock cycles |
| `--ms N` | 100 | Simulate N milliseconds of firmware execution |
| `-o PATH` | `profile.speedscope.json` | Output JSON path |
| `--open` | off | Open [speedscope.app](https://speedscope.app) in the browser after profiling |
| `--freq HZ` | from `pyproject.toml` | Override the clock frequency used for cycle→ms conversion |
| `--assert-cycles-lt N` | — | Exit with code 1 if total simulated cycles ≥ N (CI regression guard) |
| `-v` / `--verbose` | off | Show full build and profiler output |

:::{note}
`--cycles` and `--ms` are mutually exclusive. If neither is provided, the profiler
simulates 100 ms by default.
:::

**CI example — enforce a cycle budget:**

```yaml
# .github/workflows/ci.yml
- name: Profile and check cycle budget
  run: pymcu profile --assert-cycles-lt 200000
```

---

## `pymcu bench`

Like `pymcu profile`, but instead of writing a flamegraph file it prints a Rich table of
**per-function cycle statistics** directly to the terminal. Useful for quick performance
investigations without opening an external tool.

```bash
pymcu bench                  # simulate 100 ms, show all functions
pymcu bench --ms 500         # simulate 500 ms
pymcu bench --top 10         # show only the top 10 hottest functions
pymcu bench --cycles 100000  # simulate exactly 100,000 cycles
pymcu bench --freq 8000000   # override clock frequency
pymcu bench -v               # verbose build + simulation output
```

**Example output:**

```
Simulated 100.0 ms  (1,600,000 cycles @ 16 MHz)
┌──────────────────────┬───────┬────────┬────────┬──────────┬────────┐
│ Function             │ Calls │   Self │  Self% │ Avg/call │  Incl% │
├──────────────────────┼───────┼────────┼────────┼──────────┼────────┤
│ crc8_step            │  2048 │ 850.2k │  53.1% │   3.2k   │  53.1% │
│ compute_checksum     │     8 │ 200.1k │  12.5% │ 131.3k   │  65.6% │
│ main                 │     1 │  40.0k │   2.5% │   1.6M   │ 100.0% │
│ delay_ms             │     8 │ 510.0k │  31.9% │  63.8k   │  31.9% │
└──────────────────────┴───────┴────────┴────────┴──────────┴────────┘
```

`Self%` colours: **red** ≥ 30%, **yellow** ≥ 10%.

**Options:**

| Flag | Default | Description |
|---|---|---|
| `--cycles N` | — | Simulate exactly N clock cycles |
| `--ms N` | 100 | Simulate N milliseconds |
| `--freq HZ` | from `pyproject.toml` | Override clock frequency |
| `--top N` | 0 (all) | Limit output to the top N functions by self-time |
| `-v` / `--verbose` | off | Show full build and profiler output |

**Column definitions:**

| Column | Meaning |
|---|---|
| **Calls** | Number of times the function was called during simulation |
| **Self** | Cycles spent *inside* this function, excluding callees |
| **Self%** | Self cycles as a percentage of total simulation cycles |
| **Avg/call** | Average *inclusive* cycles per call (includes callees) |
| **Incl%** | Total inclusive cycles as a percentage of total cycles |

---

## Toolchain auto-detection

PyMCU auto-detects and configures the appropriate toolchain for the selected chip:

- **AVR:** Uses the built-in PyMCU AVR backend (no external assembler required)
- **PIC14/14E:** Uses `gputils` (auto-detected from PATH)

---

## C/C++ interop configuration

```toml
[tool.pymcu.ffi]
sources      = ["src/sensor.c", "src/ArduinoLib.cpp"]
include_dirs = ["src/include"]
cflags       = ["-O2"]
```

C sources use `avr-gcc`. C++ sources (`.cpp`, `.cc`, `.cxx`) use `avr-g++` with
`-fno-exceptions -fno-rtti`, enabling use of Arduino libraries from PyMCU firmware.

---

## Troubleshooting

**"Command not found":**

```bash
uv tool install pymcu-compiler    # install via uv (recommended)
# — or —
pipx install pymcu-compiler       # install via pipx
pipx ensurepath                   # add pipx bin to PATH
source ~/.zshrc                   # reload shell config
```

**"avrdude: stk500_recv(): programmer is not responding":**

- Check `--port` matches your Arduino's serial device
- macOS: `/dev/cu.usbmodem*` (note: `cu.` not `tty.`)
- Linux: `/dev/ttyACM0` or `/dev/ttyUSB0`; add user to `dialout` group: `sudo usermod -a -G dialout $USER`

**Build errors:**

Run `uv sync` (or `pip install pymcu-compiler`) to ensure all dependencies are installed.

**"pymcuc-avr-profiler not found" (profile / bench):**

The profiler binary ships with `pymcu-compiler` starting from v0.12. If you are running
from source, build it manually:

```bash
dotnet publish extensions/pymcu-avr/src/csharp/profiler/ \
    -c Release -o build/bin --nologo
```

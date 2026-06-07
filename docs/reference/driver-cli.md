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

[tool.pymcu.programmer]
name     = "avrdude"
protocol = "arduino"
baudrate = 115200
```

### PIC pyproject.toml

```toml
[tool.pymcu]
chip      = "pic16f84a"
frequency = 4000000

[tool.pymcu.toolchain]
name = "gputils"

[tool.pymcu.programmer]
name = "pickit2"
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
- All formatting uses ANSI colour when stderr is a TTY (red header and underline, dim
  context lines). Plain text is output when stderr is redirected (e.g. in CI logs).

---

## `pymcu flash`

Uploads `dist/firmware.hex` to the connected device.

```bash
pymcu flash
pymcu flash --port /dev/cu.usbmodem*    # macOS
pymcu flash --port /dev/ttyACM0         # Linux
pymcu flash --port COM3                 # Windows
```

### Supported programmers

**AVR (Arduino Uno):**

```toml
[tool.pymcu.programmer]
name     = "avrdude"
protocol = "arduino"
baudrate = 115200
```

**PIC (PICKit 2):**

```toml
[tool.pymcu.programmer]
name = "pickit2"    # pk2cmd is auto-downloaded on first use
```

---

## `pymcu clean`

Removes the `dist/` directory and all build artifacts.

```bash
pymcu clean
```

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

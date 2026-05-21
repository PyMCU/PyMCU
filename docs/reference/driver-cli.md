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
```

**Output files:**

| File | Description |
|---|---|
| `dist/firmware.hex` | Intel HEX — flash this to the MCU |
| `dist/firmware.lst` | Assembly listing with source annotations |
| `dist/firmware.cod` | Debug symbol file |

**Requirements:**

- Valid `pyproject.toml` in the project root
- All dependencies installed (`uv sync` or `pip install -r requirements.txt`)

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
pipx ensurepath    # add pipx bin to PATH
source ~/.zshrc    # reload shell config
```

**"avrdude: stk500_recv(): programmer is not responding":**

- Check `--port` matches your Arduino's serial device
- macOS: `/dev/cu.usbmodem*` (note: `cu.` not `tty.`)
- Linux: `/dev/ttyACM0` or `/dev/ttyUSB0`; add user to `dialout` group: `sudo usermod -a -G dialout $USER`

**Build errors:**

Run `uv sync` (or `pip install -r requirements.txt`) to ensure all dependencies are installed.

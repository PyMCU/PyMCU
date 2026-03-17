# PyMCU Driver Documentation

The `pymcu` driver is the command-line interface (CLI) for managing PyMCU projects. It handles project creation, dependency management interaction, building, and flashing firmware.

## Commands

### `pymcu new <project_name>`

Creates a new PyMCU project with the specified name.

**Usage:**
```bash
pymcu new my_blinking_led
```

**Features:**
- **Interactive Chip Selection:** Prompts you to select the target microcontroller from the list of supported chips.
- **Package Manager Integration:** Supports `uv`, `poetry`, and `pip`.
- **Scaffolding:** Generates a project structure with:
    - `src/main.py` (or `app.py`) with a basic template.
    - `pyproject.toml` (or `requirements.txt`) with correct dependencies.
    - `.vscode/tasks.json` for VS Code integration.
    - `.gitignore` for version control.

**Configuration:**
The generated `pyproject.toml` includes a `[tool.pymcu]` section:

**For PIC microcontrollers:**
```toml
[tool.pymcu]
chip = "pic16f84a"
frequency = 4000000

[tool.pymcu.config]
# Configuration bits can be added here
# FOSC = "HS"

[tool.pymcu.toolchain]
name = "gputils" # Auto-detected toolchain

[tool.pymcu.programmer]
name = "pickit2" # Default programmer
```

**For AVR (Arduino Uno):**
```toml
[tool.pymcu]
chip = "atmega328p"
frequency = 16000000

[tool.pymcu.programmer]
name = "avrdude"
protocol = "arduino"
baudrate = 115200
```

### `pymcu build`

Compiles the project's source code into a Intel HEX file.

**Output:**
- `dist/firmware.hex`
- `dist/firmware.cod` (Debug symbols)
- `dist/firmware.lst` (Assembly listing)

**Requirements:**
- A valid `pyproject.toml` in the project root.
- All dependencies installed (e.g., via `uv sync` or `pip install -r requirements.txt`).

### `pymcu flash`

Flashes the built firmware to the target device using the configured programmer.

**Usage:**
```bash
pymcu flash                              # Use default programmer and port
pymcu flash --port /dev/cu.usbmodem*     # Specify port (AVR/Arduino)
pymcu flash --port /dev/ttyUSB0          # Linux serial port
```

**Requirements:**
- A successful build (`dist/firmware.hex` must exist).
- A connected programmer (e.g., PICKit 2 for PIC, USB-serial adapter for AVR).
- The correct programmer configured in `pyproject.toml` under `[tool.pymcu.programmer]`.

**Supported Programmers:**

**AVR (Arduino Uno, ATmega328P):**
- `avrdude` - Uses the `avrdude` tool with Arduino bootloader
  ```toml
  [tool.pymcu.programmer]
  name = "avrdude"
  protocol = "arduino"
  baudrate = 115200
  ```

**PIC14/PIC14E:**
- `pickit2` (via `pk2cmd`) - Automatically downloaded if not present
  ```toml
  [tool.pymcu.programmer]
  name = "pickit2"
  ```

### `pymcu clean`

Removes the `dist/` directory and cleans up build artifacts.

## Toolchain Management

PyMCU attempts to auto-detect and configure the appropriate toolchain (compiler/assembler backend) for the selected chip:

- **AVR**: Uses the built-in AVR backend (no external assembler required)
- **PIC14/PIC14E**: Uses `gputils` for assembly (auto-detected)

## Troubleshooting

### General
- **"Command not found"**: Ensure `pipx` bin directory is in your PATH.
- **Build errors**: Check that all dependencies are installed (`uv sync` or `pip install -r requirements.txt`).

### AVR / Arduino Uno
- **"Programmer not found"**: Install `avrdude` via your package manager:
  - macOS: `brew install avrdude`
  - Linux: `sudo apt-get install avrdude`
  - Windows: Download from [AVRDUDE website](https://github.com/avrdudes/avrdude/releases)
- **"Permission denied" on /dev/tty*****: Add your user to the `dialout` group (Linux) or use `sudo`
- **"Device not found"**: Check the `--port` flag matches your Arduino's serial port
  - macOS: `/dev/cu.usbmodem*` or `/dev/cu.usbserial*`
  - Linux: `/dev/ttyUSB0` or `/dev/ttyACM0`
  - Windows: `COM3`, `COM4`, etc.

### PIC
- **"Programmer not found"**: Run `pymcu flash` and follow the prompts to install the required tools (e.g., `pk2cmd`).

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

**Requirements:**
- A successful build (`dist/firmware.hex` must exist).
- A connected programmer (e.g., PICKit 2).
- The correct programmer configured in `pyproject.toml` under `[tool.pymcu.programmer]`.

**Supported Programmers:**
- `pickit2` (via `pk2cmd`) - Automatically downloaded if not present.

### `pymcu clean`

Removes the `dist/` directory and cleans up build artifacts.

## Toolchain Management

PyMCU attempts to auto-detect and configure the appropriate toolchain (compiler/assembler backend) for the selected chip. Currently, it defaults to using `gputils` for PIC14/PIC14E devices.

## Troubleshooting

- **"Command not found"**: Ensure `pipx` bin directory is in your PATH.
- **"Programmer not found"**: Run `pymcu flash` and follow the prompts to install the required tools (e.g., `pk2cmd`).

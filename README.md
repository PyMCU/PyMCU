# PyMCU: Python-to-MCU Compiler

PyMCU is a modern toolchain for programming 8-bit microcontrollers using a statically-typed subset of Python. It compiles directly to bare-metal machine code with zero runtime overhead — no heap, no interpreter. Currently supports AVR (ATmega328P/Arduino Uno), PIC14/PIC14E, PIC18, and experimental RISC-V targets.

## Installation

PyMCU is best installed using `pipx` to keep it isolated from your system Python:

```bash
pipx install pymcu-compiler
```

Ensure `pipx` is installed and its bin directory is in your PATH.

## Usage

### Creating a New Project

The `pymcu` driver includes a scaffolding tool to set up a new project with the correct structure and configuration.

```bash
pymcu new my_project
```

This interactive command will guide you through:
- Selecting the target microcontroller.
- Choosing a package manager (`uv`, `poetry`, or `pip`).
- Setting up the project layout.

### Building Firmware

Navigate to your project directory and run:

```bash
pymcu build
```

This will compile your Python code in `src/` (or the root) and generate a `.hex` file in the `dist/` directory.

### Flashing Firmware

To flash the generated firmware to your device:

```bash
pymcu flash
```

This uses the programmer configured in your `pyproject.toml` (defaulting to `pk2cmd` for PICKit 2).

### Cleaning Artifacts

To remove build artifacts:

```bash
pymcu clean
```

## Documentation

- **[Full Documentation](https://pymcu.dev)** - Complete language reference, stdlib API, examples, and migration guides
- [Driver CLI](docs/docs/driver.md) - Command-line interface reference
- [Language Reference](docs/docs/language-reference.md) - Complete syntax and type system documentation
- [Language Roadmap](LANGUAGE_ROADMAP.md) - Feature status and development roadmap
- [CircuitPython Migration](docs/docs/migration/from-circuitpython.md) - Port CircuitPython code to PyMCU
- [Contributing](CONTRIBUTING.md) - Guidelines for contributing to PyMCU

## License

[MIT License](LICENSE)

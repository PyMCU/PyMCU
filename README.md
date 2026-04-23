# PyMCU: Python-to-MCU Compiler

PyMCU is a modern toolchain for programming 8-bit microcontrollers using a statically-typed subset of Python. It compiles directly to bare-metal machine code with zero runtime overhead — no heap, no interpreter. Currently supports AVR (ATmega48/88/168/328 family, ATtiny25/45/85, ATtiny24/44/84, ATtiny2313/4313, ATtiny13/13a), PIC14/PIC14E, PIC18, and experimental RISC-V targets.

## Licensing

| Component | License |
|-----------|---------|
| Compiler, CLI driver & extensions (`src/`, `extensions/`) | [MIT](LICENSE) |
| Standard library (`lib/`) | [MIT](lib/LICENSE) |
| Your compiled firmware (output) | Yours — no restrictions |

The entire PyMCU toolchain is MIT-licensed. Use it freely in open-source and commercial
projects alike. The firmware you build with PyMCU belongs entirely to you.

## Installation

PyMCU is best installed using `pipx` to keep it isolated from your system Python:

```bash
pipx install pymcu
```

Ensure `pipx` is installed and its bin directory is in your PATH.

## Usage

### Creating a New Project

The `pymcu` CLI includes a scaffolding tool to set up a new project with the correct structure and configuration.

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

This uses the programmer configured in your `pyproject.toml` (uses avrdude for AVR targets).

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

## Credits

Special thanks to Richard Wardlow, creator of the original [pyMCU](https://github.com/rwardlow/pyMCU) project (2012).
See [CREDITS.md](CREDITS.md) for the full acknowledgement.

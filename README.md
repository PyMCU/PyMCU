# PyMCU: Python-to-MCU Compiler

PyMCU is a modern toolchain for programming 8-bit microcontrollers (currently PIC14/PIC14E) using a subset of Python. It aims to provide a developer-friendly experience with familiar Python syntax while generating efficient assembly code.

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

- [Driver Documentation](docs/DRIVER.md) - Detailed guide on the CLI driver and configuration.
- [Contributing](CONTRIBUTING.md) - Guidelines for contributing to PyMCU.

## License

[MIT License](LICENSE)

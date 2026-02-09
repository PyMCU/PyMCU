# PyMCU Lib

This repository contains the Python stub files for **PyMCU**, a Python-to-microcontroller toolchain. 

These files provide type definitions, hardware register mappings, and phantom types that allow Python IDEs (like PyCharm or VS Code) to provide autocompletion and type checking for microcontroller code, while being compatible with the `pymcuc` compiler.

## Purpose

Microcontroller code written for PyMCU uses specialized types and access patterns that are not native to standard Python. These stubs bridge that gap by:
1.  **Defining Hardware Registers**: Mapping register names (e.g., `PORTB`, `TRISA`) to their physical memory addresses.
2.  **Providing Type Safety**: Using phantom types like `uint8` and `int8` to ensure correct data sizing.
3.  **Enabling IDE Features**: Allowing developers to use "Go to Definition", autocompletion, and static analysis while writing firmware in Python.

## Structure

- `src/pymcu/types.py`: Core type definitions including `ptr[T]` for memory-mapped I/O and fixed-width integer types.
- `src/pymcu/chips/`: Chip-specific register definitions (e.g., `pic16f877a.py`).

## Usage

These stubs are intended to be used as a dependency in your PyMCU projects.

```python
from pymcu.chips.pic16f877a import TRISB, PORTB, RB0
from pymcu.types import uint8

# IDE will recognize these and provide autocompletion
TRISB[RB0] = 0  # Set RB0 as output
PORTB[RB0] = 1  # Set RB0 high
```

> **Note**: These files are stubs. Running them directly with a standard Python interpreter will result in `RuntimeError` if you attempt to access hardware registers, as they lack the physical hardware interface. They must be compiled with the `pymcuc` toolchain to run on a microcontroller.

## Installation

If you are using Poetry:

```bash
poetry add git+https://github.com/your-username/pymcu-lib.git
```

Or via pip:

```bash
pip install git+https://github.com/your-username/pymcu-lib.git
```

## License

Refer to the main PyMCU project for licensing information.

# pymcu-toolchain-pic

PIC toolchain plugin for PyMCU. Provides the GNU PIC Utilities (gpasm/gplink) for assembling PyMCU firmware for PIC targets.

## Installation

```bash
pip install pymcu[pic]
# or independently:
pip install pymcu-toolchain-pic
```

## Supported chips

Any PIC chip matching `pic(10|12|14|16|17|18)` prefix, e.g.:
- `pic16f84a`, `pic16f877a`
- `pic18f4550`

## Plugin registration

This package registers itself under `pymcu.toolchains` so the PyMCU CLI discovers it automatically:

```
pymcu toolchain list
pymcu toolchain install pic
```

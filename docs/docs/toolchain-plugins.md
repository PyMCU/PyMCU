# Toolchain Plugins

PyMCU uses a plugin system to discover and load toolchains at runtime. Toolchains
are independent packages that register themselves under the `pymcu.toolchains`
entry-point group. The PyMCU CLI finds them automatically — no code changes to
`pymcu` itself are needed when a new toolchain is released.

---

## Installing toolchains

PyMCU does not bundle any toolchain in the base package. Install the one(s) you need:

```bash
# AVR targets (ATmega, ATtiny, ...)
pip install pymcu[avr]

# PIC targets (PIC16, PIC18, ...)
pip install pymcu[pic]

# All supported toolchains
pip install pymcu[all]
```

After installation, verify with:

```bash
pymcu toolchain list
```

---

## Toolchain commands

```bash
pymcu toolchain list            # show installed plugins and their status
pymcu toolchain install avr     # download and cache the AVR toolchain
pymcu toolchain update avr      # re-download to pick up a newer version
```

---

## Writing a toolchain plugin

A toolchain plugin is a Python package that:

1. Depends on `pymcu-plugin-sdk`
2. Implements the `ToolchainPlugin` ABC
3. Registers itself under the `pymcu.toolchains` entry-point group

### 1. Depend on the SDK

```toml
[project]
name = "pymcu-myarch"
dependencies = ["pymcu-plugin-sdk>=0.1.0a1"]
```

### 2. Implement `ToolchainPlugin`

```python
from pymcu.toolchain.sdk import ExternalToolchain, ToolchainPlugin
from rich.console import Console
from .mytoolchain import MyArchToolchain   # your ExternalToolchain subclass


class MyArchPlugin(ToolchainPlugin):
    family = "myarch"
    description = "My architecture toolchain"
    version = "1.0.0"
    default_chip = "mychip-default"

    @classmethod
    def supports(cls, chip: str) -> bool:
        return chip.lower().startswith("my")

    @classmethod
    def get_toolchain(cls, console: Console, chip: str) -> ExternalToolchain:
        return MyArchToolchain(console, chip)
```

The optional `get_ffi_toolchain()` method (returns `None` by default) enables C interop
for architectures that provide a GNU binutils-based pipeline.

### 3. Register the entry point

```toml
[project.entry-points."pymcu.toolchains"]
myarch = "pymcu.toolchain.myarch:MyArchPlugin"
```

### 4. Implement `ExternalToolchain`

Your toolchain class must extend `pymcu.toolchain.sdk.ExternalToolchain` and implement:

| Method | Description |
|---|---|
| `get_name() -> str` | Short identifier (used for the tool cache directory) |
| `supports(chip: str) -> bool` | Returns True for chips this toolchain handles |
| `is_cached() -> bool` | Returns True if the tool binaries are already present |
| `install() -> None` | Downloads / installs the tool binaries |
| `assemble(asm_file, output_file) -> Path` | Runs the assembler, returns the output path |

---

## SDK reference

```python
from pymcu.toolchain.sdk import (
    CacheableTool,        # base class for any downloadable, cached tool
    ExternalToolchain,    # base class for assembler/linker toolchains
    HardwareProgrammer,   # base class for hardware flash programmers
    ToolchainPlugin,      # entry-point contract for toolchain plugins
    _default_platform_key,  # returns "linux-x86_64", "darwin-arm64", etc.
    _is_non_interactive,    # True in CI / non-TTY environments
    _tool_lock,             # POSIX advisory lock for concurrent install safety
)
```

The SDK (`pymcu-plugin-sdk`) is the **only** package that toolchain plugins need to
depend on. They do **not** depend on `pymcu` itself, which avoids circular dependencies.

---

## Dependency graph

```
pymcu-toolchain-sdk   (deps: rich only)
       ^                        ^
       |                        |
     pymcu               pymcu-toolchain-avr
   (sdk + CLI)             (sdk only)
   [optional: avr]               ^
   [optional: pic]        pymcu-toolchain-pic
                            (sdk only)
```

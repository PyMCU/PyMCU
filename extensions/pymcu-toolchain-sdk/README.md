# pymcu-toolchain-sdk

Shared base classes and plugin protocol for PyMCU toolchain packages.

This package provides the stable API surface that all PyMCU toolchain plugins depend on:

- `CacheableTool` — abstract base for any downloadable, cached tool
- `ExternalToolchain` — abstract base for assembler/compiler toolchains
- `HardwareProgrammer` — abstract base for hardware flash programmers
- `ToolchainPlugin` — entry-point contract for toolchain plugins

## Usage

```python
from pymcu.toolchain.sdk import (
    CacheableTool,
    ExternalToolchain,
    HardwareProgrammer,
    ToolchainPlugin,
)
```

Plugin packages register themselves under the `pymcu.toolchains` entry-point group:

```toml
[project.entry-points."pymcu.toolchains"]
avr = "pymcu.toolchain.avr:AvrToolchainPlugin"
```

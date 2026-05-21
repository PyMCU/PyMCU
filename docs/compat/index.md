# Compatibility Layers

PyMCU provides optional compatibility packages that let you write firmware using familiar
MicroPython or CircuitPython APIs — and compile it to native machine code without any
interpreter overhead.

:::{important} These are shims, not full implementations
The compatibility packages translate API calls to PyMCU HAL calls at compile time. Features
that fundamentally require a runtime interpreter (dynamic allocation, exceptions, float sleep
durations, etc.) are not available. See each page for the full differences table.
:::

| Package | `pyproject.toml` setting | Modules provided |
|---|---|---|
| {doc}`micropython` | `stdlib = ["micropython"]` | `machine`, `utime`, `micropython` |
| {doc}`circuitpython` | `stdlib = ["circuitpython"]` | `board`, `digitalio`, `analogio`, `busio`, `pwmio`, `time` |

```{toctree}
:maxdepth: 1
:hidden:

micropython
circuitpython
```

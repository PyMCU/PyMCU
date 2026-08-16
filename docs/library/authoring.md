# Writing a PyMCU Library

:::{admonition} Proposed design — not implemented yet
:class: warning

The `pymcu.libraries` entry point, the `pymcu install` command and the curated index at
`pymcu.org/libraries` are **designed but not released**. This page documents the target
shape so libraries can be written against it today; the packaging and publishing steps
will only work once the tooling ships. Nothing here changes how `pymcu.drivers.*` works
in the current release.
:::

A PyMCU library is **source code the compiler reads at build time**, not a module
imported at runtime. There is no interpreter on the device: your `.py` files are parsed
by `pymcuc` and compiled into the user's firmware alongside their own code.

That single fact shapes everything below. A library is a wheel that ships `.py` files and
a manifest; it never ships a binary, and it is always a **project dependency**, never a
global install — the compiler looks inside the project's `.venv`.

---

## 1. Package layout

```
pymcu-lib-dht11/                     # distribution name on PyPI
  pyproject.toml
  src/pymcu_lib_dht11/
    pymcu.toml                       # the manifest
    dht11.py                         # public API, arch-neutral
    _dht11_avr.py                    # per-architecture implementation
    _dht11_arm.py
    compat/
      micropython/dht11.py           # optional adapter
      circuitpython/dht11.py         # optional adapter
    examples/basic/                  # a compilable project; see below
  api-surface.lock
```

`examples/` goes **inside** the package, not beside `src/`. It is what
`pymcu install --verify` compiles for the user's chip, and that only works if
the example travels in the wheel — a few KB of `.py` for a check that runs on
the machine that will actually build the firmware.

The directory of the installed package is added to the compiler's include path, so the
modules inside it become **top-level imports** for the user:

```python
from dht11 import DHT11
```

The distribution name (`pymcu-lib-dht11`) and the import name (`dht11`) are deliberately
different: the first has to be unique on PyPI, the second only has to be unique among the
libraries a project actually installs.

### `pyproject.toml`

```toml
[build-system]
requires = ["hatchling"]
build-backend = "hatchling.build"

[project]
name = "pymcu-lib-dht11"
version = "0.2.0"
description = "DHT11 temperature and humidity sensor for PyMCU"
requires-python = ">=3.11"
license = { text = "MIT" }
dependencies = ["pymcu-stdlib>=0.1.0a5"]

[project.entry-points."pymcu.libraries"]
dht11 = "pymcu_lib_dht11"

[tool.hatch.build.targets.wheel]
packages = ["src/pymcu_lib_dht11"]
```

`pymcu-stdlib` is a real dependency: the sources import `pymcu.types` and `pymcu.hal.*`,
so an environment without it cannot compile them. The entry point is how `pymcu build`
finds the package without anyone listing it in `[tool.pymcu]`.

---

## 2. The manifest

`pymcu.toml` sits at the root of the importable package and travels inside the wheel.

```toml
[library]
name = "dht11"
summary = "DHT11 temperature and humidity sensor"
license = "MIT"
repository = "https://github.com/example/pymcu-lib-dht11"
categories = ["sensor"]

[library.provides]
modules = ["dht11"]

[library.supports]
arch = ["avr", "arm"]
chips = []
layer = "native"
adapters = ["micropython", "circuitpython"]
symbols = []

[library.requires]
stdlib = ">=0.1.0a5"
compiler = ">=0.1.0a5"
language-level = 1

[library.examples]
basic = "examples/basic"
```

| Key | Meaning |
|---|---|
| `provides.modules` | Top-level names the library claims. Two installed libraries claiming the same name is a resolution error. |
| `supports.arch` | Architectures, as reported by `__CHIP__.arch`: `avr`, `arm`, `pic12`, `pic14`, `pic14e`, `pic18`, `riscv`. |
| `supports.chips` | Narrows to specific chips. Empty means every chip of the listed architectures. |
| `supports.layer` | `native`, `micropython` or `circuitpython` — which API the core is written against. |
| `supports.adapters` | Layers with a wrapper under `compat/<layer>/`. |
| `supports.symbols` | Optional. For layer libraries, the symbols actually used (e.g. `["machine.Pin"]`). |
| `requires.*` | Version ranges and the language level. Mirror `stdlib`/`compiler` in `[project.dependencies]` so `pip` fails during resolution, not during a build. |

**The manifest carries no version number.** The version comes from the distribution
metadata (`importlib.metadata.version`). A number that is not duplicated cannot drift out
of sync — this rule exists because a package once shipped twice under the same version
with different contents.

`supports` is a **promise**, not a measurement. CI verifies it by compiling: everything
listed must build, and an architecture that is *not* listed must fail to build.

---

## 3. Writing the code

### Target the native HAL, not a compatibility layer

The MicroPython, CircuitPython and native layers are **not interoperable**. `time.sleep`
takes a `uint16` in the MicroPython layer and a `float` in the CircuitPython one;
`board.D0` is the integer `0` in one and the string `"PD0"` in the other. The only thing
all three share is `pymcu.hal.*` and `pymcu.types`, which both compat packages depend on
and wrap.

So a portable library is written against the HAL and takes **pin identifiers**, not layer
objects:

```python
from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError
from pymcu.types import uint16, inline


class DHT11:

    @inline
    def __init__(self, pin: str):
        self.name = pin

    @inline
    def read(self) -> uint16:
        match __CHIP__.arch:
            case "avr":
                from _dht11_avr import _avr_read
                return _avr_read(self.name)
            case "arm":
                from _dht11_arm import _arm_read
                return _arm_read(self.name)
            case _:
                raise CompileError("DHT11 is not supported on this architecture")
```

`__CHIP__.name` and `__CHIP__.arch` are resolved at compile time and the losing branches
are eliminated, so this costs nothing at runtime — the same two-level dispatch the HAL
itself uses.

### End the dispatch with `CompileError`, never a sentinel

The `case _:` branch must raise. A driver that returns `0xFFFF` on an unsupported
architecture *compiles*, and the user finds out on the bench instead of at build time.
With `CompileError`, "it compiles" means "the author implemented this architecture", which
is also what makes the measured compatibility matrix trustworthy.

### Keep it zero-cost

Flash is the scarce resource: an ATmega328P has 32 KB. Follow the same rules as the HAL —
`@inline` methods, no instance state beyond what the wrapped object needs, primitives in
helper signatures. A library written in ordinary Python style compiles, but may not fit.
See {doc}`../language/type-system` for the zero-cost abstraction model.

### ASCII only

The lexer rejects any non-ASCII character outside comments and strings. Worse, non-ASCII
*inside* a string passes the lexer and is then encoded as ASCII, corrupting the byte
silently. Keep every source file in the package plain ASCII — degree signs and accented
characters included.

---

## 4. Optional compatibility adapters

An adapter re-exports the core with the idioms of one layer. It lives under
`compat/<layer>/` and only enters the include path when the project declares that layer,
so both adapters can use the same module name:

```python
# compat/circuitpython/dht11.py
from pymcu.types import uint16, inline
from _dht11_avr import _avr_read as _read_avr   # or import the shared core


class DHT11:

    @inline
    def __init__(self, pin):
        self._name = pin        # board.D4 is already a pin-name string

    @inline
    def read(self) -> uint16:
        ...
```

Import the core through its private module name (`_dht11_avr`), never through the public
one — inside the adapter, `dht11` is the adapter itself.

This mirrors what the compat packages already do with `pymcu.drivers.neopixel`: one core,
thin wrappers per layer.

---

## 5. The example project

`examples/basic/` is a normal PyMCU project — `pyproject.toml` with `[tool.pymcu]` plus a
`src/main.py`. It has three jobs: it is the copy-pasteable snippet in the docs, it is what
CI compiles per chip to produce the compatibility matrix and the flash/RAM figures in the
index, and it is what `pymcu install --verify` builds on the user's machine for their
chip.

That last one is why it ships inside the package. `--verify` copies it to a temporary
directory, rewrites `[tool.pymcu]` to the installing project's chip and layer, and builds
it; a failure rolls the install back instead of leaving a library that only breaks later.

Keep it minimal. Anything the example pulls in shows up in the numbers.

---

## 6. Testing it locally

Install the library into a test project's environment in editable mode and build:

```bash
cd examples/basic
uv pip install -e ../..
pymcu build --verbose
```

`--verbose` prints the include paths. The package directory (and its `compat/<layer>/`
when the project declares a layer) must appear as `[debug] Extra include:` lines. If it
does not, the entry point is missing or the environment being used is not the project's.

Then check the negative case: switch `[tool.pymcu]` to a chip of an architecture you do
not support and confirm the build fails with your `CompileError`, not with wrong output.

---

## 7. Publishing

1. **Lint**: `pymcu lint --library` checks ASCII, the manifest, the dispatch rule and the
   public API surface hash against `api-surface.lock`.
2. **Bump the version** whenever the public surface changes. The surface hash exists to
   make forgetting impossible: CI fails if the hash moved and the version did not.
3. **Publish to PyPI**, ideally with trusted publishing from a GitHub Release, the same
   way the PyMCU packages are published.
4. **Submit to the index**: open a PR against `pymcu-libraries` adding one line with your
   distribution name. CI validates the manifest, checks that the sources are ASCII,
   compiles the example for every declared architecture (and confirms it fails for an
   undeclared one), and measures flash and RAM. Merge regenerates
   `pymcu.org/libraries/index.json`.

You do not need a new PR for later releases: the index is regenerated periodically, picks
up the newest version from PyPI and re-measures it. That regeneration is also what marks a
library `broken` when a compiler release stops building it, so the listing reflects
reality rather than the day it was submitted.

---

## Checklist

- [ ] Public API takes pin identifiers, not layer objects
- [ ] Architecture dispatch via `match __CHIP__.arch`, ending in `CompileError`
- [ ] Every method `@inline`; no unnecessary instance state
- [ ] All sources ASCII, comments included
- [ ] `pymcu.toml` present, with no version number in it
- [ ] `pymcu-stdlib` (and any other requirement) declared in `[project.dependencies]`
- [ ] `pymcu.libraries` entry point registered
- [ ] `examples/basic/` compiles for every declared architecture
- [ ] `api-surface.lock` regenerated and the version bumped

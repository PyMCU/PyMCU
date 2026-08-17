# Writing a PyMCU Library

:::{admonition} The index is not online yet
:class: warning

The tooling described here works: `pymcu build` discovers libraries through the
`pymcu.libraries` entry point, and `pymcu install`, `pymcu libraries` and
`pymcu lint --library` are in the driver.

What does not exist yet is the curated index at `pymcu.org/libraries`, so
`pymcu install <name>` has nothing to resolve names against. Until it is published,
install with `pymcu install --from-pypi <distribution>`, which skips the index but still
requires a valid manifest and still checks the target.
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
pymcu-lib-dht/                       # distribution name on PyPI
  pyproject.toml
  README.md
  api-surface.lock
  docs/
  tests/
  examples/basic/                    # a compilable project; see below
  src/pymcu_lib_dht/
    __init__.py                      # an ordinary Python package
    pymcu.toml                       # the manifest
    mcu/                             # everything the compiler reads
      dht.py                         # public API, arch-neutral
      adafruit_dht.py                # a second public name, CircuitPython's
      _dht/                          # private implementation package
        __init__.py
        core.py
        avr.py
      compat/
        micropython/dht.py           # optional adapter
```

Two rules, and everything else follows from them.

**The package is an ordinary Python package.** It has an `__init__.py`, so
`import pymcu_lib_dht` works and `importlib.resources` can find its data. Without one
it would be a PEP 420 namespace package by accident, which is not something to be by
accident.

**Only `mcu/` reaches the compiler.** The include path gets that directory and nothing
else, so what a user can import is exactly what you put there. Everything else —
examples, tests, docs, tooling — lives at the distribution root, outside the package,
and so travels in the sdist and not the wheel. That is the normal split for a Python
project, and `pymcu lint --library` enforces it: a `.py` anywhere else in the package
is an error. It has to be, because when the include path was the package itself, a
library's `examples/` answered `import examples` from any firmware in the world, and two
libraries shipping one shadowed each other with no diagnostic at all.

Modules directly inside `mcu/` become **top-level imports** for the user:

```python
from dht import DHT11
```

Anything deeper is yours. A private package is how you avoid stamping a prefix on every
file: the include path is flat and shared with every other installed library, so a bare
`core.py` would be a global name — but `_dht/core.py` is not, and reads better than
`_dht_core.py` at every call site.

The distribution name (`pymcu-lib-dht`) and the import name (`dht`) are deliberately
different: the first has to be unique on PyPI, the second only has to be unique among the
libraries a project actually installs.

### `pyproject.toml`

```toml
[build-system]
requires = ["hatchling"]
build-backend = "hatchling.build"

[project]
name = "pymcu-lib-dht"
version = "0.2.0"
description = "DHT11, DHT22 and DHT21 sensors for PyMCU"
requires-python = ">=3.11"
license = { text = "MIT" }
dependencies = ["pymcu-stdlib>=0.1.0a5"]

[project.entry-points."pymcu.libraries"]
dht = "pymcu_lib_dht"

[tool.hatch.build.targets.wheel]
packages = ["src/pymcu_lib_dht"]
```

`pymcu-stdlib` is a real dependency: the sources import `pymcu.types` and `pymcu.hal.*`,
so an environment without it cannot compile them. The entry point is how `pymcu build`
finds the package without anyone listing it in `[tool.pymcu]`.

---

## 2. The manifest

`pymcu.toml` sits at the root of the importable package and travels inside the wheel.

```toml
[library]
name = "dht"
summary = "DHT11, DHT22 and DHT21 temperature and humidity sensors"
license = "MIT"
repository = "https://github.com/example/pymcu-lib-dht"
categories = ["sensor"]
sources = "mcu"

[library.provides]
modules = ["dht", "adafruit_dht", "_dht"]

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
| `sources` | Directory inside the package holding what the compiler reads. Defaults to `mcu`. Nothing outside it is on the include path. |
| `provides.modules` | Top-level names the library claims, private ones included. Two installed libraries claiming the same name is a resolution error, so a private package has to be declared to be protected. |
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
                from _dht.avr import read
                return read(self.name)
            case "arm":
                from _dht.arm import read
                return read(self.name)
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
`mcu/compat/<layer>/` and only enters the include path when the project declares that
layer, so several adapters can use the same module name:

```python
# mcu/compat/micropython/dht.py
from pymcu.types import uint16, inline
from _dht.core import Frame


class DHT11:

    @inline
    def __init__(self, pin):
        self._frame = Frame(pin)    # a machine.Pin, not a bare name

    @inline
    def measure(self):
        ...
```

An adapter is only worth writing where the API actually differs. If a layer would import
the same name with the same shape, do not add a directory for it — a copy that shadows
nothing is just a second place to fix the same bug.

Import the core through its private package (`_dht.core`), never through the public
name — inside the adapter, `dht` *is* the adapter. That is the whole reason the core has
a name of its own rather than living in a `dht/` package: an adapter that shadows the
public name would otherwise shadow the implementation along with it.

This mirrors what the compat packages already do with `pymcu.drivers.neopixel`: one core,
thin wrappers per layer.

---

## 5. The example project

`examples/basic/` is a normal PyMCU project — `pyproject.toml` with `[tool.pymcu]` plus a
`src/main.py`. It sits at the **distribution root**, beside `tests/` and `docs/`, so it
travels in the sdist and not in the wheel.

It has two jobs: it is the copy-pasteable snippet the docs and the catalogue show, and it
is what the index compiles per chip to produce the compatibility matrix and the flash/RAM
figures. The index reads it from the sdist, which is exactly what an sdist is for — an
immutable, versioned artefact carrying everything needed to build and check the project.

It is *not* what `pymcu install --verify` compiles. That builds a small program importing
the modules in `provides.modules`, which needs nothing but the wheel: it answers whether
those modules resolve and compile for the installing project's chip and layer, and rolls
the install back if they do not.

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
4. **Submit to the index**: open a PR against
   [`pymcu-libraries`](https://github.com/PyMCU/pymcu-libraries) adding one line with your
   distribution name. CI installs it, runs the checks above, and compiles your example for
   **one chip per architecture — including the ones you did not declare**. That last part
   is the whole point: it is what separates "does not support ARM" from "nobody updated
   `supports.arch`". Declaring an architecture that does not build fails the check;
   building for one you never declared is reported so you can claim it.

You do not need a new PR for later releases. A scheduled run re-installs the newest
version of everything listed and measures it again, so an entry says what builds *today*
rather than what built the day it was submitted — and a library that stops building
against a new compiler is marked `broken` without anyone filing an issue.

### Where the index lives

Two copies of the same file: `https://libraries.pymcu.org/index.json` (an R2 bucket behind
a Worker) and a mirror at `raw.githubusercontent.com/PyMCU/pymcu-libraries/main/index.json`.
The driver tries the first and falls back to the second, because the `pymcu.org` zone
answers 403 to requests from data centres and `pymcu install` runs inside other people's
CI. `PYMCU_LIBRARY_INDEX` overrides both, which is also how you test against a local file.

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

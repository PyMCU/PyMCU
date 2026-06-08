# Installation

:::{note}
**Alpha release** — PyMCU is currently in alpha (`0.1.0a1`). By default, `pip`, `uv` and
`pipx` do not install pre-releases unless you pass a `--pre` flag. The commands on this
page include the required flags. Once `0.1.0` stable ships, `--pre` will not be needed.
:::

## Option 1 — pip (recommended)

`pymcu-compiler` is distributed on PyPI. Install it with `pip` or `uv`:

::::{tab-set}
:::{tab-item} uv (recommended)
```bash
uv tool install --pre pymcu-compiler
```

`uv tool install` places the `pymcu` command on your PATH globally, isolated in its own
virtual environment. No activation step needed.

Requires **uv 0.11 or newer** — run `uv self update` if you are on an older version.
:::
:::{tab-item} pipx
```bash
pipx install pymcu-compiler --pip-args="--pre"
```

`pipx` installs into an isolated environment and puts `pymcu` on your PATH.
:::
:::{tab-item} pip
```bash
pip install --pre pymcu-compiler
```
:::
::::

Verify:

```bash
pymcu --version
# pymcu-compiler, version 0.1.0a1.post0
```

### Compat layer extras

Install optional MicroPython or CircuitPython compatibility packages alongside
`pymcu-compiler`:

```bash
# MicroPython compat (machine, utime, micropython modules)
pip install pymcu-micropython

# CircuitPython compat (board, digitalio, busio, neopixel, …)
pip install pymcu-circuitpython
```

### RP2040 / ARM backend (alpha)

AVR support is built in. To compile for the **Raspberry Pi Pico (RP2040)**, install the
ARM backend — it registers the `rp2040` target, toolchain and programmer with the
compiler via entry points:

::::{tab-set}
:::{tab-item} uv
```bash
uv tool install --pre "pymcu-compiler[arm]"
```
:::
:::{tab-item} pipx
```bash
pipx install "pymcu-compiler[arm]" --pip-args="--pre"
```
:::
:::{tab-item} pip
```bash
pip install --pre "pymcu-compiler[arm]"
```
:::
::::

Or if `pymcu-compiler` is already installed:

```bash
pip install --pre pymcu-arm
```

This backend lowers PyMCU's IR to **LLVM IR** (`thumbv6m-none-eabi`, Cortex-M0+) and
drives an LLVM toolchain (`opt` → `llc` → `ld.lld` → `llvm-objcopy`). On Linux x64/arm64,
Windows x64 and macOS arm64 the prebuilt `pymcu-arm-toolchain` wheel is pulled in
automatically. On other platforms, install a system LLVM instead:

```bash
brew install llvm lld        # macOS
sudo apt install llvm lld    # Debian/Ubuntu
```

RP2040 support is **alpha** — GPIO + UART0 on a single core. See
{doc}`../language/limitations` for the exact scope and {doc}`../examples/rp2040` for
runnable programs.

---

## Option 2 — Docker image

Pre-built images are published to GitHub Container Registry for every release.
Docker is useful for CI pipelines or environments where you cannot install Python tools.

| Flavor | Image | Stdlib included |
|---|---|---|
| `base` | `ghcr.io/pymcu/pymcu:latest` | Bare-metal PyMCU stdlib |
| `micropython` | `ghcr.io/pymcu/pymcu:micropython` | + `machine`, `utime`, `micropython` compat |
| `circuitpython` | `ghcr.io/pymcu/pymcu:circuitpython` | + `board`, `digitalio`, `analogio`, `pwmio` compat |

```bash
docker pull ghcr.io/pymcu/pymcu:latest
```

Mount your project and run `pymcu build`:

```bash
docker run --rm \
    -v "$(pwd):/workspace" \
    ghcr.io/pymcu/pymcu:latest \
    sh -c "cd /workspace && pymcu build"
```

---

## Option 3 — Build from source

For contributors or anyone who wants to run the latest development build.

### Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Python 3.11 or newer
- `uv` (recommended)
- `avrdude` (for flashing — see below)

### Steps

```bash
git clone https://github.com/pymcu/pymcu.git
cd pymcu

# Build the C# compiler and AVR backend
dotnet publish src/compiler/PyMCU.csproj -c Release -o build/bin --nologo
dotnet publish extensions/pymcu-avr/src/csharp/cli/PyMCU.AVR.csproj -c Release -o build/bin --nologo

# Set up Python environment
uv venv && source .venv/bin/activate
uv sync
rsync -av lib/src/pymcu/ .venv/lib/python3.*/site-packages/pymcu/
pip install -e src/driver
```

Verify:

```bash
pymcu --version
# pymcu-compiler, version 0.1.0a1
```

---

## Flash the firmware

Compiling produces `dist/firmware.hex`. Flashing to an Arduino Uno requires **avrdude**
installed on your host machine.

::::{tab-set}
:::{tab-item} macOS
```bash
brew install avrdude
```
:::
:::{tab-item} Linux (Debian/Ubuntu)
```bash
sudo apt-get install avrdude
```
:::
:::{tab-item} Windows
Download from the [AVRDUDE releases page](https://github.com/avrdudes/avrdude/releases).
:::
::::

Then use `pymcu flash` (which calls avrdude internally):

```bash
pymcu flash --port /dev/cu.usbmodem*   # macOS
pymcu flash --port /dev/ttyACM0        # Linux
pymcu flash --port COM3                # Windows
```

Or call avrdude directly:

```bash
avrdude -c arduino -p atmega328p -P /dev/ttyACM0 -b 115200 \
        -U flash:w:dist/firmware.hex:i
```

---

## Next steps

- {doc}`quickstart` — create your first project and flash an LED

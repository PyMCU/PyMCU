# Installation

:::{admonition} Alpha Software — UX/UI in progress
:class: warning

PyMCU is in early alpha. A graphical interface and simplified tooling are under active
development. For now, all workflows are command-line based and flashing requires
**avrdude** to be installed separately on the host machine.
:::

---

## Option 1 — Docker (recommended)

The easiest way to get started is the official PyMCU Docker image. It ships with the
compiler, AVR toolchain, and the correct stdlib already configured — no need to install
.NET, Python, or `avrdude` inside the container.

### Choose a flavor

The image comes in three flavors depending on which stdlib you want baked in:

| Flavor | `--build-arg FLAVOR=` | Description |
|---|---|---|
| `base` | `base` (default) | Bare-metal PyMCU stdlib only |
| `micropython` | `micropython` | Adds `machine`, `utime`, `micropython` compat layer |
| `circuitpython` | `circuitpython` | Adds `board`, `digitalio`, `analogio`, `pwmio`, `busio` compat layer |

### Build the image

```bash
# Clone the repository
git clone https://github.com/pymcu/pymcu.git
cd pymcu

# Base image
docker build -t pymcu .

# With MicroPython compat
docker build --build-arg FLAVOR=micropython -t pymcu:micropython .

# With CircuitPython compat
docker build --build-arg FLAVOR=circuitpython -t pymcu:circuitpython .
```

### Compile your project

Mount your project directory into `/workspace` and run `pymcu build`:

```bash
docker run --rm -v "$(pwd):/workspace" pymcu \
    sh -c "cd /workspace && pymcu build"
```

This produces `dist/firmware.hex` in your project folder on the host.

:::{note}
Flashing (`pymcu flash`) requires access to the USB serial port. Use `--device` to
pass the port through to the container, or flash directly from the host with
`avrdude` (see [Flash the firmware](#flash-the-firmware) below).
:::

---

## Option 2 — Local build from source

Use this if you want to contribute to PyMCU or prefer a native install.

### Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Python 3.11 or newer
- `uv` (recommended for virtual environment setup)
- `avrdude` (for flashing — see below)

### 1. Clone the repository

```bash
git clone https://github.com/pymcu/pymcu.git
cd pymcu
```

### 2. Build the compiler

The PyMCU compiler (`pymcuc`) is written in C#. Build it with the .NET SDK:

```bash
dotnet build src/compiler/PyMCU.Compiler.csproj
```

### 3. Set up the Python environment

```bash
uv venv
source .venv/bin/activate          # Windows: .venv\Scripts\activate

rsync -av lib/src/pymcu/ .venv/lib/python3.11/site-packages/pymcu/

pip install -e src/driver
```

### 4. Verify

```bash
pymcu --version
# pymcu, version 0.11.0
```

---

## Flash the firmware

PyMCU generates a `.hex` file. Flashing to an Arduino Uno requires **avrdude** on the
host machine.

:::{admonition} avrdude is required for flashing
:class: note

A built-in flash command (`pymcu flash`) is available and calls avrdude internally.
A friendlier flashing UI is planned for a future release.
:::

Install avrdude:

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

Then flash:

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

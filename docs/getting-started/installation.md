# Installation

:::{admonition} Alpha Software — pip package coming soon
:class: warning

PyMCU is in early alpha. A `pip`-installable release of the `pymcu` CLI is under active
development. For now, the recommended path is the **Docker image from GitHub Container
Registry** — no local toolchain required.
:::

---

## Option 1 — Docker image from GitHub (recommended)

Pre-built images are published to GitHub Container Registry for every release. Pull the
flavor that matches your workflow:

| Flavor | Image | Stdlib included |
|---|---|---|
| `base` | `ghcr.io/pymcu/pymcu:latest` | Bare-metal PyMCU stdlib |
| `micropython` | `ghcr.io/pymcu/pymcu:micropython` | + `machine`, `utime`, `micropython` compat |
| `circuitpython` | `ghcr.io/pymcu/pymcu:circuitpython` | + `board`, `digitalio`, `analogio`, `pwmio` compat |

```bash
# Pull once
docker pull ghcr.io/pymcu/pymcu:latest          # base
docker pull ghcr.io/pymcu/pymcu:micropython      # MicroPython compat
docker pull ghcr.io/pymcu/pymcu:circuitpython    # CircuitPython compat
```

### Compile your project

Mount your project directory and run `pymcu build`:

```bash
docker run --rm \
    -v "$(pwd):/workspace" \
    ghcr.io/pymcu/pymcu:latest \
    sh -c "cd /workspace && pymcu build"
```

This writes `dist/firmware.hex` to your project folder on the host. Flash it with
`avrdude` — see [Flash the firmware](#flash-the-firmware).

---

## Option 2 — Build the Docker image locally

If you prefer to build from the repository instead of pulling:

```bash
git clone https://github.com/pymcu/pymcu.git
cd pymcu

# Base image
docker build -t pymcu .

# With MicroPython compat
docker build --build-arg FLAVOR=micropython -t pymcu:micropython .

# With CircuitPython compat
docker build --build-arg FLAVOR=circuitpython -t pymcu:circuitpython .
```

Then use the same `docker run` pattern shown above, replacing the image name with your
local tag (e.g. `pymcu` instead of `ghcr.io/pymcu/pymcu:latest`).

---

## Option 3 — Local install from source

For contributors or anyone who wants a native install without Docker.

### Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Python 3.11 or newer
- `uv` (recommended)
- `avrdude` (for flashing — see below)

### Steps

```bash
git clone https://github.com/pymcu/pymcu.git
cd pymcu

# Build the C# compiler
dotnet build src/compiler/PyMCU.Compiler.csproj

# Set up Python environment
uv venv && source .venv/bin/activate
rsync -av lib/src/pymcu/ .venv/lib/python3.11/site-packages/pymcu/
pip install -e src/driver
```

Verify:

```bash
pymcu --version
# pymcu, version 0.11.0
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

# Installation

:::{admonition} Alpha Software: Local Build Required
:class: warning

PyMCU is in early alpha and does not have a stable, packaged release. You must build it from source.
:::

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Python 3.11 or newer
- `uv` (recommended for virtual environment setup)
- A supported programmer for your target hardware (e.g. `avrdude` for Arduino)

## 1. Clone the repository

```bash
git clone https://github.com/pymcu/pymcu.git
cd pymcu
```

## 2. Build the compiler

The PyMCU compiler (`pymcuc`) is written in C#. Build it using the .NET SDK:

```bash
dotnet build src/compiler/PyMCU.Compiler.csproj
```

This creates the compiler executable at `src/compiler/bin/Debug/net8.0/pymcuc`.

## 3. Set up the Python environment

The `pymcu` command-line driver and the standard library are Python-based.

### Create a virtual environment (with `uv`)

```bash
uv venv
source .venv/bin/activate
```

### Sync the standard library

The PyMCU standard library (`lib/src/pymcu`) must be available in your environment. Use `rsync` to link it:

```bash
rsync -av lib/src/pymcu/ .venv/lib/python3.11/site-packages/pymcu/
```

### Install the driver

Install the `pymcu` driver in editable mode:

```bash
pip install -e src/driver
```

## 4. Verify the installation

Check that the `pymcu` command is available and can find the compiler:

```bash
pymcu --version
```

Expected output: `pymcu, version 0.10.0` (or similar)

---

## Hardware toolchains

PyMCU includes its own AVR codegen — no external assembler is needed to *compile*.
You only need external tools to *flash* the firmware.

### AVR (Arduino Uno / ATmega328P)

Install `avrdude`:

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

---

## Next steps

- {doc}`quickstart` — create your first project and flash an LED
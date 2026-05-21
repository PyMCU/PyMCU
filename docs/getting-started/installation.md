# Installation

## Requirements

- Python 3.11 or newer
- A supported programmer for your target hardware (e.g. avrdude for Arduino)

## Install with pipx (recommended)

```bash
pipx install pymcu
```

`pipx` installs PyMCU in an isolated environment and puts the `pymcu` command on your PATH.

## Install with pip

```bash
pip install pymcu
```

## Verify the installation

```bash
pymcu --version
```

Expected output: `pymcu 0.10.x`

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

### PIC

PyMCU will prompt you to download `pk2cmd` (PICKit 2) automatically on first use.

---

## IDE integration

### VS Code

`pymcu new` generates a `.vscode/tasks.json` with **Build** and **Flash** tasks. Open the
project folder in VS Code and run tasks with **Terminal → Run Task**.

### PyCharm / other

Add `pymcu build` and `pymcu flash` as External Tools (File → Settings → Tools → External Tools).

---

## Next steps

- {doc}`quickstart` — create your first project and flash an LED

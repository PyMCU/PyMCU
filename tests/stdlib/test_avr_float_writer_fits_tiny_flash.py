"""A 4 KB AVR part cannot hold the correct float repr; it must not try to.

uart_write_float (MicroPython's 7-significant-digit float32 policy) pulls in
_f32_repr + _f32_scale, about 7.3 KB together -- more than the entire flash
of a 4 KB part. lib/src/pymcu/hal/avr/uart/__init__.py routes any AVR chip
with flash_size <= 4096 to uart_write_float_compact (one decimal, no
_f32_repr/_f32_scale) instead. Regression: before this routing covered only
"attiny2313" by name, print(float) silently stopped building on the other
4 KB parts (attiny4313, atmega48, atmega48p) the day _f32_repr replaced the
old two-fixed-decimals formatter, because they were not on that list.

This builds a real firmware image per chip through the driver and inspects
the generated assembly, the same way test_avr_geometry_vs_vendor.py does --
a regex over the HAL source could drift from what actually gets linked in.
"""

import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCU = REPO / ".venv" / "bin" / "pymcu"

pytestmark = pytest.mark.skipif(
    not PYMCU.exists(), reason="pymcu driver not built in this repo's .venv")

PROJECT = """from pymcu.hal.uart import UART


def main():
    u = UART(9600)
    v: float = 1234.5
    u.print_float(v)
"""

TOML = ('[project]\nname = "v"\nversion = "0.1.0"\nrequires-python = ">=3.11"\n\n'
        '[tool.pymcu]\ntarget = "{chip}"\nfrequency = 8000000\nsources = "src"\n'
        'entry = "main.py"\n')

# flash_size (bytes) for each chip, from lib/src/pymcu/chips/<chip>.py --
# duplicated here as a small, explicit fixture rather than re-parsed, so this
# test does not silently follow a future change to those files.
TINY_FLASH = {"attiny2313": 2048, "attiny4313": 4096, "atmega48": 4096, "atmega48p": 4096}
ROOMY_FLASH = {"atmega88": 8192, "atmega328p": 32768}


def build(tmp_path: Path, chip: str) -> str:
    (tmp_path / "src").mkdir()
    (tmp_path / "src" / "main.py").write_text(PROJECT)
    (tmp_path / "pyproject.toml").write_text(TOML.format(chip=chip))
    proc = subprocess.run([str(PYMCU), "build"], cwd=tmp_path,
                          capture_output=True, text=True, timeout=120)
    assert proc.returncode == 0, (
        f"{chip}: print(float) failed to build\n{proc.stdout}\n{proc.stderr}")
    asm = tmp_path / "dist" / "debug" / "firmware.asm"
    assert asm.exists(), f"{chip}: build reported success but left no debug asm"
    return asm.read_text()


@pytest.mark.parametrize("chip", sorted(TINY_FLASH))
def test_a_4kb_or_smaller_part_uses_the_compact_writer(tmp_path, chip):
    text = build(tmp_path, chip)
    assert "f32_repr" not in text, (
        f"{chip} ({TINY_FLASH[chip]} B flash) linked in the full float repr "
        "(~7.3 KB), which cannot fit -- it should have used "
        "uart_write_float_compact instead")


@pytest.mark.parametrize("chip", sorted(ROOMY_FLASH))
def test_a_bigger_part_uses_the_correct_writer(tmp_path, chip):
    text = build(tmp_path, chip)
    assert "f32_repr" in text, (
        f"{chip} ({ROOMY_FLASH[chip]} B flash, room for the correct writer) "
        "used the compact one-decimal fallback instead of the correct "
        "7-significant-digit repr")

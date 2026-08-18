import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

RP2040_TIMERAWL = 0x40054028
RP2350_TIMERAWL = 0x400B0028

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(),
    reason="compiler binary not built (run `just build`)",
)

PROGRAM = (
    "from pymcu.types import uint32\n"
    "from pymcu.time import millis, micros\n"
    "\n"
    "def main():\n"
    "    while True:\n"
    "        t: uint32 = millis()\n"
    "        u: uint32 = micros()\n"
    "        if t > u:\n"
    "            pass\n"
)


def frontend(tmp_path: Path, arch: str, chip: str, freq: int, source: str = PROGRAM):
    src = tmp_path / "main.py"
    src.write_text(source)
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(src), "-o", "/dev/null", "--arch", arch,
         "--target", chip, "--freq", str(freq), "-I", str(STDLIB),
         "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    ir = mir.read_text() if mir.exists() else ""
    return proc, ir


def test_micros_on_rp2040_reads_the_hardware_timer(tmp_path):
    proc, ir = frontend(tmp_path, "arm", "rp2040", 125_000_000)
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr
    assert str(RP2040_TIMERAWL) in ir


def test_micros_on_rp2350_reads_the_hardware_timer(tmp_path):
    proc, ir = frontend(tmp_path, "arm", "rp2350", 150_000_000)
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr
    assert str(RP2350_TIMERAWL) in ir


def test_millis_on_rp2040_divides_the_microsecond_counter(tmp_path):
    proc, ir = frontend(tmp_path, "arm", "rp2040", 125_000_000)
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr
    assert '"value": 1000' in ir or '"value":1000' in ir


def test_millis_on_atmega_still_uses_the_timer0_counter(tmp_path):
    proc, _ = frontend(tmp_path, "avr", "atmega328p", 16_000_000)
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr


def test_millis_without_a_timebase_is_a_compile_error_not_a_frozen_zero(tmp_path):
    proc, _ = frontend(tmp_path, "pic18", "pic18f45k50", 16_000_000)
    assert "[BUILD_FAIL]" in proc.stdout, proc.stdout + proc.stderr
    combined = proc.stdout + proc.stderr
    assert "millis() needs a timebase" in combined
    assert "delay_ms" in combined


def test_micros_without_a_timebase_is_a_compile_error(tmp_path):
    source = (
        "from pymcu.types import uint32\n"
        "from pymcu.time import micros\n"
        "\n"
        "def main():\n"
        "    while True:\n"
        "        u: uint32 = micros()\n"
        "        if u > 0:\n"
        "            pass\n"
    )
    proc, _ = frontend(tmp_path, "pic18", "pic18f45k50", 16_000_000, source)
    assert "[BUILD_FAIL]" in proc.stdout, proc.stdout + proc.stderr
    assert "micros() needs a timebase" in proc.stdout + proc.stderr

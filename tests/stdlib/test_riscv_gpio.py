# tests/stdlib/test_riscv_gpio.py
#
# Compiles small programs against the WCH RISC-V GPIO HAL and checks the
# addresses and bit patterns that reach the assembly. The port map is pure
# table lookup, so a wrong entry is invisible until it drives the wrong pin on
# real silicon -- exactly what these assertions pin down.

import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
BACKEND = REPO / "build" / "bin" / "pymcuc-riscv"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not (PYMCUC.exists() and BACKEND.exists()),
    reason="compiler binaries not built (run `just build` and `just build-backend-riscv`)",
)


def compile_asm(tmp_path: Path, source: str, chip: str) -> str:
    """Run the two-phase pipeline and return the generated assembly."""
    src = tmp_path / "main.py"
    src.write_text(source)
    mir = tmp_path / "firmware.mir"
    asm = tmp_path / "firmware.asm"

    frontend = subprocess.run(
        [str(PYMCUC), str(src), "-o", "/dev/null", "--arch", "riscv",
         "--target", chip, "--freq", "48000000", "-I", str(STDLIB),
         "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    assert "[BUILD_OK]" in frontend.stdout, frontend.stdout + frontend.stderr

    backend = subprocess.run(
        [str(BACKEND), str(mir), "-o", str(asm), "--target", chip, "--arch", "riscv"],
        capture_output=True, text=True,
    )
    assert backend.returncode == 0, backend.stdout + backend.stderr
    return asm.read_text()


def blink(pin: str) -> str:
    return (
        "from pymcu.hal.gpio import Pin\n"
        "\n"
        "def main():\n"
        f"    led = Pin(\"{pin}\", Pin.OUT)\n"
        "    while True:\n"
        "        led.high()\n"
        "        led.low()\n"
        "        led.toggle()\n"
    )


# ---------------------------------------------------------------------------
# CH32V003 -- ports A/C/D, 8 pins, CFGLR only
# ---------------------------------------------------------------------------

class TestCh32v003:
    def test_port_d_pin_4(self, tmp_path):
        asm = compile_asm(tmp_path, blink("PD4"), "ch32v003")
        assert "li\tt2, 0x40011400" in asm          # GPIOD_CFGLR
        assert "li\tt1, 65536" in asm               # output nibble at (4 % 8) * 4 = 16
        assert "li\tt2, 0x4001140C" in asm          # GPIOD_BSHR
        assert "li\tt0, 16" in asm                  # set   -> 1 << 4
        assert "li\tt0, 1048576" in asm             # reset -> 1 << (4 + 16)

    def test_port_a_pin_1(self, tmp_path):
        asm = compile_asm(tmp_path, blink("PA1"), "ch32v003")
        assert "li\tt2, 0x40010800" in asm          # GPIOA_CFGLR
        assert "li\tt2, 0x4001080C" in asm          # GPIOA_BSHR
        assert "ori\tt0, t0, 4" in asm              # IOPAEN is bit 2

    def test_port_c_clock_bit(self, tmp_path):
        asm = compile_asm(tmp_path, blink("PC0"), "ch32v003")
        assert "ori\tt0, t0, 16" in asm             # IOPCEN is bit 4

    def test_pin_above_the_port_width_is_rejected(self, tmp_path):
        # The V003's ports stop at 7.
        with pytest.raises(AssertionError):
            compile_asm(tmp_path, blink("PD9"), "ch32v003")


# ---------------------------------------------------------------------------
# CH32V203 -- ports A-D, 16 pins, configuration split across CFGLR and CFGHR
# ---------------------------------------------------------------------------

class TestCh32v203:
    def test_low_half_uses_cfglr(self, tmp_path):
        asm = compile_asm(tmp_path, blink("PA5"), "ch32v203")
        assert "li\tt2, 0x40010800" in asm          # GPIOA_CFGLR
        assert "li\tt1, 1048576" in asm             # nibble at 5 * 4 = 20
        assert "li\tt2, 0x40010810" in asm          # GPIOA_BSHR
        assert "li\tt0, 32" in asm                  # set   -> 1 << 5
        assert "li\tt0, 2097152" in asm             # reset -> 1 << (5 + 16)

    def test_high_half_uses_cfghr(self, tmp_path):
        # This is the case the V003 map could not express at all.
        asm = compile_asm(tmp_path, blink("PB12"), "ch32v203")
        assert "li\tt2, 0x40010C04" in asm          # GPIOB_CFGHR, not CFGLR
        assert "li\tt1, 65536" in asm               # nibble at (12 % 8) * 4 = 16
        assert "li\tt2, 0x40010C10" in asm          # GPIOB_BSHR
        assert "li\tt0, 4096" in asm                # set   -> 1 << 12
        assert "ori\tt0, t0, 8" in asm              # IOPBEN is bit 3

    def test_top_pin_of_the_high_half(self, tmp_path):
        asm = compile_asm(tmp_path, blink("PC15"), "ch32v203")
        assert "li\tt2, 0x40011004" in asm          # GPIOC_CFGHR
        assert "li\tt1, 268435456" in asm           # nibble at (15 % 8) * 4 = 28
        assert "li\tt0, 32768" in asm               # set -> 1 << 15

    def test_port_b_exists_only_on_the_v203(self, tmp_path):
        compile_asm(tmp_path, blink("PB0"), "ch32v203")
        with pytest.raises(AssertionError):
            compile_asm(tmp_path, blink("PB0"), "ch32v003")

    def test_toggle_uses_outdr(self, tmp_path):
        asm = compile_asm(tmp_path, blink("PD8"), "ch32v203")
        assert "li\tt2, 0x4001140C" in asm          # GPIOD_OUTDR
        assert "xori\tt0, t0, 256" in asm           # 1 << 8

# tests/stdlib/test_riscv_timebase.py
#
# micros()/millis() on the CH32V003 read a SysTick time base that millis_init() starts. Like the
# ATmega's Timer0, it has to be armed before the first read, and the driver does that when the
# compiler reports a resolved reader with [NEEDS_TIMEBASE]. Running the firmware (the counter
# really advances, async sleeps really wait) is the oracle sweep's job; here the token and the
# assembly are pinned.

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


def compile_program(tmp_path: Path, source: str, freq: int = 24_000_000):
    src = tmp_path / "main.py"
    src.write_text(source)
    mir = tmp_path / "firmware.mir"
    frontend = subprocess.run(
        [str(PYMCUC), str(src), "-o", str(tmp_path / "out.bin"),
         "--target", "ch32v003", "--freq", str(freq), "-I", str(STDLIB), "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    out = frontend.stdout + frontend.stderr
    if "[BUILD_OK]" not in frontend.stdout:
        return out, None
    asm = tmp_path / "firmware.asm"
    backend = subprocess.run(
        [str(BACKEND), str(mir), "-o", str(asm), "--target", "ch32v003", "--arch", "riscv"],
        capture_output=True, text=True,
    )
    assert backend.returncode == 0, backend.stdout + backend.stderr
    return out, asm.read_text()


READER = "from pymcu.time import micros\n\ndef main():\n    v = micros()\n"
WITH_INIT = (
    "from pymcu.hal.timer import millis_init\n"
    "from pymcu.time import micros, millis\n"
    "millis_init()\n"
    "a = micros()\n"
    "b = millis()\n"
)


def test_a_program_that_arms_it_itself_reports_that(tmp_path):
    out, _ = compile_program(tmp_path, WITH_INIT)

    assert "[TIMEBASE_INIT]" in out


def test_the_time_base_is_systick_overflowing_once_a_millisecond(tmp_path):
    _, asm = compile_program(tmp_path, WITH_INIT)

    assert "li\tt2, 0xE000F010" in asm          # SysTick CMP
    assert "li\tt0, 24000" in asm               # 24 MHz / 1000: a millisecond of HCLK
    assert "li\tt0, 47" in asm                  # CTLR: INIT | STRE | STCLK | STIE | STE
    assert "li\tt2, 0xE000E100" in asm          # PFIC IENR1
    assert "li\tt0, 4096" in asm                # IRQ 12 (bit 12)


def test_the_systick_handler_sits_in_vector_12(tmp_path):
    _, asm = compile_program(tmp_path, WITH_INIT)

    words = [l.strip() for l in asm.splitlines() if l.strip().startswith(".word")]
    assert "_systick_isr" in words[11]          # words[k] is vector k + 1; the first slot is j _start


def test_a_clock_that_is_not_whole_megahertz_is_refused(tmp_path):
    out, asm = compile_program(tmp_path, WITH_INIT, freq=12_500_000)

    assert asm is None
    assert "whole number of MHz" in out

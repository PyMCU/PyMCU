# tests/stdlib/test_rp_ws2812.py
#
# WS2812 pixels on the RP2040 and RP2350 go through a PIO state machine (hal/rp/ws2812.py).
# The register stores that set it up are constants once the pin and the clock are known, so the
# MIR of the frontend shows them: which block is released from reset, where the program lands,
# the divider that makes 800 kHz bits, the side-set pin. A wrong address or divider is invisible
# until a strip stays dark. The waveform itself is measured in the emulator (pymcu-arm,
# Ws2812PioTests); the program words are checked against the compiler's PIO assembler
# (tests/unit Ws2812PioProgramTests).

import json
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")

PIO0 = 0x50200000

PROGRAM = (
    "from pymcu.hal.ws2812 import ws2812_init, ws2812_write_byte, ws2812_reset\n\n"
    "def main():\n"
    "    ws2812_init({pin})\n"
    "    ws2812_write_byte({pin}, 255)\n"
    "    ws2812_reset({pin})\n"
)


def stores(tmp_path: Path, chip: str, freq: int, pin: int) -> list[tuple[int, int]]:
    """Every constant store to a fixed address in main, in order: (address, value)."""
    src = tmp_path / "main.py"
    src.write_text(PROGRAM.format(pin=pin))
    mir = tmp_path / "firmware.mir"
    out = subprocess.run(
        [str(PYMCUC), str(src), "--target", chip, "--freq", str(freq),
         "-I", str(STDLIB), "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    assert "[BUILD_OK]" in out.stdout, out.stdout + out.stderr
    program = json.loads(mir.read_text())
    main = next(f for f in program["functions"] if f["name"] == "main")
    return [
        (i["dst"]["address"], i["src"]["value"])
        for i in main["body"]
        if i["$t"] == "copy" and i["dst"]["$t"] == "mem" and i["src"]["$t"] == "const"
    ]


@pytest.mark.parametrize("chip,freq,io_bank,pads,reset_bit,div_int,div_frac", [
    ("rp2040", 125_000_000, 0x40014000, 0x4001C000, 10, 15, 160),
    ("rp2350", 150_000_000, 0x40028000, 0x40038000, 11, 18, 192),
])
class TestWs2812Setup:
    def test_the_program_sits_at_the_top_of_instruction_memory(
            self, tmp_path, chip, freq, io_bank, pads, reset_bit, div_int, div_frac):
        s = stores(tmp_path, chip, freq, 16)

        for k, word in enumerate([0x6221, 0x113F, 0x141C, 0xA442]):
            assert (PIO0 + 0x048 + 4 * (28 + k), word) in s

    def test_state_machine_3_runs_at_eight_megahertz(
            self, tmp_path, chip, freq, io_bank, pads, reset_bit, div_int, div_frac):
        s = stores(tmp_path, chip, freq, 16)

        assert (PIO0 + 0x0C8 + 0x18 * 3, (div_int << 16) | (div_frac << 8)) in s

    def test_one_side_set_pin_and_the_wrap_over_the_program(
            self, tmp_path, chip, freq, io_bank, pads, reset_bit, div_int, div_frac):
        s = stores(tmp_path, chip, freq, 16)

        assert (PIO0 + 0x0CC + 0x18 * 3, (31 << 12) | (28 << 7)) in s
        assert (PIO0 + 0x0DC + 0x18 * 3, (1 << 29) | (16 << 10) | (1 << 26) | (16 << 5)) in s
        assert (PIO0 + 0x0D0 + 0x18 * 3, (1 << 17) | (8 << 25) | (1 << 30)) in s

    def test_the_pin_is_routed_to_pio0_and_its_pad_enabled(
            self, tmp_path, chip, freq, io_bank, pads, reset_bit, div_int, div_frac):
        s = stores(tmp_path, chip, freq, 16)

        assert (io_bank + 8 * 16 + 4, 6) in s
        assert (pads + 4 + 4 * 16, 1 << 6) in s

    def test_pio0_is_taken_out_of_reset(
            self, tmp_path, chip, freq, io_bank, pads, reset_bit, div_int, div_frac):
        s = stores(tmp_path, chip, freq, 16)

        assert any(addr in (0x4000F000, 0x40023000) and value & (1 << reset_bit) for addr, value in s)

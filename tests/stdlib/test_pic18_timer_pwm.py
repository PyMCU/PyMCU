import json
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

T0CON = 0xFD5
TMR0L = 0xFD6
TMR0H = 0xFD7
INTCON = 0xFF2
T2CON = 0xFBA
PR2 = 0xFBB

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(),
    reason="compiler binary not built (run `just build`)",
)


def build_ir(tmp_path: Path, source: str):
    src = tmp_path / "main.py"
    src.write_text(source)
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(src), "-o", "/dev/null", "--arch", "pic18",
         "--target", "pic18f45k50", "--freq", "16000000", "-I", str(STDLIB),
         "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr
    return json.loads(mir.read_text())


def const_stores(ir) -> dict:
    found = {}
    for func in ir["functions"]:
        for ins in func["body"]:
            dst = ins.get("dst")
            src = ins.get("src")
            if (isinstance(dst, dict) and dst.get("$t") == "mem"
                    and isinstance(src, dict) and src.get("$t") == "const"):
                found.setdefault(dst["address"], set()).add(src["value"])
    return found


def loads_from(ir) -> set:
    found = set()
    for func in ir["functions"]:
        for ins in func["body"]:
            src = ins.get("src")
            if isinstance(src, dict) and src.get("$t") == "mem":
                found.add(src["address"])
    return found


TIMER = (
    "from pymcu.types import uint8, uint16\n"
    "from pymcu.hal.pic18.timer import Timer\n"
    "from pymcu.chips.pic18f45k50 import LATD, TRISD\n"
    "\n"
    "def main():\n"
    "    TRISD.value = 0\n"
    "    t = Timer(0, 256)\n"
    "    t.start()\n"
    "    while True:\n"
    "        c: uint16 = t.counter()\n"
    "        LATD.value = c & 0xFF\n"
)


def test_timer0_runs_in_16_bit_mode(tmp_path):
    written = const_stores(build_ir(tmp_path, TIMER))
    assert T0CON in written
    for value in written[T0CON]:
        assert not (value & 0x40), f"T08BIT set in T0CON=0x{value:02X}: TMR0H is dead in 8-bit mode"


def test_counter_reads_both_halves_of_tmr0(tmp_path):
    read = loads_from(build_ir(tmp_path, TIMER))
    assert TMR0L in read
    assert TMR0H in read


def test_overflow_reads_the_real_flag_not_a_constant(tmp_path):
    source = TIMER.replace(
        "        c: uint16 = t.counter()\n        LATD.value = c & 0xFF\n",
        "        o: uint8 = t.overflow()\n        LATD.value = o\n")
    ir = build_ir(tmp_path, source)
    bits = {(i["source"]["address"], i["bit"])
            for f in ir["functions"] for i in f["body"]
            if i.get("$t") in ("jbs", "jbc") and isinstance(i.get("source"), dict)}
    bits |= {(i["source"]["address"], i["bit"])
             for f in ir["functions"] for i in f["body"]
             if i.get("$t") == "bchk" and isinstance(i.get("source"), dict)}
    assert any(addr == INTCON for addr, _ in bits) or INTCON in loads_from(ir)


def pwm(freq: int) -> str:
    return (
        "from pymcu.types import uint8\n"
        "from pymcu.hal.pic18.pwm import PWM\n"
        "from pymcu.chips.pic18f45k50 import TRISD, ANSELC\n"
        "\n"
        "def main():\n"
        "    ANSELC.value = 0\n"
        "    TRISD.value = 0\n"
        f"    p = PWM(\"RC2\", 128, {freq})\n"
        "    p.start()\n"
        "    while True:\n"
        "        pass\n"
    )


@pytest.mark.parametrize("freq,prescaler_bits", [
    (15000, 0x00), (10000, 0x00), (3000, 0x01), (2000, 0x01), (500, 0x02), (100, 0x02)])
def test_pwm_picks_the_timer2_prescaler_for_the_requested_frequency(tmp_path, freq, prescaler_bits):
    written = const_stores(build_ir(tmp_path, pwm(freq)))
    assert T2CON in written
    assert any((v & 0x03) == prescaler_bits for v in written[T2CON]), \
        f"{freq} Hz -> T2CON {[hex(v) for v in written[T2CON]]}, expected prescaler bits {prescaler_bits:#04x}"


def test_pwm_keeps_full_duty_resolution(tmp_path):
    written = const_stores(build_ir(tmp_path, pwm(3000)))
    assert PR2 in written and 0xFF in written[PR2]

import json
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

TMR0, GPIO, OPTION = 0x01, 0x06, 0x81

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")


def build_ir(tmp_path: Path, body: str):
    src = tmp_path / "main.py"
    src.write_text(
        "from pymcu.types import uint8, uint16\n"
        "from pymcu.hal.timer import Timer\n"
        "from pymcu.chips.pic10f200 import GPIO\n"
        "\n"
        "def main():\n"
        + body
    )
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(src), "-o", "/dev/null", "--arch", "pic12",
         "--target", "pic10f200", "--freq", "4000000", "-I", str(STDLIB),
         "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    return proc, (json.loads(mir.read_text()) if mir.exists() else None)


def instructions(ir):
    return [i for f in ir["functions"] for i in f["body"] if i["$t"] != "dbg"]


def writes_to(ir, address):
    """Every constant written to an SFR, so a wrong bit pattern is visible."""
    out = set()
    for ins in instructions(ir):
        dst = ins.get("dst")
        src = ins.get("src")
        if isinstance(dst, dict) and dst.get("address") == address:
            if isinstance(src, dict) and src.get("$t") == "const":
                out.add(src.get("value"))
    return out


def reads_from(ir, address):
    return any(isinstance(i.get("src"), dict) and i["src"].get("address") == address
               for i in instructions(ir))


READ_COUNTER = (
    "    t = Timer(0, 2)\n"
    "    while True:\n"
    "        GPIO.value = uint8(t.counter())\n"
)


def test_counter_actually_reads_tmr0(tmp_path):
    """The stub returned a literal 0: a running timer that always reads zero."""
    _, ir = build_ir(tmp_path, READ_COUNTER)
    assert ir is not None, "the program did not compile at all"
    assert reads_from(ir, TMR0), \
        "counter() never touches TMR0 -- it is returning a constant"


def test_the_timer_program_emits_something(tmp_path):
    """Anti-stub: a body of `pass` compiles clean and proves nothing."""
    _, ir = build_ir(tmp_path, READ_COUNTER)
    kinds = {i["$t"] for i in instructions(ir)}
    assert kinds - {"lbl", "jmp"}, \
        "the whole program folded to a bare loop: every call was a stub"


@pytest.mark.parametrize("prescaler,ps", [
    (2, 0), (4, 1), (8, 2), (16, 3), (32, 4), (64, 5), (128, 6), (256, 7)])
def test_prescaler_selects_the_right_divider(tmp_path, prescaler, ps):
    _, ir = build_ir(tmp_path, READ_COUNTER.replace("Timer(0, 2)", f"Timer(0, {prescaler})"))
    assert 0xC0 | ps in writes_to(ir, OPTION), \
        f"prescaler {prescaler} must write OPTION=0x{0xC0 | ps:02X}"


def test_the_option_write_leaves_the_gpio_bits_alone(tmp_path):
    """OPTION is write-only on this core, so the whole byte is written at once.

    NOT_GPWU<7> and NOT_GPPU<6> are active low: a write of 0x00 silently turns
    on wake-on-pin-change and the weak pull-ups while configuring a timer.
    """
    _, ir = build_ir(tmp_path, READ_COUNTER)
    for value in writes_to(ir, OPTION):
        assert value & 0xC0 == 0xC0, \
            f"OPTION=0x{value:02X} clears NOT_GPWU/NOT_GPPU and changes GPIO behaviour"


def test_a_prescaler_the_chip_cannot_divide_by_is_refused(tmp_path):
    """3 is not a power of two: the stub matched nothing and wrote nothing."""
    proc, _ = build_ir(tmp_path, READ_COUNTER.replace("Timer(0, 2)", "Timer(0, 3)"))
    assert "[BUILD_FAIL]" in proc.stdout, \
        "an unsupported prescaler must not compile to a timer left at its reset value"


def test_prescaler_256_is_not_truncated(tmp_path):
    """256 does not fit in the uint8 the chip helper used to declare."""
    _, ir = build_ir(tmp_path, READ_COUNTER.replace("Timer(0, 2)", "Timer(0, 256)"))
    assert 0xC7 in writes_to(ir, OPTION)


@pytest.mark.parametrize("call,why", [
    ("t.stop()", "Timer0 free-runs off the instruction clock; there is no enable bit"),
    ("t.overflow()", "the baseline core has no T0IF flag"),
    ("t.set_compare(10)", "there is no compare hardware"),
    ("t.irq(handler)", "the baseline core has no interrupts at all"),
])
def test_what_the_chip_cannot_do_does_not_compile(tmp_path, call, why):
    body = (
        "    t = Timer(0, 2)\n"
        "    while True:\n"
        f"        {call}\n"
    )
    if "irq" in call:
        body = "def handler():\n    pass\n\n\ndef main():\n" + body
        src = tmp_path / "main.py"
        src.write_text(
            "from pymcu.types import uint8, uint16\n"
            "from pymcu.hal.timer import Timer\n"
            "from pymcu.chips.pic10f200 import GPIO\n\n" + body)
        mir = tmp_path / "firmware.mir"
        proc = subprocess.run(
            [str(PYMCUC), str(src), "-o", "/dev/null", "--arch", "pic12",
             "--target", "pic10f200", "--freq", "4000000", "-I", str(STDLIB),
             "--emit-ir", str(mir)], capture_output=True, text=True)
    else:
        proc, _ = build_ir(tmp_path, body)
    assert "[BUILD_FAIL]" in proc.stdout, f"must be refused: {why}"


def test_start_is_a_real_no_op_not_a_stub(tmp_path):
    """start() is the one that honestly does nothing: the timer is already running."""
    proc, ir = build_ir(tmp_path, READ_COUNTER.replace(
        "    while True:", "    t.start()\n    while True:"))
    assert "[BUILD_OK]" in proc.stdout, \
        "start() must stay callable: on this core the timer runs from reset"

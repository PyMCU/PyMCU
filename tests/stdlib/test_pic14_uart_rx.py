import json
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"
CHIPS = STDLIB / "pymcu" / "chips"

RX_FLAG = {
    "pic16f18877": (0x70F, 5),
    "pic16f877a": (0x0C, 5),
}

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(),
    reason="compiler binary not built (run `just build`)",
)


def program(chip: str, port: str) -> str:
    return (
        "from pymcu.types import uint8\n"
        "from pymcu.hal.pic14.uart import UART\n"
        f"from pymcu.chips.{chip} import {port}, TRISD\n"
        "\n"
        "def main():\n"
        "    TRISD.value = 0\n"
        "    u = UART(9600)\n"
        "    while True:\n"
        "        if u.available() == 1:\n"
        f"            {port}.value = 1\n"
    )


def bit_tests(tmp_path: Path, chip: str, port: str):
    src = tmp_path / "main.py"
    src.write_text(program(chip, port))
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(src), "-o", "/dev/null", "--arch", "pic14",
         "--target", chip, "--freq", "8000000", "-I", str(STDLIB),
         "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr
    found = set()
    for func in json.loads(mir.read_text())["functions"]:
        for ins in func["body"]:
            if ins.get("$t") in ("jbs", "jbc") and isinstance(ins.get("source"), dict):
                found.add((ins["source"]["address"], ins["bit"]))
    return found


@pytest.mark.parametrize("chip,port", [("pic16f18877", "LATD"), ("pic16f877a", "PORTD")])
def test_available_polls_the_receive_flag_of_that_chip(tmp_path, chip, port):
    assert RX_FLAG[chip] in bit_tests(tmp_path, chip, port)


def test_available_is_not_a_constant_zero(tmp_path):
    assert bit_tests(tmp_path, "pic16f877a", "PORTD")


@pytest.mark.parametrize("name,reg,bit", [
    ("RCIF", "PIR3", 5), ("TXIF", "PIR3", 4),
    ("ADIF", "PIR1", 0), ("TMR1IF", "PIR4", 0), ("TMR2IF", "PIR4", 1),
    ("TMR0IF", "PIR0", 5), ("CCP1IF", "PIR6", 0), ("CCP2IF", "PIR6", 1),
])
def test_interrupt_flag_lives_where_the_datasheet_puts_it(name, reg, bit):
    """The F1 generation moved these out of PIR1; the map was the F877A's."""
    text = (CHIPS / "pic16f18877.py").read_text()
    block = text.split(f"# {reg} Bits", 1)
    assert len(block) == 2, f"no {reg} bit block"
    body = block[1].split("\n\n", 1)[0]
    assert any(l.split(":")[0].strip() == name and l.split("=")[1].strip() == str(bit)
               for l in body.splitlines() if ":" in l and "=" in l), \
        f"{name} is not {reg}<{bit}>:\n{body}"

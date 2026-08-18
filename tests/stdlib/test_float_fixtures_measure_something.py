import json
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")


def program(expr: str) -> str:
    return (
        "from pymcu.types import uint8, uint32\n"
        "from pymcu.chips.pic18f45k50 import LATD, TRISD, ANSELD, PORTB\n"
        "\n"
        "def main():\n"
        "    ANSELD.value = 0\n"
        "    TRISD.value = 0\n"
        "    s: uint8 = PORTB.value\n"
        "    f: float = float(s)\n"
        "    while True:\n"
        f"        u: uint32 = bitcast(uint32, {expr})\n"
        "        LATD.value = u & 0xFF\n"
    )


def float_ops(tmp_path: Path, expr: str) -> int:
    src = tmp_path / "main.py"
    src.write_text(program(expr))
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(src), "-o", "/dev/null", "--arch", "pic18",
         "--target", "pic18f45k50", "--freq", "16000000", "-I", str(STDLIB),
         "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr
    ir = json.loads(mir.read_text())
    count = 0
    for func in ir["functions"]:
        for ins in func["body"]:
            if ins.get("$t") != "binary":
                continue
            for slot in ("src1", "src2"):
                operand = ins.get(slot)
                if isinstance(operand, dict) and operand.get("type") == 6:
                    count += 1
                    break
    return count


LITERAL_ONLY = ["0.1 + 0.2", "1.0 / 3.0", "2.0 / 3.0", "0.1 * 3.0", "16777216.0 - 16777215.0"]
SEEDED = ["(0.1 + f) + 0.2", "(1.0 + f) / 3.0", "(2.0 + f) / 3.0", "(0.1 + f) * 3.0",
          "(16777216.0 + f) - 16777215.0"]


@pytest.mark.parametrize("expr", LITERAL_ONLY)
def test_literal_only_float_arithmetic_is_folded_away(tmp_path, expr):
    """Documents the trap: such a fixture would pass without running the soft-float."""
    assert float_ops(tmp_path, expr) == 0


@pytest.mark.parametrize("expr", SEEDED)
def test_one_volatile_operand_defeats_the_folding(tmp_path, expr):
    assert float_ops(tmp_path, expr) > 0

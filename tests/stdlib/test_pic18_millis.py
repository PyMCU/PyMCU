import json
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

T0CON = 0xFD5
TMR0L = 0xFD6
INTCON = 0xFF2

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(),
    reason="compiler binary not built (run `just build`)",
)

MILLIS = (
    "from pymcu.types import uint8, uint32\n"
    "from pymcu.time import millis_init, millis, micros\n"
    "from pymcu.chips.pic18f45k50 import LATD, TRISD\n"
    "\n"
    "def main():\n"
    "    TRISD.value = 0\n"
    "    millis_init()\n"
    "    while True:\n"
    "        m: uint32 = millis()\n"
    "        u: uint32 = micros()\n"
    "        LATD.value = (m + u) & 0xFF\n"
)

ASYNC = (
    "import asyncio\n"
    "from pymcu.time import millis_init\n"
    "from pymcu.chips.pic18f45k50 import LATD, TRISD, ANSELD\n"
    "\n"
    "async def fast():\n"
    "    while True:\n"
    "        LATD[0] = 1\n"
    "        await asyncio.sleep_ms(50)\n"
    "        LATD[0] = 0\n"
    "        await asyncio.sleep_ms(50)\n"
    "\n"
    "async def slow():\n"
    "    while True:\n"
    "        LATD[3] = 1\n"
    "        await asyncio.sleep_ms(125)\n"
    "        LATD[3] = 0\n"
    "        await asyncio.sleep_ms(125)\n"
    "\n"
    "def main():\n"
    "    ANSELD.value = 0\n"
    "    TRISD.value = 0\n"
    "    millis_init()\n"
    "    asyncio.gather(fast(), slow())\n"
)


def build(tmp_path: Path, source: str, freq: int = 16_000_000):
    src = tmp_path / "main.py"
    src.write_text(source)
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(src), "-o", "/dev/null", "--arch", "pic18",
         "--target", "pic18f45k50", "--freq", str(freq), "-I", str(STDLIB),
         "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    return proc, (json.loads(mir.read_text()) if mir.exists() else None)


def const_stores(ir) -> dict:
    found = {}
    for func in ir["functions"]:
        for ins in func["body"]:
            dst, src = ins.get("dst"), ins.get("src")
            if (isinstance(dst, dict) and dst.get("$t") == "mem"
                    and isinstance(src, dict) and src.get("$t") == "const"):
                found.setdefault(dst["address"], set()).add(src["value"])
    return found


def test_millis_compiles_on_pic18_instead_of_refusing(tmp_path):
    proc, _ = build(tmp_path, MILLIS)
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr


@pytest.mark.parametrize("freq,t0con", [
    (4_000_000, 0xC1), (8_000_000, 0xC2), (16_000_000, 0xC3)])
def test_millis_arms_timer0_at_1024us_for_this_clock(tmp_path, freq, t0con):
    _, ir = build(tmp_path, MILLIS, freq)
    assert t0con in const_stores(ir).get(T0CON, set())


def test_millis_zeroes_its_accumulators_at_init(tmp_path):
    _, ir = build(tmp_path, MILLIS)
    zeroed = [name for func in ir["functions"] for ins in func["body"]
              if ins.get("$t") == "copy"
              and isinstance(ins.get("dst"), dict) and ins["dst"].get("$t") == "var"
              and "_millis_" in str(ins["dst"].get("name", ""))
              and isinstance(ins.get("src"), dict) and ins["src"].get("$t") == "const"
              and ins["src"]["value"] == 0
              for name in [ins["dst"]["name"]]]
    for suffix in ("_millis_count", "_millis_ms", "_millis_fract"):
        assert any(n.endswith(suffix) for n in zeroed), f"{suffix} never zeroed"


def test_micros_reads_the_hardware_counter(tmp_path):
    _, ir = build(tmp_path, MILLIS)
    read = {ins["src"]["address"] for func in ir["functions"] for ins in func["body"]
            if isinstance(ins.get("src"), dict) and ins["src"].get("$t") == "mem"}
    assert TMR0L in read


def test_async_is_no_longer_gated_on_pic18(tmp_path):
    proc, _ = build(tmp_path, ASYNC)
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr
    assert "needs a timebase" not in proc.stdout + proc.stderr

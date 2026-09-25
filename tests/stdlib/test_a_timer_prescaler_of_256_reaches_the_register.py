"""The largest prescaler a PIC timer offers is 256, so its parameter is uint16.

`Timer.__init__` declares `prescaler: uint16`, but three of the per-chip
`timer0_init` helpers behind it declared `uint8`. Their `elif prescaler == 256`
arm could never be taken, and because the helpers are @inline the mismatch was
invisible: the argument was substituted as written, 256 matched, and the
register was configured by an arm the declared type says is unreachable.

Once an @inline argument is narrowed to its parameter's width -- as a call to a
real subroutine always was -- 256 became 0, no arm matched, and `Timer(0, 256)`
configured nothing at all. The declaration was the thing that was wrong.
"""

import json
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(),
    reason="compiler binary not built (run `just build`)",
)

PROGRAM = (
    "from pymcu.types import uint16\n"
    "from pymcu.hal.{hal}.timer import Timer\n"
    "\n"
    "t = Timer(0, 256)\n"
    "t.start()\n"
)

# arch, chip, hal package, the register timer0_init writes, and the value the
# 256 arm selects on that part.
TARGETS = [
    ("pic14", "pic16f877a", "pic14", 0x0081, 0x87),    # OPTION_REG, PS = 1:256
    ("pic14-84a", "pic16f84a", "pic14", 0x0081, 0x87),  # OPTION_REG, PS = 1:256
    ("pic14e", "pic16f18877", "pic14", 0x001F, 0x48),  # T0CON1, CKPS = 1:256
    ("pic18", "pic18f45k50", "pic18", 0x0FD5, 0x07),   # T0CON, T0PS = 1:256
    ("pic12", "pic10f200", "pic12", 0x0081, 0xC7),     # OPTION, PS = 1:256
]


def const_stores(tmp_path, arch, chip, hal):
    src = tmp_path / "main.py"
    src.write_text(PROGRAM.format(hal=hal))
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(src), "-o", "/dev/null", "--arch", arch,
         "--target", chip, "--freq", "4000000", "-I", str(STDLIB),
         "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr
    found = {}
    for func in json.loads(mir.read_text())["functions"]:
        for ins in func["body"]:
            dst, src_ = ins.get("dst"), ins.get("src")
            if (isinstance(dst, dict) and dst.get("$t") == "mem"
                    and isinstance(src_, dict) and src_.get("$t") == "const"):
                found.setdefault(dst["address"], set()).add(src_["value"])
    return found


@pytest.mark.parametrize("arch,chip,hal,reg,value", TARGETS, ids=[t[0] for t in TARGETS])
def test_the_largest_prescaler_configures_the_timer(tmp_path, arch, chip, hal, reg, value):
    written = const_stores(tmp_path, arch, chip, hal)
    assert reg in written, \
        f"{arch}: Timer(0, 256) wrote nothing to 0x{reg:04X}; the 256 arm was not taken"
    assert value in written[reg], \
        f"{arch}: 0x{reg:04X} got {[hex(v) for v in written[reg]]}, not 0x{value:02X}"

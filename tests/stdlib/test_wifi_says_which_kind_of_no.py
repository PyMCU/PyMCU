"""The WiFi facade has to say WHICH kind of no, and one of them it cannot say yet.

The CYW43439 is not part of any RP2xxx chip. It is a separate part soldered next to it on some
boards and absent on others, and the silicon is identical either way: a Pico and a Pico W are
both rp2040, a Pico 2 and a Pico 2 W are both rp2350. So the question the facade has to answer
is about the BOARD, and the only thing it is given is the chip.

That is not a wording problem, it is a missing fact, and it is worth stating precisely because
the wording has already been rewritten twice without it. `DeviceConfig` carries Chip,
TargetChip, DetectedChip, Arch, Frequency and the memory sizes; there is no board field.
`pymcuc` has no board flag. `__CHIP__` exposes name, arch, ram_size and flash_size. The driver
maps a board name to a chip and passes only the chip. A Pico and a Pico W are the same input.

    not an RP2xxx chip     no board in the family carries the radio     REFUSED, closed here
    rp2040 or rp2350       the W boards have the radio, the plain
                           ones do not, and both are the same chip      CANNOT BE DECIDED

The second is pinned below as it stands rather than left to a comment, because it is the one
that produces a wrong binary and the one most likely to be "fixed" by someone who has not
measured.
"""

import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")


PROGRAM = (
    "from pymcu.hal.wifi import CYW43\n"
    "from pymcu.types import uint8\n\n\n"
    "def main() -> None:\n"
    "    w = CYW43()\n"
    "    w.init()\n"
    "    while True:\n"
    "        pass\n"
)


def _compile(tmp_path: Path, target: str):
    src = tmp_path / "main.py"
    src.write_text(PROGRAM)
    proc = subprocess.run(
        [str(PYMCUC), str(src), "--target", target,
         "-I", str(tmp_path), "-I", str(STDLIB),
         "--emit-ir", str(tmp_path / "firmware.mir")],
        capture_output=True, text=True,
    )
    # The exit code and not the text. A bootstrap failure prints `[Warning]` and `[BUILD_FAIL]`
    # and never the word `error`, so a check that greps for it reads an abort as a success.
    return proc.returncode, proc.stdout + proc.stderr


def test_a_chip_outside_the_family_is_refused(tmp_path):
    """The one answer that needs no board knowledge, so it is the one that can be closed.

    It also has to keep saying WHICH kind of no. An earlier version of this facade said "only
    supported on the Pico 2 W (rp2350) so far" to everyone, which reads as "your board cannot do
    this" to someone holding a board that can.
    """
    rc, out = _compile(tmp_path, "atmega328p")

    assert rc != 0
    assert "not available on this chip" in out
    assert "separate part carried by some RP2xxx boards" in out


@pytest.mark.parametrize("chip", ["rp2040", "rp2350"])
def test_an_rp2xxx_compiles_even_for_a_board_with_no_radio(tmp_path, chip):
    """PINNED AS IT STANDS, and it is wrong. Read the reason before changing it.

    A plain Pico is rp2040 and a plain Pico 2 is rp2350, and neither carries a CYW43439. This
    program compiles for both: a binary with a radio driver in it for a board with no radio.
    That is the only defect on this axis that produces wrong output rather than a poor message.

    It covers BOTH chips, and it did not always: while the driver was wired for rp2350 only,
    rp2040 was refused and this pinned one chip. Wiring the Pico W closed a real gap and widened
    this one at the same time, which is the honest consequence and not a complaint.

    It is not fixed here because it CANNOT be, with what the compiler is given. The guard has
    two options and both are wrong:

        accept the chip   a plain Pico or Pico 2 keeps compiling WiFi     <- this, today
        refuse the chip   breaks the W boards, the ones that work

    There is no third option while the board identity stops at the driver.

    WHEN THE BOARD FIELD EXISTS: this test should fail, and the fix is to assert that a plain
    Pico and a plain Pico 2 are refused while a Pico W and a Pico 2 W are not. Deleting it, or
    relaxing it back to "an rp2xxx compiles", restores the wrong binary.

    And it will only fail for a program that DECLARES its board. Measured over the three trees:
    344 projects set `target` and 30 set `board`, and the four WiFi programs that exist all set
    `target`. For those the answer stays the same, because the compiler is still not told which
    board it is. The field does not fix the silence for everyone; it fixes it for whoever says
    which board they have.
    """
    rc, out = _compile(tmp_path, chip)

    assert rc == 0, out
    assert "not available" not in out

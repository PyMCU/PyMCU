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


def _compile(tmp_path: Path, target: str, board: str | None = None):
    src = tmp_path / "main.py"
    src.write_text(PROGRAM)
    cmd = [str(PYMCUC), str(src), "--target", target,
           "-I", str(tmp_path), "-I", str(STDLIB),
           "--emit-ir", str(tmp_path / "firmware.mir")]
    if board is not None:
        cmd += ["--board", board]
    proc = subprocess.run(
        cmd,
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


@pytest.mark.parametrize("chip,board", [("rp2040", "pico"), ("rp2350", "pico2")])
def test_a_board_that_was_named_and_has_no_radio_is_refused(tmp_path, chip, board):
    """The case the board field bought, and the only one where "use another board" is the advice.

    This test was the pinned one. It asserted that an rp2xxx compiles even for a board with no
    radio, with the instruction that when the board field existed the fix was to assert that a
    plain Pico and a plain Pico 2 are refused while the W boards are not. The field exists, and
    this is that instruction being followed.
    """
    rc, out = _compile(tmp_path, chip, board)

    assert rc != 0
    assert "this board has no WiFi radio" in out
    # It must not read as "not implemented": the radio is absent, not the driver.
    assert "not implemented" not in out


@pytest.mark.parametrize("chip,board", [("rp2040", "pico_w"), ("rp2040", "raspberry_pi_pico_w"),
                                        ("rp2350", "pico2_w"), ("rp2350", "raspberry_pi_pico2_w")])
def test_a_board_that_carries_the_radio_compiles(tmp_path, chip, board):
    """All four spellings the driver accepts, because the guard cannot know which one survives.

    Of the two ways to be wrong here, refusing a real Pico W is loud and gets reported; building
    a radio into a firmware for a board without one is silent. So the list is a whitelist, and
    that couples it to src/driver/core/boards.py: a W board added there and not to the list in
    pymcu/hal/wifi.py is refused, and the message will look like a bug in the HAL.
    """
    rc, out = _compile(tmp_path, chip, board)

    assert rc == 0, out


@pytest.mark.parametrize("chip", ["rp2040", "rp2350"])
def test_no_board_given_still_compiles_and_that_is_deliberate(tmp_path, chip):
    """PINNED, and it is the half the field does NOT fix. Read this before "finishing" it.

    A program built with `target = "rp2040"` never tells the compiler which board it is, so this
    still compiles for a plain Pico. That is not the field failing: a project sets `board` or
    `target` and the driver refuses both at once, so a target build has no board to give.

    Measured over the three trees: 344 projects set target, 30 set board, and all four WiFi
    programs that exist set target, the Pico 2 W demo included. A guard that treated "" as "no"
    would take WiFi away from every one of them.

    So the honest summary is narrower than "the board field closed the hole": a program can now
    say enough to be protected, and one that does not is answering a question it was never
    asked. Changing this to a refusal breaks 344 projects to protect the ones that did not ask.
    """
    rc, out = _compile(tmp_path, chip)

    assert rc == 0, out

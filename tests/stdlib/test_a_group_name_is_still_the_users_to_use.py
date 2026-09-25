"""A grouped peripheral's name stays available for a user's own class.

RFC 0012 gives each peripheral a class named after it, and that name is spelled the way a
user spells their own classes, because PEP 8 says classes are CapWords. So every group the
project adds takes a plausible name out of the namespace: `Timer1` today, `Timer0`,
`Timer2`, `Adc`, `Spi` and `Uart` as the feature grows. Those are ordinary names for a
class in an embedded program.

PyMCU#497 is what happens when one of them is taken by accident. The refusal for calling a
group registered the BARE spelling as well as the qualified one, and the target's chip file
is scanned on every build whether or not the program imports it, so a user's own
`class Timer1` could not be constructed in any program, and the message told them about
silicon and registers they had never named.

No existing gate could catch that. The ROM gate, the AVR suite and the oracle corpus all
measure programs that ALREADY EXIST, and none of them declares a class named after a
peripheral group. A name reserved in silence is invisible to every one of them, because the
programs it breaks are the ones nobody has written yet. It surfaced because another agent's
probe happened to use the name.

So this is the gate for that class of regression: take the names the feature claims and
compile a user class called each of them. The list is DERIVED from the chip files rather
than written down here, so the day somebody groups `Timer0` the sweep covers it without
anyone remembering to come back.
"""

import re
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"
CHIPS = STDLIB / "pymcu" / "chips"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)"
)

_CLASS = re.compile(r"^class\s+(\w+)\s*:\s*$", re.M)
_REGISTER_ATTR = re.compile(r"^\s+\w+\s*:\s*ptr\[", re.M)


def _grouped_peripherals():
    """(chip, group) for every class in a chip file that declares a register.

    Derived, never listed. A group is a class whose body declares at least one
    `name: ptr[...]` attribute, which is what makes it a register group rather than an
    ordinary class that happens to live in a chip file.
    """
    found = []
    for path in sorted(CHIPS.glob("*.py")):
        if path.name == "__init__.py":
            continue
        text = path.read_text(encoding="utf-8")
        for match in _CLASS.finditer(text):
            body = text[match.end():]
            following = re.search(r"^\S", body, re.M)
            if following is not None:
                body = body[: following.start()]
            if _REGISTER_ATTR.search(body):
                found.append((path.stem, match.group(1)))
    return found


def _compile(tmp_path, source, chip):
    src = tmp_path / "main.py"
    src.write_text(source)
    return subprocess.run(
        [str(PYMCUC), str(src), "-o", str(tmp_path / "out.bin"), "--arch", "avr", "--target", chip,
         "--freq", "16000000", "-I", str(STDLIB), "--emit-ir", str(tmp_path / "fw.mir")],
        capture_output=True, text=True,
    )


def test_the_sweep_found_the_groups_to_sweep():
    # An empty sweep passes every parametrised test below without compiling anything, so
    # the shape of a chip file changing under this regex has to be a failure and not a
    # quiet skip. Timer1 on the ATmega328P is the group RFC 0012 shipped.
    groups = _grouped_peripherals()
    assert ("atmega328p", "Timer1") in groups, groups


@pytest.mark.parametrize("chip,group", _grouped_peripherals())
def test_a_user_class_may_use_a_group_name(tmp_path, chip, group):
    """The user's class wins, and the group is not in the program at all."""
    result = _compile(
        tmp_path,
        f"class {group}:\n"
        "    def __init__(self, n):\n"
        "        self.n = n\n"
        "\n"
        "def main():\n"
        f"    obj = {group}(7)\n",
        chip,
    )
    assert "[BUILD_OK]" in result.stdout, (
        f"a user class named {group!r} was refused on {chip}:\n"
        + result.stdout + result.stderr
    )


@pytest.mark.parametrize("chip,group", _grouped_peripherals())
def test_a_user_class_wins_over_an_imported_group(tmp_path, chip, group):
    """And it wins even when the program imports the group of the same name.

    This is the case that needs the chip module PRESENT to mean anything, and the reason
    is worth keeping next to the fixture rather than in a commit message: the first
    regression test written for PyMCU#497 compiled a lone user class with no chip module
    in the build. Nothing registers a group in that situation, so the test passed on the
    BROKEN compiler too. A test that passes with the bug in place is worse than no test,
    so the import below is not decoration and must not be simplified away.
    """
    result = _compile(
        tmp_path,
        f"from pymcu.chips.{chip} import {group}\n"
        "\n"
        f"class {group}:\n"
        "    def __init__(self, n):\n"
        "        self.n = n\n"
        "\n"
        "def main():\n"
        f"    obj = {group}(7)\n",
        chip,
    )
    assert "[BUILD_OK]" in result.stdout, (
        f"a user class named {group!r} lost to the imported group on {chip}:\n"
        + result.stdout + result.stderr
    )


@pytest.mark.parametrize("chip,group", _grouped_peripherals())
def test_the_group_itself_is_still_refused(tmp_path, chip, group):
    """The other half, so fixing one by breaking the other cannot pass.

    Deleting the refusal satisfies both tests above and silently loses the diagnostic that
    RFC 0012 decision 5 exists for.
    """
    result = _compile(
        tmp_path,
        f"from pymcu.chips.{chip} import {group}\n"
        "\n"
        "def main():\n"
        f"    obj = {group}()\n",
        chip,
    )
    assert "not a class to instantiate" in (result.stdout + result.stderr), (
        f"calling the {group!r} group on {chip} was accepted:\n"
        + result.stdout + result.stderr
    )

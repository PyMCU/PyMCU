"""Every trigger `Pin` exports either configures the hardware or is refused by name.

`Pin.IRQ_HIGH_LEVEL = 8` was defined on the AVR `Pin` class, and `pin_irq_setup` handled
only 1, 2, 3 and 4. Trigger 8 fell off the end of the if/elif chain: EICRA was left at its
reset value 0x00 -- which is LOW LEVEL -- and EIMSK was enabled anyway. The user asked for
high level and got its exact opposite, with no diagnostic of any kind. Held high, the pin
they selected never fired; held low, low-level triggering re-asserts for as long as the pin
stays low, so the ISR re-enters forever and the part never reaches the next statement.

Two things are pinned here. First that the rejection exists and names what was asked for.
Second the ISCn1:ISCn0 encoding of every trigger that IS supported, per external interrupt,
against the datasheet table -- because "silently left at the reset value" is only invisible
while nothing states what the value should have been.

The census at the end is what makes this survive: a trigger constant added to the class
without a home in pin_irq_setup fails here rather than on someone's bench.
"""

import ast
import json
import re
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"
GPIO_INIT = STDLIB / "pymcu" / "hal" / "avr" / "gpio" / "__init__.py"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")

# ATmega328P data-space addresses.
EICRA, EIMSK = 0x69, 0x3D

PROGRAM = """\
from pymcu.hal.gpio import Pin
from pymcu.types import uint16

hits: uint16 = 0


def on_edge():
    global hits
    hits = hits + 1


def main():
    btn = Pin("{pin}", Pin.IN_PULLUP)
    btn.irq({trigger}, on_edge)
    while True:
        pass
"""


def build(tmp_path: Path, pin: str, trigger: str, target: str = "atmega328p"):
    """Compiles the program and returns (process, IR or None)."""
    src = tmp_path / "main.py"
    src.write_text(PROGRAM.format(pin=pin, trigger=trigger))
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(src), "-o", "/dev/null", "--arch", "avr",
         "--target", target, "--freq", "16000000", "-I", str(STDLIB),
         "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    return proc, (json.loads(mir.read_text()) if mir.exists() else None)


def diagnostic(proc):
    """The error MESSAGE only.

    stderr also echoes the offending source line, so `"IRQ_HIGH_LEVEL" in proc.stderr` is
    satisfied by the program the test itself wrote -- these assertions have to read what the
    compiler SAID, not what it quoted back.
    """
    for line in proc.stderr.splitlines():
        m = re.search(r"error: (?:\w+Error: )?(.*)", line)
        if m:
            return m.group(1)
    return ""


def bits(ir, address):
    """{bit: 0 or 1} for the bit writes the program makes to one register."""
    out = {}
    for fn in ir["functions"]:
        for insn in fn["body"]:
            if insn.get("$t") in ("bset", "bclr") \
                    and isinstance(insn.get("target"), dict) \
                    and insn["target"].get("address") == address:
                out[insn["bit"]] = 1 if insn["$t"] == "bset" else 0
    return out


# ── the trigger that cannot be supported ─────────────────────────────────────

def test_high_level_is_refused_instead_of_silently_configuring_low_level(tmp_path):
    proc, _ = build(tmp_path, "PD2", "Pin.IRQ_HIGH_LEVEL")

    assert proc.returncode != 0, "IRQ_HIGH_LEVEL used to build clean and configure low level"
    assert "IRQ_HIGH_LEVEL" in diagnostic(proc), \
        f"the rejection must name what was asked for; got: {diagnostic(proc)}"


def test_the_high_level_rejection_says_why_and_what_to_use(tmp_path):
    proc, _ = build(tmp_path, "PD2", "Pin.IRQ_HIGH_LEVEL")

    msg = diagnostic(proc)
    assert "ISCn1" in msg, "name the encoding that has no high-level mode"
    assert "Pin.IRQ_RISING" in msg, "the reader needs the trigger that does exist"


@pytest.mark.parametrize("target", ["atmega328p", "atmega2560", "atmega32u4"])
def test_every_avr_chip_with_external_interrupts_refuses_high_level(tmp_path, target):
    """All three pin_irq_setup implementations had the same hole."""
    pin = "PD2" if target == "atmega328p" else "PD0"
    proc, _ = build(tmp_path, pin, "Pin.IRQ_HIGH_LEVEL", target=target)

    assert proc.returncode != 0, f"{target} still accepts IRQ_HIGH_LEVEL"
    assert "IRQ_HIGH_LEVEL is not supported" in diagnostic(proc), \
        f"{target} refused it, but not for this reason: {diagnostic(proc)}"


@pytest.mark.parametrize("trigger", ["5", "6", "7", "9", "255"])
def test_a_trigger_that_is_not_one_of_the_four_is_refused(tmp_path, trigger):
    """Any value off the end of the chain used to mean "low level, quietly"."""
    proc, _ = build(tmp_path, "PD2", trigger)

    assert proc.returncode != 0, f"trigger {trigger} still builds"
    assert "unknown irq trigger" in diagnostic(proc)


def test_the_bit_mask_reading_is_addressed_in_the_message(tmp_path):
    """The constants are 1, 2, 4, 8, which invites `|`. Only 1|2 == 3 means anything."""
    proc, _ = build(tmp_path, "PD2", "Pin.IRQ_RISING | Pin.IRQ_LOW_LEVEL")

    assert proc.returncode != 0
    assert "IRQ_CHANGE" in diagnostic(proc), \
        "say which combination does work, since the values look like a mask"


# ── the encodings that must not drift ────────────────────────────────────────
#
# ATmega328P datasheet, ISCn1:ISCn0 -- 00 low level, 01 any edge, 10 falling, 11 rising.
# INT0 is EICRA[1:0] and is enabled by EIMSK[0]; INT1 is EICRA[3:2], EIMSK[1].

ENCODINGS = [
    ("PD2", "Pin.IRQ_LOW_LEVEL", {0: 0, 1: 0}, 0),
    ("PD2", "Pin.IRQ_CHANGE",    {0: 1, 1: 0}, 0),
    ("PD2", "Pin.IRQ_FALLING",   {0: 0, 1: 1}, 0),
    ("PD2", "Pin.IRQ_RISING",    {0: 1, 1: 1}, 0),
    ("PD3", "Pin.IRQ_LOW_LEVEL", {2: 0, 3: 0}, 1),
    ("PD3", "Pin.IRQ_CHANGE",    {2: 1, 3: 0}, 1),
    ("PD3", "Pin.IRQ_FALLING",   {2: 0, 3: 1}, 1),
    ("PD3", "Pin.IRQ_RISING",    {2: 1, 3: 1}, 1),
]


@pytest.mark.parametrize("pin,trigger,isc,int_bit", ENCODINGS,
                         ids=[f"{p}-{t.split('.')[-1]}" for p, t, _, _ in ENCODINGS])
def test_the_trigger_reaches_the_datasheet_encoding(tmp_path, pin, trigger, isc, int_bit):
    proc, ir = build(tmp_path, pin, trigger)

    assert proc.returncode == 0, f"{pin} {trigger} must build: {proc.stderr}"
    assert bits(ir, EICRA) == isc, \
        f"{trigger} on {pin} must write ISCn1:ISCn0 = {isc}, and write BOTH bits"
    assert bits(ir, EIMSK).get(int_bit) == 1, "the interrupt has to be enabled too"


# The same four encodings on the other two chips that have external interrupts. Their
# pin_irq_setup was written and then never imported by the facade, so Pin.irq() failed as
# "name 'pin_irq_setup' is not defined" for every program on either part -- an internal
# helper the user never wrote, naming a file they had never opened. INT0 is PD0 on both.
@pytest.mark.parametrize("target", ["atmega2560", "atmega32u4"])
@pytest.mark.parametrize("trigger,isc", [
    ("Pin.IRQ_LOW_LEVEL", {0: 0, 1: 0}),
    ("Pin.IRQ_CHANGE",    {0: 1, 1: 0}),
    ("Pin.IRQ_FALLING",   {0: 0, 1: 1}),
    ("Pin.IRQ_RISING",    {0: 1, 1: 1}),
])
def test_the_other_avr_chips_reach_their_own_setup(tmp_path, target, trigger, isc):
    proc, ir = build(tmp_path, "PD0", trigger, target=target)

    assert proc.returncode == 0, f"{target} {trigger}: {proc.stderr}"
    assert bits(ir, EICRA) == isc
    assert bits(ir, EIMSK).get(0) == 1


def test_low_level_writes_its_zeroes_rather_than_relying_on_reset(tmp_path):
    """The reset value happens to be low level, which is what hid the bug.

    Leaving the bits alone would produce the right behaviour from reset and the wrong
    behaviour from any state a previous irq() call left behind.
    """
    _, ir = build(tmp_path, "PD2", "Pin.IRQ_LOW_LEVEL")

    assert set(bits(ir, EICRA)) == {0, 1}, "both ISC bits must be written explicitly"


# ── the census ───────────────────────────────────────────────────────────────

def trigger_constants():
    """Every IRQ_* constant the AVR Pin class exports, with its value."""
    tree = ast.parse(GPIO_INIT.read_text())
    cls = next(n for n in ast.walk(tree)
               if isinstance(n, ast.ClassDef) and n.name == "Pin")
    out = {}
    for node in cls.body:
        if isinstance(node, ast.Assign) and isinstance(node.targets[0], ast.Name) \
                and node.targets[0].id.startswith("IRQ_"):
            out[node.targets[0].id] = node.value.value
    return out


CONSTANTS = trigger_constants()


def test_the_census_found_the_constants():
    """A scan that silently matched nothing would pass for ever."""
    assert set(CONSTANTS) >= {"IRQ_FALLING", "IRQ_RISING", "IRQ_LOW_LEVEL"}, \
        f"the scan of {GPIO_INIT.name} only found {set(CONSTANTS)}"


def test_irq_change_is_named(tmp_path):
    """pin_irq_setup has always implemented trigger 3, and irq() defaults to it.

    It was reachable only as `IRQ_FALLING | IRQ_RISING`, which is 3 by arithmetic.
    """
    assert CONSTANTS.get("IRQ_CHANGE") == 3
    proc, _ = build(tmp_path, "PD2", "Pin.IRQ_CHANGE")
    assert proc.returncode == 0, proc.stderr


@pytest.mark.parametrize("name", sorted(CONSTANTS))
def test_every_exported_trigger_either_configures_the_chip_or_names_itself(tmp_path, name):
    """The bug in one sentence: a constant existed that nothing downstream handled.

    "It builds" is not the bar -- IRQ_HIGH_LEVEL built. A trigger that compiles has to
    leave the ISC bits written, because the reset value is itself a valid mode and a
    trigger that writes nothing has silently selected it.
    """
    proc, ir = build(tmp_path, "PD2", f"Pin.{name}")

    if proc.returncode != 0:
        assert name in diagnostic(proc), \
            f"Pin.{name} is exported but neither configures the hardware nor names itself"
        return

    assert set(bits(ir, EICRA)) == {0, 1}, \
        f"Pin.{name} builds without writing ISC01:ISC00, so it takes whatever mode the " \
        f"bits already held -- which from reset is low level, not {name}"


def test_the_setup_helpers_guard_before_touching_a_register():
    """A guard placed after the first register write would leave the chip half-configured."""
    for chip in ("atmega328p", "atmega2560", "atmega32u4"):
        text = (STDLIB / "pymcu" / "hal" / "avr" / "gpio" / f"{chip}.py").read_text()
        body = text[text.index("def pin_irq_setup("):]
        guard = body.index("raise CompileError")
        first_write = min(m.start() for m in re.finditer(r"^\s+EICRA\[|^\s+PCICR\[",
                                                         body, re.MULTILINE))
        assert guard < first_write, \
            f"{chip}: pin_irq_setup writes a register before it validates the trigger"

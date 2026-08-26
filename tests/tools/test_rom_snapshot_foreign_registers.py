"""The ROM snapshot proved a binary was PRODUCED. This is the invariant that it is this chip's.

Three bugs shipped through that gap and every one of them produced a binary the gate counted
the bytes of: the ATmega328P USART compiled on every AVR, so an ATtiny4313 with 256 bytes of
SRAM got UCSR0A at 0xC0 and UDR0 at 0xC6 and eight parts with no USART at all got one; a flash
table over 256 bytes read only its first 256; a 16-bit compare register written low-byte-only.

The invariant is one line of intent: every absolute data-space address in the IR has to be a
register the target chip declares. What is pinned below is mostly that it DISCRIMINATES -- an
invariant every image satisfies would be a decoration -- and that it does not fire on the
correct images, because a gate that cries wolf is worse than no gate.
"""

import json
import re
import subprocess
import sys
import tempfile
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent))
from rom_snapshot import (  # noqa: E402
    CHIPS_DIR,
    declared_registers,
    foreign_registers,
    mir_addresses,
)

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")

BLINK = """\
from pymcu.hal.gpio import Pin
from pymcu.time import delay_ms


def main():
    led = Pin("PB0", Pin.OUT)
    while True:
        led.high()
        delay_ms(100)
        led.low()
        delay_ms(100)
"""

UART = """\
from pymcu.types import uint8
from pymcu.hal.uart import UART


def main():
    u = UART(9600)
    n: uint8 = 0
    while True:
        u.write(0x41 + n)
        n = n + 1
"""


def emit_ir(tmp_path: Path, source: str, chip: str, freq: int = 16_000_000) -> Path | None:
    src = tmp_path / "main.py"
    src.write_text(source)
    mir = tmp_path / "firmware.mir"
    subprocess.run(
        [str(PYMCUC), str(src), "-o", "/dev/null", "--arch", "avr", "--target", chip,
         "--freq", str(freq), "-I", str(tmp_path), "-I", str(STDLIB), "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    return mir if mir.exists() else None


# ── the invariant discriminates ──────────────────────────────────────────────

def test_the_328p_uart_image_is_foreign_to_a_chip_without_those_registers(tmp_path):
    """The reported bug, reconstructed: this image against the part that received it.

    0xC0 and 0xC6 are UCSR0A and UDR0. The ATtiny4313 declares neither.
    """
    mir = emit_ir(tmp_path, UART, "atmega328p")
    assert mir is not None

    foreign = foreign_registers(mir, "attiny4313")

    assert 0xC0 in foreign and 0xC6 in foreign


def test_a_range_check_would_have_missed_it(tmp_path):
    """Why the invariant is "a register this chip declares" and not "somewhere in its RAM".

    0xC0 sits INSIDE the ATtiny4313's SRAM (0x0060..0x015F), so an address-range test passes
    the exact bug this exists to catch.
    """
    text = (CHIPS_DIR / "attiny4313.py").read_text()
    ram_start = int(re.search(r"^RAM_START\s*=\s*(0x[0-9a-fA-F]+|\d+)", text, re.M).group(1), 0)
    ram_size = int(re.search(r"^RAM_SIZE\s*=\s*(0x[0-9a-fA-F]+|\d+)", text, re.M).group(1), 0)

    assert ram_start <= 0xC0 < ram_start + ram_size
    assert 0xC0 not in declared_registers("attiny4313")


# ── and it does not fire on the correct images ───────────────────────────────

@pytest.mark.parametrize("chip,freq", [
    ("atmega328p", 16_000_000),
    ("attiny85", 8_000_000),
    ("attiny4313", 8_000_000),
    ("atmega2560", 16_000_000),
])
def test_a_correct_blink_touches_only_registers_its_chip_has(tmp_path, chip, freq):
    mir = emit_ir(tmp_path, BLINK, chip, freq)
    assert mir is not None, f"blink must build for {chip}"

    assert foreign_registers(mir, chip) == []


@pytest.mark.parametrize("chip,freq", [
    ("atmega328p", 16_000_000),
    ("attiny4313", 8_000_000),
    ("atmega2560", 16_000_000),
])
def test_a_correct_uart_touches_only_registers_its_chip_has(tmp_path, chip, freq):
    mir = emit_ir(tmp_path, UART, chip, freq)
    assert mir is not None, f"uart must build for {chip}"

    assert foreign_registers(mir, chip) == []


# ── the scope, stated rather than assumed ────────────────────────────────────

def test_chips_with_computed_register_bases_are_out_of_scope():
    """`ptr(UART0_BASE + 0x08)` cannot be read off the file, so those chips are skipped.

    Naming them here means the day one of them starts declaring literals, or a new chip
    arrives with computed bases, somebody is told rather than left with a silently narrower
    check than they think they have.
    """
    skipped = {f.stem for f in CHIPS_DIR.glob("*.py")
               if not f.stem.startswith("_") and declared_registers(f.stem) is None}

    assert skipped == {"rp2040", "rp2350", "ch32v003", "ch32v203"}


def test_every_other_chip_is_in_scope():
    covered = [f.stem for f in CHIPS_DIR.glob("*.py")
               if not f.stem.startswith("_") and declared_registers(f.stem) is not None]

    assert len(covered) >= 25, f"only {len(covered)} chips covered: {sorted(covered)}"
    assert all(declared_registers(c) for c in covered), "a chip in scope must declare registers"


# ── the mechanics ────────────────────────────────────────────────────────────

def test_a_variable_is_not_an_address(tmp_path):
    """Variables are `var` nodes with names; only registers are raw addresses.

    That is what makes "every address must be declared" the right shape rather than an
    approximation, so it is worth a test of its own.
    """
    mir = emit_ir(tmp_path, BLINK, "atmega328p")
    doc = json.loads(mir.read_text())

    kinds = set()

    def walk(node):
        if isinstance(node, dict):
            if "$t" in node:
                kinds.add(node["$t"])
            for v in node.values():
                walk(v)
        elif isinstance(node, list):
            for v in node:
                walk(v)

    walk(doc)
    assert "var" in kinds, "the corpus program has to have variables for this to mean anything"
    assert mir_addresses(mir), "and registers, or the check has nothing to look at"


def test_an_unreadable_mir_is_not_a_failure(tmp_path):
    """The gate reports on binaries; it must not become the thing that breaks."""
    junk = tmp_path / "firmware.mir"
    junk.write_text("not json")

    assert foreign_registers(junk, "atmega328p") == []


def test_an_unknown_chip_is_out_of_scope():
    assert declared_registers("no-such-chip") is None

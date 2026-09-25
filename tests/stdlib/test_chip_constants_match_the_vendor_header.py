"""Chip constants agree with the vendor's header, and the disagreements only shrink.

`lib/src/pymcu/chips/*.py` carries about 1 900 register declarations and the HAL carries a
per-chip EEPROM size. Every one of those numbers is a fact about silicon copied by hand,
and a wrong one compiles clean: an address that is not the register it claims still
produces a binary, and a size that is too large still passes every bounds check. Two
campaigns have already been paid for out of this shape, the ATtiny register maps
(PyMCU#490) and the EEPROM sizes (PyMCU#493), and in both the file's own comment agreed
with the wrong value, so reading harder was never going to find them.

The vendor ships the same facts in machine-readable form, so they are compared here rather
than trusted. See `chip_header_audit.py` for what is read and why `E2END` is evaluated
instead of scraped.

This test is a RATCHET, not a gate. The discrepancies that exist today are recorded in
`chip_header_baseline.json` with their current values, so the test can land while those
campaigns wait their turn. It fails on a discrepancy that is NOT in the baseline, and it
fails when a baselined discrepancy has changed to a different wrong value, because that is
a new fact and not the one that was signed off. Fixing one means deleting its entry: the
baseline only ever shrinks, and a test asserts that it never grows.
"""

import json
import re
from pathlib import Path

import chip_header_audit as audit

REPO = Path(__file__).resolve().parents[2]
CHIPS = REPO / "lib" / "src" / "pymcu" / "chips"
EEPROM_FACADE = REPO / "lib" / "src" / "pymcu" / "hal" / "avr" / "eeprom" / "__init__.py"
BASELINE = Path(__file__).with_name("chip_header_baseline.json")

_DECL = re.compile(
    r"^(\w+)\s*:\s*ptr\[uint(8|16)\]\s*=\s*ptr\((0x[0-9A-Fa-f]+)\)", re.M
)
_LADDER = re.compile(r"^\s*(?:el)?if\s+\(?\s*(__CHIP__\.name\s*==.*?)\)?\s*:\s*$")
_CHIP = re.compile(r'__CHIP__\.name\s*==\s*"([a-z0-9]+)"')
_SIZE = re.compile(r"^\s*EEPROM_SIZE\s*:\s*uint16\s*=\s*(\d+)\s*$")


def _declared_registers(chip):
    """{register: (address, width)} as the chip definition declares it."""
    text = (CHIPS / (chip + ".py")).read_text(encoding="utf-8")
    return {
        name: (int(address, 16), int(bits) // 8)
        for name, bits, address in _DECL.findall(text)
    }


def _logical_lines(text):
    """Source lines with parenthesised continuations joined into one.

    Half the EEPROM ladder is written `elif (... or ...` across two and three lines. A
    reader that takes one physical line at a time skips exactly those arms without a word,
    which is how the first version of this audit reported two discrepancies where there
    are five.
    """
    buffer = ""
    for raw in text.splitlines():
        buffer = buffer + " " + raw.strip() if buffer else raw
        if buffer.count("(") > buffer.count(")"):
            continue
        yield buffer
        buffer = ""
    if buffer:
        yield buffer


def _declared_eeprom_sizes():
    """{chip: EEPROM_SIZE} by walking the facade's if/elif/else ladder in order.

    The `else` arm is read too, and it matters: it is where the ATmega328P lands, which is
    the part every default build targets. An audit that only read the named arms would
    leave the most-used chip in the project uncompared.
    """
    sizes = {}
    pending = []
    fallback = None
    in_else = False
    for line in _logical_lines(EEPROM_FACADE.read_text(encoding="utf-8")):
        ladder = _LADDER.match(line)
        if ladder is not None:
            pending, in_else = _CHIP.findall(ladder.group(1)), False
            continue
        if re.match(r"^\s*else\s*:\s*$", line):
            pending, in_else = [], True
            continue
        size = _SIZE.match(line)
        if size is None:
            continue
        if pending:
            for chip in pending:
                sizes.setdefault(chip, int(size.group(1)))
            pending = []
        elif in_else and fallback is None:
            fallback = int(size.group(1))
            in_else = False

    if fallback is not None:
        for chip in audit.CHIP_HEADERS:
            sizes.setdefault(chip, fallback)
    return sizes


def _discrepancies():
    """Every place a declared constant disagrees with the vendor header, as {key: found}."""
    found = {}

    for chip in sorted(audit.CHIP_HEADERS):
        path = CHIPS / (chip + ".py")
        if not path.is_file():
            continue
        vendor = audit.vendor_registers(chip)
        for name, (address, width) in sorted(_declared_registers(chip).items()):
            if name in audit.NOT_IN_CHIP_HEADER:
                continue
            if name not in vendor:
                found["%s:%s" % (chip, name)] = "absent from the vendor header"
            elif (address, width) not in vendor[name]:
                found["%s:%s" % (chip, name)] = "0x%X/u%d, header says %s" % (
                    address,
                    width * 8,
                    ", ".join(
                        "0x%X/u%d" % (a, w * 8) for a, w in sorted(vendor[name])
                    ),
                )

    for chip, size in sorted(_declared_eeprom_sizes().items()):
        if chip not in audit.CHIP_HEADERS:
            continue
        expected = audit.vendor_eeprom_size(chip)
        if expected is not None and size != expected:
            found["%s:EEPROM_SIZE" % chip] = "%d, header says %d" % (size, expected)

    return found


def test_no_new_disagreement_with_the_vendor_header():
    baseline = json.loads(BASELINE.read_text(encoding="utf-8"))["known"]
    found = _discrepancies()

    new = {k: v for k, v in found.items() if k not in baseline}
    changed = {
        k: (baseline[k], v) for k, v in found.items() if k in baseline and baseline[k] != v
    }

    assert not new, (
        "a chip constant disagrees with the vendor header and is not in the baseline:\n"
        + "\n".join("  %s: %s" % (k, v) for k, v in sorted(new.items()))
        + "\n\nIf the constant is wrong, fix it. If the HEADER is the odd one, add the entry "
        "to chip_header_baseline.json with the reason in its commit message."
    )

    assert not changed, (
        "a baselined discrepancy now reports a different value, so it is a new fact and not "
        "the one that was signed off:\n"
        + "\n".join(
            "  %s: baseline %s, now %s" % (k, was, now) for k, (was, now) in sorted(changed.items())
        )
    )


def test_the_baseline_only_shrinks():
    baseline = json.loads(BASELINE.read_text(encoding="utf-8"))["known"]
    found = _discrepancies()

    stale = sorted(set(baseline) - set(found))
    assert not stale, (
        "the baseline lists discrepancies that no longer exist. They were fixed, so delete "
        "them: the baseline may only shrink.\n"
        + "\n".join("  %s" % k for k in stale)
    )


def test_the_audit_actually_read_something():
    # A header path that moved, or a regex that stopped matching, would empty the sweep and
    # let every constant through. Two anchors: a chip whose registers are known correct, and
    # an EEPROM size the ladder definitely carries.
    assert len(audit.vendor_registers("atmega328p")) > 80
    assert len(_declared_registers("atmega328p")) > 80

    # Every audited chip must come out of the EEPROM ladder with a size. The arms of that
    # ladder are parenthesised across several lines, and a reader that takes one line at a
    # time drops those arms without a word, which is how the first version of this audit
    # under-reported five discrepancies as two.
    sizes = _declared_eeprom_sizes()
    assert sizes.get("atmega328p") == 1024, "the else arm of the ladder was not read"
    assert sizes.get("attiny85") == 512, "a named arm of the ladder was not read"
    missing = sorted(set(audit.CHIP_HEADERS) - set(sizes))
    assert not missing, (
        "these audited chips never matched an arm of the EEPROM ladder, so their size was "
        "never compared: " + ", ".join(missing)
    )

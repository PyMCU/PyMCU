"""Read the vendor's own header and say what each chip's constants should be.

Every number in `lib/src/pymcu/chips/*.py` and in the per-chip tables of the HAL is a
hand-copied fact about silicon, and a wrong one compiles clean: an address that is not the
register it claims still produces a binary, and a size that is too large still passes every
bounds check. The vendor ships the same facts in machine-readable form, in the avr-libc
headers that the toolchain wheel carries, so they can be compared instead of trusted.

Two facts are read:

* every `_SFR_IO8` / `_SFR_MEM8` / `_SFR_MEM16` macro, which gives a register's data-space
  address and its width (an IO address is the data-space address minus 0x20);
* `E2END`, the last EEPROM address, which gives the EEPROM size as `E2END + 1`.

`E2END` is EVALUATED, not scraped. `iotn13a.h` writes it `(64 - 1)` while its siblings
write a hex literal, and a reader that grabs the first number out of the line reports the
ATtiny13A as having 65 bytes and sends somebody to fix a chip that is correct. That false
positive happened while this audit was being written; the arithmetic is why.
"""

import re
from pathlib import Path

TOOLCHAIN_HEADERS = Path(
    "/Users/begeistert/Repos/avr-wasi/native/pymcu_avr_toolchain/avr/include/avr"
)

# The per-chip header each chip definition must agree with. A chip with no entry is not
# audited, which is the honest state for parts whose vendor header this toolchain does not
# carry rather than a silent pass.
CHIP_HEADERS = {
    "atmega48": "iom48.h",
    "atmega48p": "iom48p.h",
    "atmega88": "iom88.h",
    "atmega88p": "iom88p.h",
    "atmega168": "iom168.h",
    "atmega168p": "iom168p.h",
    "atmega328": "iom328p.h",
    "atmega328p": "iom328p.h",
    "atmega2560": "iom2560.h",
    "atmega32u4": "iom32u4.h",
    "attiny13": "iotn13.h",
    "attiny13a": "iotn13a.h",
    "attiny24": "iotn24.h",
    "attiny25": "iotn25.h",
    "attiny44": "iotn44.h",
    "attiny45": "iotn45.h",
    "attiny84": "iotn84.h",
    "attiny85": "iotn85.h",
    "attiny2313": "iotn2313.h",
    "attiny4313": "iotn4313.h",
}

# SREG, SPL and SPH live in <avr/common.h>, not in the per-chip header, so a chip file
# declaring them has nothing to compare against here.
NOT_IN_CHIP_HEADER = {"SREG", "SPL", "SPH"}

_INCLUDE = re.compile(r"^\s*#\s*include\s+<avr/([\w.]+)>", re.M)
_SFR = re.compile(
    r"^\s*#\s*define\s+(\w+)\s+_SFR_(IO|MEM)(8|16)\s*\(([^)]+)\)", re.M
)
_E2END = re.compile(r"^\s*#\s*define\s+E2END\s+(.+?)\s*$", re.M)
_ARITH = re.compile(r"^[\s0-9xXa-fA-F()+\-*]+$")


def _read_header(name, seen=None):
    """A header plus everything it includes, since iom168.h is a shell over iomx8.h."""
    seen = set() if seen is None else seen
    path = TOOLCHAIN_HEADERS / name
    if name in seen or not path.is_file():
        return ""
    seen.add(name)
    text = path.read_text(encoding="utf-8", errors="ignore")
    return "\n".join([text] + [_read_header(inc, seen) for inc in _INCLUDE.findall(text)])


def _eval_int(expression):
    """`0x7F`, `(0xFF)` and `(64 - 1)` all mean a number. Anything else means unknown."""
    expression = expression.split("/*")[0].strip()
    if not expression or not _ARITH.match(expression):
        return None
    try:
        value = eval(expression, {"__builtins__": {}}, {})  # noqa: S307 - arithmetic only
    except Exception:
        return None
    return value if isinstance(value, int) else None


def vendor_registers(chip):
    """{register: (data-space address, width in bytes)} as the vendor header declares it."""
    registers = {}
    for name, space, bits, address in _SFR.findall(_read_header(CHIP_HEADERS[chip])):
        value = _eval_int(address)
        if value is None:
            continue
        if space == "IO":
            value += 0x20
        registers.setdefault(name, set()).add((value, int(bits) // 8))
    return registers


def vendor_eeprom_size(chip):
    """`E2END + 1`, or None when the header does not define it."""
    for expression in _E2END.findall(_read_header(CHIP_HEADERS[chip])):
        value = _eval_int(expression)
        if value is not None:
            return value + 1
    return None

"""No comparison arm asks for a value its parameter cannot hold.

An `@inline` argument is narrowed to the width its parameter declares. So a body that
compares that parameter against a constant wider than the declaration has an arm nothing
can take: the caller's 1152 arrives as 128, no arm matches, and the function falls off the
end having configured nothing. It compiles, it runs, and it is wrong in silence.

PyMCU#493 was this in the PIC UART, where the baud selector is the rate in hundreds and
`baud: uint8` made 38400, 57600 and 115200 unreachable, so the generator kept whatever
reset left and the part talked at the wrong rate. PyMCU#499 was the same three rates in the
AVR UART. Both were found by reading a diff, which is not a method.

This is the method: for every function that declares an integer parameter, check that no
`param == K` in its body asks for a K the declared type cannot hold. It is a source sweep,
so it costs nothing and covers every architecture at once, including the per-chip files
that no fixture compiles.

The sweep runs on the same rule the compiler applies, not on a list of known cases, so a
selector that grows a faster rate is covered the day it is written.
"""

import re
from pathlib import Path

STDLIB = Path(__file__).resolve().parents[2] / "lib" / "src" / "pymcu"

# The widths a parameter can declare, and the largest value each one holds. A comparison
# against anything above this is an arm the narrowing makes unreachable.
MAX_VALUE = {
    "uint8": 255,
    "int8": 127,
    "uint16": 65535,
    "int16": 32767,
}

_DEF = re.compile(r"^\s*def\s+(\w+)\s*\(([^)]*)\)", re.M)
_NEXT_TOP_LEVEL = re.compile(r"^(?:def |class |@)", re.M)


def _int_parameters(signature):
    """{name: declared type} for the parameters a BARE width annotation narrows.

    `const[uint16]` is deliberately excluded, and the exclusion was measured rather than
    assumed. The first version of this sweep treated it as a width and reported four arms
    in the live PIC UARTs as unreachable: `pic16f877a_uart.uart_init(baud: const[uint16])`
    comparing against 115200. Compiling that call writes `SPBRG = 1`, the correct divisor
    for 115200 at 4 MHz, so the arm is taken and the report was wrong. A const parameter
    carries its literal through; a bare one is narrowed to its declared width, which is
    the whole mechanism this sweep is about. Both UARTs that really were broken,
    PyMCU#493 and PyMCU#499, declared a BARE `uint8`.

    Including const here would fail the build on working code and send somebody to widen a
    parameter that needs no widening, which is worse than not having the sweep at all.
    """
    declared = {}
    for part in signature.split(","):
        if ":" not in part:
            continue
        name, annotation = part.split(":", 1)
        annotation = annotation.split("=")[0].strip()
        if annotation in MAX_VALUE:
            declared[name.strip()] = annotation
    return declared


def _unreachable_arms():
    """Every `param == K` whose K the parameter's declared width cannot hold."""
    found = []
    for path in sorted(STDLIB.rglob("*.py")):
        text = path.read_text(encoding="utf-8")
        for match in _DEF.finditer(text):
            parameters = _int_parameters(match.group(2))
            if not parameters:
                continue
            body = text[match.end():]
            following = _NEXT_TOP_LEVEL.search(body)
            if following is not None:
                body = body[: following.start()]
            for name, declared in parameters.items():
                pattern = re.compile(r"\b%s\s*==\s*(\d+)\b" % re.escape(name))
                for comparison in pattern.finditer(body):
                    value = int(comparison.group(1))
                    if value > MAX_VALUE[declared]:
                        found.append(
                            "%s: %s(%s: %s) compares == %d, which a %s cannot hold"
                            % (
                                path.relative_to(STDLIB).as_posix(),
                                match.group(1), name, declared, value, declared,
                            )
                        )
    return found


def test_the_sweep_reads_the_stdlib():
    # An empty sweep passes the test below without checking anything, so the sweep has to
    # prove it found functions to check. The count is a floor, not a fixture: the stdlib
    # has hundreds of functions declaring a width.
    checked = 0
    for path in STDLIB.rglob("*.py"):
        text = path.read_text(encoding="utf-8")
        for match in _DEF.finditer(text):
            if _int_parameters(match.group(2)):
                checked += 1
    assert checked > 200, (
        "the sweep found only %d functions with a width-declaring parameter, which means "
        "the signature regex stopped matching rather than that the stdlib shrank" % checked
    )


def test_no_arm_is_unreachable_by_its_parameter_width():
    unreachable = sorted(set(_unreachable_arms()))
    assert not unreachable, (
        "these comparisons can never be taken, because an inline argument is narrowed to "
        "the parameter's declared width before the body compares it:\n"
        + "\n".join("  " + u for u in unreachable)
        + "\n\nWiden the parameter to the type the values need. The arm that looks correct "
        "in the source is the one the caller can never reach."
    )

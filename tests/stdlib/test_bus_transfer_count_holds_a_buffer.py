"""A bus transfer's byte count is wide enough for the buffers it is given.

`test_no_arm_is_unreachable_by_its_parameter_width` sweeps for the COMPARISON form of a
lying width declaration: a body that tests `param == K` for a K the declaration cannot
hold. That sweep cannot see the other form, and the other form is the one that reached the
wire.

In the PASS-THROUGH form the parameter is not compared with anything. It is used, at its
declared width, on a value that arrives from the CALL SITE -- `write_bytes(buf, len(buf))`
-- so the truncation happens at the boundary and the body is blameless when read. The AVR
I2C entry point declared `n: uint8`, the MicroPython layer handed it `len(buf)`, and a
128x32 SSD1306's 512-byte framebuffer arrived as `512 & 0xFF == 0`. The loop ran zero
times, `show()` put one byte on the wire, nothing was said, and the display stayed blank
while every command byte before it was correct (PyMCU#511).

The compiler's own refusal does not cover this either: it fires on a LITERAL the parser
built, so `take(300)` against `def take(n: uint8)` is refused naming the 44 that would
arrive, while `take(len(big))` for the same 300 compiles and receives the same 44. A
computed value has no line and column to point at, so the check cannot see it.

So this is the rule, as a source sweep like its sibling: a function that takes a BUFFER and
a COUNT of bytes to move through it must declare that count wide enough for a buffer bigger
than 255 bytes, because a framebuffer is one and a bytearray has no such limit. uint8 is
refused; uint16 or wider passes.
"""

import re
from pathlib import Path

HAL = Path(__file__).resolve().parents[2] / "lib" / "src" / "pymcu" / "hal"

# How a buffer is spelled in a HAL signature: either unannotated (the HAL's own convention
# for "some contiguous storage") or annotated bytearray.
BUFFER_NAMES = {"buf", "data", "write_buf", "read_buf", "wbuf", "rbuf", "buffer"}

# How the count of bytes to move is spelled.
COUNT_NAMES = {"n", "nbytes", "count", "length"}

# Widths that cannot hold a buffer longer than 255 bytes.
TOO_NARROW = {"uint8", "int8"}

_DEF = re.compile(r"^(\s*)def\s+(\w+)\s*\(([^)]*)\)", re.M)


def _params(signature):
    """(name, annotation) for each parameter, annotation "" when there is none."""
    out = []
    depth = 0
    current = ""
    for ch in signature:
        if ch in "[(":
            depth += 1
        elif ch in "])":
            depth -= 1
        if ch == "," and depth == 0:
            out.append(current)
            current = ""
        else:
            current += ch
    if current.strip():
        out.append(current)

    parsed = []
    for raw in out:
        raw = raw.split("=")[0].strip()
        if not raw or raw in ("self", "*", "/") or raw.startswith("*"):
            continue
        if ":" in raw:
            name, ann = raw.split(":", 1)
            parsed.append((name.strip(), ann.strip()))
        else:
            parsed.append((raw, ""))
    return parsed


def _offenders():
    bad = []
    for path in sorted(HAL.rglob("*.py")):
        source = path.read_text()
        for match in _DEF.finditer(source):
            func = match.group(2)
            params = _params(match.group(3))
            names = {name for name, _ in params}
            if not (names & BUFFER_NAMES):
                continue
            takes_buffer = any(
                name in BUFFER_NAMES and (ann == "" or "bytearray" in ann)
                for name, ann in params
            )
            if not takes_buffer:
                continue
            for name, ann in params:
                if name in COUNT_NAMES and ann in TOO_NARROW:
                    line = source[: match.start()].count("\n") + 1
                    bad.append(
                        f"{path.relative_to(HAL.parents[2])}:{line}: {func}({name}: {ann}) "
                        f"moves bytes through a buffer, so a buffer longer than 255 "
                        f"truncates at the call"
                    )
    return bad


def test_a_bus_transfer_count_is_not_declared_uint8():
    bad = _offenders()
    assert not bad, (
        "a byte count declared uint8 beside a buffer parameter truncates silently:\n  "
        + "\n  ".join(bad)
    )


def test_the_sweep_finds_the_shape_it_is_looking_for(tmp_path):
    """The sweep is only worth its runtime if it catches the reported signature.

    A sweep that matches nothing passes for the wrong reason, so the defect as it was
    written is fed back through the same matcher.
    """
    probe = tmp_path / "hal" / "probe.py"
    probe.parent.mkdir(parents=True)
    probe.write_text(
        "def i2c_write_bytes(addr: uint8, buf, n: uint8) -> uint8:\n"
        "    return 0\n"
    )

    global HAL
    original, HAL = HAL, probe.parent
    try:
        found = _offenders()
    finally:
        HAL = original

    assert len(found) == 1, found
    assert "i2c_write_bytes" in found[0]

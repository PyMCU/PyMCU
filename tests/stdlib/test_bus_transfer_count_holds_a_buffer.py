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
_NEXT_DEF = re.compile(r"^\s*(?:@inline\s*$|def |class )", re.M)


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
            line = source[: match.start()].count("\n") + 1
            where = f"{path.relative_to(HAL.parents[2])}:{line}"

            counts = set()
            for name, ann in params:
                if name not in COUNT_NAMES:
                    continue
                counts.add(name)
                if ann in TOO_NARROW:
                    bad.append(
                        f"{where}: {func}({name}: {ann}) moves bytes through a buffer, "
                        f"so a buffer longer than 255 truncates at the call"
                    )

            # THE OTHER END OF THE SAME LOOP. Widening the count and leaving the counter
            # that walks it narrow is worse than the defect it replaces: `i: uint8` against
            # `while i < n` with n over 255 does not truncate, it never terminates -- and
            # the compiler folds the comparison to always-true, so what is emitted is a bare
            # backward jump with the code after the loop deleted. Measured on an AVR probe
            # of exactly this shape: the MIR loop body ends in `jmp` with no compare, and
            # the print after it is not in the program at all.
            for local, ann in _local_counters(source, match.end()):
                if ann in TOO_NARROW and local in _walked_against(source, match.end(), counts):
                    bad.append(
                        f"{where}: {func} walks a wide count with `{local}: {ann}`, "
                        f"which cannot reach it: the loop never ends"
                    )
    return bad


def _body(source, start):
    """The text of the def that starts at `start`, up to the next def/class."""
    nxt = _NEXT_DEF.search(source, start)
    return source[start : nxt.start() if nxt else len(source)]


def _local_counters(source, start):
    """(name, annotation) for each annotated local assignment in the body."""
    return re.findall(r"^\s*(\w+)\s*:\s*(\w+)\s*=", _body(source, start), re.M)


def _walked_against(source, start, counts):
    """Locals used as the left side of `while <local> < <a count parameter>`."""
    body = _body(source, start)
    return {
        var
        for var, limit in re.findall(r"^\s*while\s+(\w+)\s*<\s*(\w+)\s*:", body, re.M)
        if limit in counts
    }


def test_a_bus_transfer_count_is_not_declared_uint8():
    bad = _offenders()
    assert not bad, (
        "a byte count that cannot hold a buffer longer than 255, at either end of the "
        "loop:\n  " + "\n  ".join(bad)
    )


def _sweep_over(tmp_path, source):
    probe = tmp_path / "hal" / "probe.py"
    probe.parent.mkdir(parents=True, exist_ok=True)
    probe.write_text(source)

    global HAL
    original, HAL = HAL, probe.parent
    try:
        return _offenders()
    finally:
        HAL = original


def test_the_sweep_finds_a_narrow_count_parameter(tmp_path):
    """The sweep is only worth its runtime if it catches the reported signature.

    A sweep that matches nothing passes for the wrong reason, so the defect as it was
    written is fed back through the same matcher.
    """
    found = _sweep_over(tmp_path,
        "def i2c_write_bytes(addr: uint8, buf, n: uint8) -> uint8:\n"
        "    return 0\n"
    )

    assert len(found) == 1, found
    assert "i2c_write_bytes" in found[0]
    assert "truncates" in found[0]


def test_the_sweep_finds_a_narrow_counter_walking_a_wide_count(tmp_path):
    """The other end of the loop, which the parameter check alone cannot see.

    This shape shipped: the RP2040 and RP2350 `write_bytes` had the count widened to
    uint16 and the counter left at uint8, which is worse than the truncation it replaced
    because the loop never terminates. The first version of this sweep looked only at the
    parameter list and passed over both.
    """
    found = _sweep_over(tmp_path,
        "def write_bytes(self, addr: uint8, data: bytearray, n: uint16):\n"
        "    i: uint8 = 0\n"
        "    while i < n:\n"
        "        i = i + 1\n"
    )

    assert len(found) == 1, found
    assert "write_bytes" in found[0]
    assert "never ends" in found[0]


def test_the_sweep_leaves_a_bit_counter_alone(tmp_path):
    """A uint8 counter is only wrong when it walks the COUNT.

    `softi2c.py` shifts a byte out bit by bit with `i: uint8 = 0` against `while i < 8`, in
    a function that also takes a buffer and a wide count. That counter is right as it is and
    widening it would cost bytes for nothing, so the rule is tied to the variable compared
    against the COUNT PARAMETER, not against any constant. Without this case a sweep that
    flagged every narrow local in such a function would still pass its other probes.
    """
    found = _sweep_over(tmp_path,
        "def write_bytes(self, addr: uint8, buf, n: uint16) -> uint8:\n"
        "    i: uint16 = 0\n"
        "    while i < n:\n"
        "        bit: uint8 = 0\n"
        "        while bit < 8:\n"
        "            bit = bit + 1\n"
        "        i = i + 1\n"
    )

    assert found == [], found


def test_the_sweep_leaves_a_counter_that_can_reach_its_count(tmp_path):
    """The invariant beside it: a wide counter on a wide count is not a finding.

    Without this the previous test passes for a matcher that flags every local named `i`.
    """
    found = _sweep_over(tmp_path,
        "def write_bytes(self, addr: uint8, data: bytearray, n: uint16):\n"
        "    i: uint16 = 0\n"
        "    while i < n:\n"
        "        i = i + 1\n"
    )

    assert found == [], found

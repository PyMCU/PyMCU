"""A module-level object in the entry file is usable from every function, not only main.

The entry file has no synthesized `__module_init`: its module level is injected into
main's body instead, so main IS the entry file's init, and lowering it is what gives
the instance's fields their storage. Functions were lowered in source order, so a
helper defined ABOVE main was lowered first and read the fields as run-time values.
`Pin.high()` is `self._port[self._bit] = 1`, and a run-time bit index is only legal on
a constant port address, so the build failed with

    error: TypeError: runtime bit index is only supported on a chip register
           (a constant port address); indexing a bit through a runtime pointer is
           not yet supported

naming an operation the program does not perform. The same two statements moved
inside main built. Issue #159, the entry-file twin of #117.

This lives here rather than in tests/unit because the diagnostic it is about only
fires when a chip has been bootstrapped: the check asks whether the port address is a
known chip register, and the unit harness constructs its IRGenerator without a chip,
so the same source fails there whatever the lowering order. Driving pymcuc with the
real stdlib is what tells the two orders apart.
"""

import json
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")

PORTD, DDRD = 0x2B, 0x2A


def build(tmp_path: Path, source: str):
    src = tmp_path / "main.py"
    src.write_text(source)
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(src), "-o", "/dev/null", "--target", "atmega328p",
         "--freq", "16000000", "-I", str(tmp_path), "-I", str(STDLIB),
         "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    out = proc.stdout + proc.stderr
    ir = json.loads(mir.read_text()) if "[BUILD_OK]" in proc.stdout and mir.exists() else None
    return out, ir


HELPER_ABOVE = (
    "from pymcu.hal.gpio import Pin\n\n"
    'led = Pin("PD5", Pin.OUT)\n\n\n'
    "def blink():\n"
    "    led.high()\n"
    "    led.low()\n\n\n"
    "def main():\n"
    "    blink()\n"
    "    while True:\n"
    "        pass\n"
)

HELPER_BELOW = (
    "from pymcu.hal.gpio import Pin\n\n"
    'led = Pin("PD5", Pin.OUT)\n\n\n'
    "def main():\n"
    "    blink()\n"
    "    while True:\n"
    "        pass\n\n\n"
    "def blink():\n"
    "    led.high()\n"
    "    led.low()\n"
)

TWO_DEEP = (
    "from pymcu.hal.gpio import Pin\n\n"
    'led = Pin("PD5", Pin.OUT)\n\n\n'
    "def inner():\n"
    "    led.high()\n"
    "    led.low()\n\n\n"
    "def outer():\n"
    "    inner()\n\n\n"
    "def main():\n"
    "    outer()\n"
    "    while True:\n"
    "        pass\n"
)

TWO_INSTANCES = (
    "from pymcu.hal.gpio import Pin\n\n"
    'a = Pin("PD5", Pin.OUT)\n'
    'b = Pin("PB1", Pin.OUT)\n\n\n'
    "def both():\n"
    "    a.high()\n"
    "    b.high()\n\n\n"
    "def main():\n"
    "    both()\n"
    "    while True:\n"
    "        pass\n"
)

NO_CONSTRUCTION = (
    "from pymcu.hal.gpio import Pin\n\n\n"
    "def twice(v: uint8) -> uint8:\n"
    "    return v * 2\n\n\n"
    "def main():\n"
    '    led = Pin("PD5", Pin.OUT)\n'
    "    led.high()\n"
    "    twice(2)\n"
    "    while True:\n"
    "        pass\n"
)


def function_names(ir):
    return [f["name"] for f in ir["functions"]]


def mem_addresses(fn):
    """Every I/O register a function's instructions name."""
    out = set()

    def walk(node):
        if isinstance(node, dict):
            if node.get("$t") == "mem" and "address" in node:
                out.add(node["address"])
            for v in node.values():
                walk(v)
        elif isinstance(node, list):
            for v in node:
                walk(v)

    walk(fn["body"])
    return out


@pytest.mark.parametrize("name,source", [
    ("helper_above", HELPER_ABOVE),
    ("helper_below", HELPER_BELOW),
    ("two_deep", TWO_DEEP),
    ("two_instances", TWO_INSTANCES),
], ids=lambda v: v if isinstance(v, str) and " " not in v and "\n" not in v else "")
def test_a_module_level_pin_is_usable_from_any_function(tmp_path, name, source):
    out, ir = build(tmp_path, source)
    assert ir is not None, f"{name} failed to build:\n{out}"


def test_the_helper_actually_drives_the_port(tmp_path):
    """Building is not enough: the emitted helper has to reach PORTD."""
    out, ir = build(tmp_path, HELPER_ABOVE)
    assert ir is not None, out

    blink = next(f for f in ir["functions"] if f["name"] == "blink")
    assert PORTD in mem_addresses(blink), \
        f"blink() names no PORTD access; it touches {[hex(a) for a in sorted(mem_addresses(blink))]}"


def test_the_program_uses_the_bit_the_pin_names(tmp_path):
    """PD5 is bit 5, and main's injected module level is what establishes it.

    How it establishes it changed with #175. Before, the bit was a run-time value main
    copied into `led__bit` storage for the helper to read. Now that a read-only method no
    longer forces the field mutable, it is a compile-time constant and appears as the bit
    of the operations themselves. Either way the program has to use bit 5, so that is what
    is asserted rather than the storage that used to carry it.
    """
    out, ir = build(tmp_path, HELPER_ABOVE)
    assert ir is not None, out

    def bits_used(fn):
        return {i["bit"] for i in fn["body"] if i.get("$t") in ("bset", "bclr", "bwrt")}

    stored = any(i.get("$t") == "copy"
                 and i["src"].get("$t") == "const" and i["src"]["value"] == 5
                 and str(i["dst"].get("name", "")).endswith("_bit")
                 for f in ir["functions"] for i in f["body"])
    folded = 5 in set().union(*(bits_used(f) for f in ir["functions"]))

    assert stored or folded, \
        "the program must reach bit 5, either through the instance's stored bit or folded"


def test_the_direction_register_is_still_configured(tmp_path):
    """The construction sets PD5 as an output, and hoisting must not drop it."""
    out, ir = build(tmp_path, HELPER_ABOVE)
    assert ir is not None, out
    main = next(f for f in ir["functions"] if f["name"] == "main")
    assert DDRD in mem_addresses(main), "Pin(..., Pin.OUT) must still configure DDRD"


def test_emission_order_is_unchanged(tmp_path):
    """Hoisting is for LOWERING order only; the helper is still emitted before main."""
    out, ir = build(tmp_path, HELPER_ABOVE)
    assert ir is not None, out
    names = function_names(ir)
    assert names.index("blink") < names.index("main")


def test_an_entry_file_with_no_module_level_construction_keeps_source_order(tmp_path):
    """The gate. Lowering order is observable through the shared label, temporary and
    string-literal counters, so a program with nothing to bind must not be reordered."""
    out, ir = build(tmp_path, NO_CONSTRUCTION)
    assert ir is not None, out
    names = function_names(ir)
    assert names.index("twice") < names.index("main")

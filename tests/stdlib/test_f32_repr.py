"""Run `_f32_repr`/`_f32_scale` as ordinary Python and read what they print.

Same technique as test_uart_text_writers.py: the HAL source is pure arithmetic
over an output buffer, so it can be executed on the host and compared against
a reference instead of inspected as IR or run through the AVR emulator. That
catches a digit bug directly, at fuzzing scale, instead of one probe at a
time.

The policy under test is MicroPython's float32 print: exactly 7 significant
digits, half-to-even on the value's EXACT decimal expansion, trailing zeros
dropped, CPython's repr layout (fixed notation while the decimal point sits
inside -4..16, scientific outside it). The reference below gets the same
answer a different way: widening the float32 to float64 is exact, so
formatting that float64 with Python's own `%.6e` (correctly rounded,
half-to-even) reproduces the same 7-significant-digit rounding decision.
"""

import ast
import math
import struct
import textwrap
from pathlib import Path

import pytest

HAL = Path(__file__).resolve().parents[2] / "lib" / "src" / "pymcu" / "hal" / "uart_text.py"

WIDTH = {"uint8": 8, "uint16": 16, "uint32": 32, "int16": 16, "int32": 32}


def truncate(name, value):
    bits = WIDTH.get(name)
    if bits is None:
        return value
    value = int(value) & ((1 << bits) - 1)
    if name.startswith("int") and value >> (bits - 1):
        value -= 1 << bits
    return value


class Narrow(ast.NodeTransformer):
    """Wrap every write to an annotated local in its own type -- see
    test_uart_text_writers.py's copy of this for why it is needed."""

    def __init__(self):
        self.types = {}

    def visit_AnnAssign(self, node):
        self.generic_visit(node)
        if isinstance(node.target, ast.Name) and isinstance(node.annotation, ast.Name):
            name = node.annotation.id
            if name in WIDTH and node.value is not None:
                self.types[node.target.id] = name
                node.value = ast.Call(func=ast.Name(id="__narrow", ctx=ast.Load()),
                                      args=[ast.Constant(name), node.value], keywords=[])
        return node

    def visit_AugAssign(self, node):
        self.generic_visit(node)
        if isinstance(node.target, ast.Name) and node.target.id in self.types:
            return ast.Assign(
                targets=[ast.Name(id=node.target.id, ctx=ast.Store())],
                value=ast.Call(
                    func=ast.Name(id="__narrow", ctx=ast.Load()),
                    args=[ast.Constant(self.types[node.target.id]),
                          ast.BinOp(left=ast.Name(id=node.target.id, ctx=ast.Load()),
                                    op=node.op, right=node.value)],
                    keywords=[]))
        return node


def _load():
    tree = ast.parse(HAL.read_text())
    wanted = [n for n in tree.body
              if isinstance(n, ast.FunctionDef) and n.name in ("_f32_scale", "_f32_repr")]
    assert [n.name for n in wanted] == ["_f32_scale", "_f32_repr"], \
        "uart_text.py no longer defines both halves of the float32 repr"

    env = {"__narrow": truncate,
           # Only value the compiled code ever bitcasts is a float into a
           # uint32, so the target-type argument can be ignored here.
           "bitcast": lambda t, v: struct.unpack("<I", struct.pack("<f", v))[0],
           "uint8": lambda v: truncate("uint8", v), "uint16": lambda v: truncate("uint16", v),
           "uint32": lambda v: truncate("uint32", v), "int16": lambda v: truncate("int16", v),
           "int32": lambda v: truncate("int32", v)}
    body = []
    for node in wanted:
        node = Narrow().visit(ast.parse(textwrap.dedent(ast.unparse(node))).body[0])
        node.decorator_list = []
        ast.fix_missing_locations(node)
        body.append(node)
    exec(compile(ast.Module(body=body, type_ignores=[]), "<hal>", "exec"), env)
    return env


def f32(v):
    """Round a Python float to its nearest float32, widened back exactly."""
    return struct.unpack("<f", struct.pack("<f", v))[0]


def py_ref(value):
    """MicroPython's float32 print policy, computed a different way than the
    HAL does it: %.6e on the exact float64 (== the float32's exact value,
    widening loses nothing) is CPython's own correctly-rounded, half-to-even
    7-significant-digit decimal expansion."""
    v = f32(value)
    if v != v:
        return "nan"
    if v == float("inf"):
        return "inf"
    if v == float("-inf"):
        return "-inf"
    if v == 0.0:
        return "-0.0" if math.copysign(1.0, v) < 0 else "0.0"
    neg = v < 0
    av = -v if neg else v
    mant, exp = ("%.6e" % av).split("e")
    exp = int(exp)
    digits = mant.replace(".", "").rstrip("0") or "0"
    decpt = exp + 1
    if decpt <= -4 or decpt > 16:
        out = digits[0]
        if len(digits) > 1:
            out += "." + digits[1:]
        e = decpt - 1
        out += "e" + ("+%02d" % e if e >= 0 else "-%02d" % -e)
    elif decpt <= 0:
        out = "0." + "0" * (-decpt) + digits
    elif len(digits) <= decpt:
        out = digits + "0" * (decpt - len(digits)) + ".0"
    else:
        out = digits[:decpt] + "." + digits[decpt:]
    return ("-" if neg else "") + out


@pytest.fixture(scope="module")
def f32_repr():
    env = _load()

    def run(value):
        out = bytearray(20)
        n = env["_f32_repr"](value, out)
        assert n <= 19, f"_f32_repr wrote {n} bytes for {value!r}, more than the 19 it is bounded to"
        return bytes(out[:n]).decode("ascii")
    return run


NAMED = [0.0, -0.0, 1.0, 0.1, 0.001, 1e-5, 123.456, 3.14159265, 1e7, 1e16, 1e20,
         16777216.0, 16777217.0, 0.30000001, 2 / 3, 1 / 3, math.pi,
         float("inf"), float("-inf"), float("nan")]


@pytest.mark.parametrize("value", NAMED)
def test_named_values_match_micropythons_policy(f32_repr, value):
    assert f32_repr(value) == py_ref(value)


@pytest.mark.parametrize("value, expected", [
    (math.pi, "3.141593"), (1 / 3, "0.3333333"), (1e20, "1e+20"), (1e-5, "1e-05"),
    (123.456, "123.456"), (0.30000001, "0.3"), (2 / 3, "0.6666667"), (0.1, "0.1"),
])
def test_named_values_match_the_orchestrators_examples(f32_repr, value, expected):
    """The exact strings from the handoff -- spelled out, not derived, so a
    change to py_ref cannot silently take these with it."""
    assert f32_repr(value) == expected


def test_edge_bit_patterns(f32_repr):
    edges = {
        0x00000001: "smallest subnormal",
        0x007fffff: "largest subnormal",
        0x00800000: "smallest normal",
        0x7f7fffff: "largest finite",
        0x3f800000: "1.0",
        0x40000000: "2.0",
        0x3f000000: "0.5",
    }
    for bits, label in edges.items():
        v = struct.unpack("<f", struct.pack("<I", bits))[0]
        assert f32_repr(v) == py_ref(v), label


def test_fuzz_random_bit_patterns(f32_repr):
    import random
    random.seed(1234)
    fails = []
    for _ in range(20000):
        bits = random.getrandbits(32)
        v = struct.unpack("<f", struct.pack("<I", bits))[0]
        if v != v:
            continue
        got, exp = f32_repr(v), py_ref(v)
        if got != exp:
            fails.append((bits, v, got, exp))
    assert not fails, fails[:10]

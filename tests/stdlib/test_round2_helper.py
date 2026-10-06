"""Run `_pymcu_round2` as ordinary Python and compare it against decimal
half-to-even on the exact float32 expansion -- the routine's own contract.

Same technique as test_uart_text_writers.py: the helper is pure arithmetic,
so it executes on the host with its annotated locals truncated to their
declared types on every write. The truncation is what makes the OLD bug
visible here: the `frac: uint32` accumulator wrapped at ten digits, the same
wrap the compiled code showed on the chip (round(0.5, 10) -> 0.07050327).

The reference is NOT a re-port of the routine: it quantizes the float32's
exact Decimal expansion half-to-even -- the same rounding
_float_fmt_digits performs internally -- and reads back a float32, plus an
explicit sign check so -0.0 stays distinct from +0.0.
"""

import ast
import struct
import textwrap
from decimal import Decimal, ROUND_HALF_EVEN
from pathlib import Path

import pytest

LIB = Path(__file__).resolve().parents[2] / "lib" / "src" / "pymcu"

WIDTH = {"uint8": 8, "uint16": 16, "uint32": 32, "int16": 16, "int32": 32, "int8": 8}


def f32(value):
    return struct.unpack("<f", struct.pack("<f", value))[0]


def truncate(name, value):
    if name == "float":
        return f32(value)
    bits = WIDTH.get(name)
    if bits is None:
        return value
    value = int(value) & ((1 << bits) - 1)
    if name.startswith("int") and value >> (bits - 1):
        value -= 1 << bits
    return value


class Narrow(ast.NodeTransformer):
    """Wrap every write to an annotated local in its declared type."""

    def __init__(self):
        self.types = {}

    def visit_AnnAssign(self, node):
        self.generic_visit(node)
        if isinstance(node.target, ast.Name) and isinstance(node.annotation, ast.Name):
            name = node.annotation.id
            if (name in WIDTH or name == "float") and node.value is not None:
                self.types[node.target.id] = name
                node.value = ast.Call(func=ast.Name(id="__narrow", ctx=ast.Load()),
                                      args=[ast.Constant(name), node.value], keywords=[])
        return node

    def visit_Assign(self, node):
        self.generic_visit(node)
        if (len(node.targets) == 1 and isinstance(node.targets[0], ast.Name)
                and node.targets[0].id in self.types):
            name = node.targets[0].id
            node.value = ast.Call(func=ast.Name(id="__narrow", ctx=ast.Load()),
                                  args=[ast.Constant(self.types[name]), node.value], keywords=[])
        return node


def _load():
    uint32_fn = lambda v: truncate("uint32", v)
    env = {"__narrow": truncate,
           "bitcast": lambda t, v: (struct.unpack("<I", struct.pack("<f", v))[0] if t is uint32_fn
                                     else struct.unpack("<f", struct.pack("<I", int(v) & 0xFFFFFFFF))[0]),
           "uint8": lambda v: truncate("uint8", v), "uint16": lambda v: truncate("uint16", v),
           "uint32": uint32_fn, "int16": lambda v: truncate("int16", v),
           "int32": lambda v: truncate("int32", v), "int8": lambda v: truncate("int8", v),
           "float": lambda v: truncate("float", v)}
    wanted = {"_float_fmt_digits": LIB / "hal" / "uart_text.py",
              "_pymcu_round2": LIB / "round2.py"}
    body = []
    for name, source in wanted.items():
        tree = ast.parse(source.read_text())
        for node in tree.body:
            if isinstance(node, ast.FunctionDef) and node.name == name:
                node = Narrow().visit(ast.parse(textwrap.dedent(ast.unparse(node))).body[0])
                node.decorator_list = []
                ast.fix_missing_locations(node)
                body.append(node)
    exec(compile(ast.Module(body=body, type_ignores=[]), "<round2>", "exec"), env)
    return env["_pymcu_round2"]


ROUND2 = _load()


def ref_round(value, n):
    """Half-to-even on the float32's exact decimal expansion, as a float32."""
    d = Decimal(f32(value))
    q = d.quantize(Decimal(1).scaleb(-n), rounding=ROUND_HALF_EVEN)
    return f32(float(q))


def bits(value):
    return struct.unpack("<I", struct.pack("<f", value))[0]


CASES = [
    # (value, n): values a float32 represents exactly keep the host and the
    # chip looking at the same number.
    (2.675, 2), (1.005, 2),
    (1234.5, -2), (1750.0, -2), (-1750.0, -2), (999.9, -2), (7.77, 0),
]


@pytest.mark.parametrize("value,n", CASES)
def test_round2_matches_half_to_even(value, n):
    assert bits(ROUND2(f32(value), n)) == bits(ref_round(value, n))


@pytest.mark.parametrize("n", [-2, 0, 2, 5, 15])
def test_round2_keeps_the_negative_zero_sign(n):
    result = ROUND2(f32(-0.0), n)
    assert result == 0.0
    assert bits(result) == bits(-0.0)

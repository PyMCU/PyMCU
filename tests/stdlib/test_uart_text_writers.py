"""Run the UART text writers as ordinary Python and read what they print.

These helpers are pure arithmetic over a single `uart_write(byte)` primitive, so
they can be executed on the host instead of inspected as IR -- which is the only
way to catch a writer that compiles perfectly and prints the wrong number.

The one thing the host does not model for free is fixed-width arithmetic, so the
annotated locals are truncated to their declared type on every assignment, the
same way the generated code does.
"""

import ast
import textwrap
from pathlib import Path

import pytest

HAL = Path(__file__).resolve().parents[2] / "lib" / "src" / "pymcu" / "hal"

WRITERS = ("uart_write_decimal_u8", "uart_write_decimal_u16", "uart_write_decimal_i16",
           "uart_write_decimal_u32", "uart_write_decimal_i32", "uart_write_float")

SOURCES = {
    "avr": HAL / "avr" / "uart" / "avr.py",
    "atmega32u4": HAL / "avr" / "uart" / "atmega32u4.py",
    "attiny2313": HAL / "avr" / "uart" / "attiny2313.py",
    "pic18": HAL / "pic18" / "pic18_uart.py",
    "rp": HAL / "rp" / "console.py",
}

WIDTH = {"uint8": 8, "uint16": 16, "uint32": 32, "int16": 16, "int32": 32}


def truncate(name, value):
    """Apply the declared width, so a uint8 that overflows wraps like the chip."""
    bits = WIDTH.get(name)
    if bits is None or isinstance(value, float):
        return value
    value = int(value) & ((1 << bits) - 1)
    if name.startswith("int") and value >> (bits - 1):
        value -= 1 << bits
    return value


class Narrow(ast.NodeTransformer):
    """Wrap every write to an annotated local in its own type, and each augmented
    assignment to one, so `value -= 100` cannot go negative on a uint8."""

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


def load(source: Path):
    """Execute every writer a HAL file defines, in one namespace: they call each other."""
    tree = ast.parse(source.read_text())
    wanted = [n for n in tree.body
              if isinstance(n, ast.FunctionDef) and n.name in WRITERS]
    if not wanted:
        return {}

    printed = []
    env = {"__narrow": truncate,
           "uart_write": lambda b: printed.append(int(b) & 0xFF),
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

    def caller(name, argument_type):
        if name not in env:
            return None

        def run(value):
            printed.clear()
            env[name](truncate(argument_type, value) if argument_type else value)
            return "".join(chr(b) for b in printed)
        return run

    return {n.name: caller(n.name, ARGUMENT_TYPE[n.name]) for n in wanted}


ARGUMENT_TYPE = {"uart_write_decimal_u8": "uint8", "uart_write_decimal_u16": "uint16",
                 "uart_write_decimal_i16": "int16", "uart_write_decimal_u32": "uint32",
                 "uart_write_decimal_i32": "int32", "uart_write_float": None}


@pytest.mark.parametrize("value", [0, 1, 9, 10, 99, 100, 200, 255])
def test_the_u8_writer_prints_the_number(value):
    """Sanity check on the oracle itself before it is used to accuse anyone."""
    run = load(SOURCES["avr"])["uart_write_decimal_u8"]
    assert run(value) == str(value)


@pytest.mark.parametrize("value", [0, 7, 65535, 12345])
def test_the_u16_writer_prints_the_number(value):
    run = load(SOURCES["avr"])["uart_write_decimal_u16"]
    assert run(value) == str(value)


@pytest.mark.parametrize("value", [-32768, -1, 0, 32767])
def test_the_i16_writer_prints_the_number(value):
    run = load(SOURCES["avr"])["uart_write_decimal_i16"]
    assert run(value) == str(value)


@pytest.mark.parametrize("value", [-2147483648, -1, 0, 2147483647])
def test_the_i32_writer_prints_the_number(value):
    run = load(SOURCES["avr"])["uart_write_decimal_i32"]
    assert run(value) == str(value)


@pytest.mark.parametrize("hal", sorted(SOURCES))
@pytest.mark.parametrize("value", [0.0, 1.5, 12.5, 99.9, 1234.5, 60000.0])
def test_every_float_writer_prints_the_integer_part(hal, value):
    """The integer part is not a matter of taste: a wrong digit is a wrong number.

    How many decimals a HAL prints is a design choice each one may make; printing
    `<34.5` for 1234.5 is not one. Three of the five copies of this function
    overflow their accumulator and emit punctuation instead of digits.
    """
    run = load(SOURCES[hal]).get("uart_write_float")
    if run is None:
        pytest.skip(f"{hal} has no float writer")
    printed = run(value)
    assert "." in printed, f"{hal} printed {printed!r} with no decimal point"
    integer_part = printed.split(".")[0]
    assert integer_part.isdigit(), \
        f"{hal} printed {printed!r} for {value}: the integer part is not digits"
    assert int(integer_part) == int(value), \
        f"{hal} printed {printed!r} for {value}"

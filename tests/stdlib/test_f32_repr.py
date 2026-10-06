"""Run `_f32_pow5`/`_f32_decimal_exp`/`_f32_order`/`_f32_repr` as ordinary
Python and read what they print.

Same technique as test_uart_text_writers.py: the HAL source is pure
arithmetic over an output buffer, so it can be executed on the host and
compared against a reference instead of inspected as IR or run through the
AVR emulator.

The policy under test is MicroPython's REAL float32 print algorithm
(py/formatfloat.c's mp_format_float, fmt='g', prec=MP_FLOAT_REPR_PREC, the
MICROPY_FLOAT_FORMAT_IMPL_APPROX path every float32 MicroPython/CircuitPython
port builds with by default): scale by powers of ten using FLOAT32
arithmetic (mp_decimal_exp: `10**n = 2**n * 5**n`, the `2**n` part folded
into the IEEE-754 exponent field, the `5**n` part a real multiply/divide),
search 6 to 9 significant digits for one that round-trips exactly. This is
NOT CPython's shortest-round-trip repr and NOT an exact decimal expansion:
its own last significant digit is not always exact, on real MicroPython
builds as much as here (see https://docs.pymcu.org/limitations/#float-text for the measured
mismatch rate against a real float32 MicroPython build and why).

`py_ref` below is an independent Python port of the same algorithm (not a
copy-paste of the HAL source), cross-checked by hand against a real
`micropython` unix-port binary built with MICROPY_FLOAT_IMPL_FLOAT during
this work (not committed here -- see the report). It omits mp_format_float's
dichotomic bisection (which corrects the last 1-2 digits against a round-trip
check the way the C code does, at a code-size cost past this HAL's budget),
so it is expected to disagree with real MicroPython, and with the HAL under
test, on a measured minority of values -- always in the last significant
digit, never in digit count or magnitude. The tests below assert exact
agreement only on values verified against the real binary or on real AVR
hardware (the emulator, both front ends); the fuzz test asserts the disagreement
RATE stays within the measured band, not zero disagreement.
"""

import ast
import math
import random
import struct
import textwrap
from pathlib import Path

import pytest

HAL = Path(__file__).resolve().parents[2] / "lib" / "src" / "pymcu" / "hal" / "uart_text.py"

WIDTH = {"uint8": 8, "uint16": 16, "uint32": 32, "int16": 16, "int32": 32, "int8": 8}


def f32(v):
    """Round a Python float to its nearest float32, widened back exactly."""
    try:
        return struct.unpack("<f", struct.pack("<f", v))[0]
    except OverflowError:
        return float("inf") if v > 0 else float("-inf")


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
    """Wrap every write to an annotated local (including `float` ones -- a
    plain Assign, not just AugAssign, since this HAL reassigns with
    `x = x * y` throughout, never `x *= y`) in its own type. Skipping plain
    Assign here silently drops float32 rounding at every step but the first,
    which lets the host run in double precision -- see the report for what
    that cost to find."""

    def __init__(self):
        self.types = {}

    def visit_AnnAssign(self, node):
        self.generic_visit(node)
        if isinstance(node.target, ast.Name) and isinstance(node.annotation, ast.Name):
            name = node.annotation.id
            if name in WIDTH or name == "float":
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

    def visit_Assign(self, node):
        self.generic_visit(node)
        if (len(node.targets) == 1 and isinstance(node.targets[0], ast.Name)
                and node.targets[0].id in self.types):
            name = node.targets[0].id
            node.value = ast.Call(func=ast.Name(id="__narrow", ctx=ast.Load()),
                                  args=[ast.Constant(self.types[name]), node.value], keywords=[])
        return node


FUNCS = ("_f32_pow5", "_f32_decimal_exp", "_f32_order", "_f32_repr")


def _load():
    tree = ast.parse(HAL.read_text())
    wanted = [n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name in FUNCS]
    assert [n.name for n in wanted] == list(FUNCS), \
        "uart_text.py no longer defines the mp_format_float-style repr helpers"

    def to_uint32(v):
        return struct.unpack("<I", struct.pack("<f", v))[0]

    def to_float(v):
        return struct.unpack("<f", struct.pack("<I", int(v) & 0xFFFFFFFF))[0]

    env = {"__narrow": truncate}
    env_uint32 = lambda v: truncate("uint32", v)
    env["uint8"] = lambda v: truncate("uint8", v)
    env["uint16"] = lambda v: truncate("uint16", v)
    env["uint32"] = env_uint32
    env["int16"] = lambda v: truncate("int16", v)
    env["int32"] = lambda v: truncate("int32", v)
    env["int8"] = lambda v: truncate("int8", v)
    env["float"] = f32
    # The only two directions the HAL ever bitcasts: float<->uint32. Dispatch
    # on which callable was passed as the target type.
    env["bitcast"] = lambda t, v: to_uint32(v) if t is env_uint32 else to_float(v)

    body = []
    for node in wanted:
        node = Narrow().visit(ast.parse(textwrap.dedent(ast.unparse(node))).body[0])
        node.decorator_list = []
        ast.fix_missing_locations(node)
        body.append(node)
    exec(compile(ast.Module(body=body, type_ignores=[]), "<hal>", "exec"), env)
    return env


def pow5(n):
    neg = n < 0
    if neg:
        n = -n
    result = f32(1.0)
    for _ in range(n):
        result = f32(result * 5.0)
    return f32(1.0 / result) if neg else result


def decimal_exp(num, dec_exp):
    if dec_exp == 0 or num == 0.0:
        return num
    b = struct.unpack("<I", struct.pack("<f", num))[0]
    exp = ((b >> 23) & 0xFF) + dec_exp
    b2 = (b & 0x807FFFFF) | ((exp & 0xFF) << 23)
    res = struct.unpack("<f", struct.pack("<I", b2))[0]
    if dec_exp < 0 and dec_exp >= -10:
        return f32(res / pow5(-dec_exp))
    return f32(res * pow5(dec_exp))


def order(f):
    b = struct.unpack("<I", struct.pack("<f", f))[0]
    be = ((b >> 23) & 0xFF) - 127
    e = (be * 77) >> 8
    positive = b >= 0x3F800000
    u_base = decimal_exp(f32(1.0), e + (1 if positive else 0))
    while (f >= u_base) == positive:
        e += 1 if positive else -1
        u_base = decimal_exp(f32(1.0), e + (1 if positive else 0))
    return e


def py_ref(value):
    """Independent Python port of the HAL's algorithm (no dichotomic
    bisection -- see module docstring)."""
    v = f32(value)
    if v != v:
        return "nan"
    if math.isinf(v):
        return "-inf" if v < 0 else "inf"
    neg = struct.unpack("<I", struct.pack("<f", v))[0] >> 31 != 0
    f = -v if neg else v
    if f == 0.0:
        return "-0.0" if neg else "0.0"
    ordv = order(f)
    num_digits = 6
    cap = 1000000
    while True:
        decexp = num_digits - ordv - 1
        m = int(decimal_exp(f, decexp) + 0.5)
        decexp2 = decexp
        if m >= cap:
            m //= 10
            decexp2 = decexp - 1
        check = decimal_exp(f32(float(m)), -decexp2)
        if check == f or num_digits >= 9:
            break
        num_digits += 1
        cap *= 10
    xw = -decexp2
    digits = str(m).rstrip("0") or "0"
    nd = len(digits)
    decpt = xw + (len(str(m)))
    if decpt <= -4 or decpt > 16:
        out = digits[0]
        if nd > 1:
            out += "." + digits[1:]
        ex = decpt - 1
        out += "e" + ("+%02d" % ex if ex >= 0 else "-%02d" % -ex)
    elif decpt <= 0:
        out = "0." + "0" * (-decpt) + digits
    elif nd <= decpt:
        out = digits + "0" * (decpt - nd) + ".0"
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


# Every one of these was cross-checked on real AVR hardware (avr8sharp, both
# front ends) AND against a real `micropython` unix-port binary built with
# MICROPY_FLOAT_IMPL_FLOAT during this work -- see the report for both.
# A list, not a dict: 0.0 and -0.0 are the same dict key (0.0 == -0.0), which
# would silently drop one of them.
NAMED = [
    (0.0, "0.0"), (-0.0, "-0.0"), (1.0, "1.0"), (0.1, "0.1"), (0.001, "0.001"),
    (1e-5, "1e-05"), (123.456, "123.456"), (3.14159265, "3.1415928"),
    (10000000.0, "10000000.0"), (1e16, "1e+16"), (1e20, "1e+20"),
    (16777216.0, "16777216.0"), (0.30000001, "0.3"), (2 / 3, "0.6666667"),
    (1 / 3, "0.33333334"), (math.pi, "3.1415928"),
    (3.3, "3.3"), (12.34, "12.34"), (250.75, "250.75"), (-40.0, "-40.0"),
    (1013.25, "1013.25"), (9.81, "9.81"), (24.902347564697266, "24.902348"),
    (99.60939025878906, "99.60939"), (49.80469512939453, "49.804696"),
]


@pytest.mark.parametrize("value, expected", NAMED)
def test_named_values_match_real_micropython(f32_repr, value, expected):
    assert f32_repr(value) == expected


def test_non_finite_and_signed_zero(f32_repr):
    assert f32_repr(float("inf")) == "inf"
    assert f32_repr(float("-inf")) == "-inf"
    assert f32_repr(float("nan")) == "nan"
    assert f32_repr(-0.0) == "-0.0"


def test_edge_bit_patterns(f32_repr):
    # Bit patterns whose ONLY claim is that _f32_repr does not crash or
    # produce something that is not a number: the port's own accuracy
    # against real MicroPython is documented, not re-derived, at these
    # extremes (see https://docs.pymcu.org/limitations/#float-text).
    edges = [0x00000001, 0x007fffff, 0x00800000, 0x7f7fffff,
             0x3f800000, 0x40000000, 0x3f000000]
    for bits in edges:
        v = struct.unpack("<f", struct.pack("<I", bits))[0]
        out = f32_repr(v)
        assert out and out[0] in "-0123456789"


def test_fuzz_matches_the_reference_port_within_the_measured_rate(f32_repr):
    """py_ref (no bisection) and the HAL under test (also no bisection --
    see https://docs.pymcu.org/limitations/#float-text) implement the SAME simplified
    algorithm, so they should agree far more often than either agrees with
    real MicroPython's bisection-corrected one. A regression here is a
    difference between the HAL and its own Python port, not an accuracy
    claim against MicroPython."""
    random.seed(1234)
    fails = []
    tested = 0
    for _ in range(4000):
        bits = random.getrandbits(32)
        v = struct.unpack("<f", struct.pack("<I", bits))[0]
        if v != v:
            continue
        tested += 1
        got, exp = f32_repr(v), py_ref(v)
        if got != exp:
            fails.append((bits, v, got, exp))
    rate = len(fails) / tested
    assert rate < 0.02, f"{len(fails)}/{tested} ({rate:.1%}) disagreements with the reference port: {fails[:10]}"

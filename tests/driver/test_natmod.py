# -----------------------------------------------------------------------------
# PyMCU -- tests for the native-module (.mpy) signature conversion and adapter
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------

"""
What crosses the native-module boundary, and what is refused with which sentence.

These tests do not need a toolchain: the conversion reads the annotations and the
adapter is text. That is the point of testing here rather than only end to end --
a refusal has to be readable, and a generated adapter has to be reviewable, and
neither fact is visible in a .mpy.
"""

from pathlib import Path

import pytest

from src.driver.commands.natmod import (
    NatmodError,
    collect_exports,
    generate_adapter,
)


def write(tmp_path: Path, source: str) -> Path:
    p = tmp_path / "main.py"
    p.write_text(source)
    return p


# ── what crosses ─────────────────────────────────────────────────────────────


def test_scalars_and_buffer_convert(tmp_path):
    src = write(tmp_path, (
        "def add(a: int, b: int) -> int:\n"
        "    return a + b\n"
        "\n"
        "def brightness(buf: bytearray, n: int, scale: uint8) -> None:\n"
        "    buf[0] = scale\n"
    ))
    exports = collect_exports(src)
    assert [e.name for e in exports] == ["add", "brightness"]

    add, brightness = exports
    # A bare `int` is 16 bits on this backend, and the adapter has to truncate to the
    # same width the kernel's own code generation used.
    assert [p.ctype for p in add.params] == ["int16_t", "int16_t"]
    assert add.ret_ctype == "int16_t"
    assert brightness.ret_ctype is None
    assert brightness.params[0].kind == "buffer"


def test_mutability_decides_the_buffer_flag(tmp_path):
    src = write(tmp_path, (
        "def ro(b: bytes) -> None:\n"
        "    pass\n"
        "\n"
        "def rw(b: bytearray) -> None:\n"
        "    pass\n"
    ))
    ro, rw = collect_exports(src)
    assert ro.params[0].buffer_flag == "MP_BUFFER_READ"
    assert ro.params[0].ctype == "const uint8_t *"
    assert rw.params[0].buffer_flag == "MP_BUFFER_RW"


def test_inline_is_not_exported(tmp_path):
    """@inline is a template expanded at each call site, so there is no symbol to bind."""
    src = write(tmp_path, (
        "@inline\n"
        "def helper(a: int) -> int:\n"
        "    return a\n"
        "\n"
        "def go(a: int) -> int:\n"
        "    return helper(a)\n"
    ))
    assert [e.name for e in collect_exports(src)] == ["go"]


# ── what is refused, and with what sentence ──────────────────────────────────


@pytest.mark.parametrize(
    "source, expected",
    [
        ("def f(x: float) -> int:\n    return 1\n", "floating point"),
        ("def f(s: str) -> int:\n    return 1\n", "take a bytes or bytearray instead"),
        ("def f(t: tuple) -> int:\n    return 1\n", "a tuple is an interpreter object"),
        ("def f(d: dict) -> int:\n    return 1\n", "a dict is an interpreter object"),
        ("def f(a) -> int:\n    return 1\n", "has no type annotation"),
        ("def f(a: int):\n    return 1\n", "has no return annotation"),
        ("def f(a: int) -> float:\n    return 1.0\n", "floating point"),
        ("def f(n: int) -> bytearray:\n    return bytearray(n)\n",
         "cannot hand back a buffer it allocated"),
        ("def f(*a: int) -> int:\n    return 1\n", "fixed argument count"),
        ("def f(a: int = 1) -> int:\n    return a\n", "fixed argument count"),
        ("async def f(a: int) -> int:\n    return a\n", "cannot be coroutines"),
        ("x = 1\n", "exports no function"),
    ],
)
def test_refusals_say_why(tmp_path, source, expected):
    src = write(tmp_path, source)
    with pytest.raises(NatmodError) as e:
        collect_exports(src)
    assert expected in str(e.value)


def test_a_refusal_is_located_at_the_def(tmp_path):
    """The line is the function's own, not the top of the file: a module with three
    good kernels and one bad one has to point at the bad one."""
    src = write(tmp_path, (
        "def ok(a: int) -> int:\n"
        "    return a\n"
        "\n"
        "\n"
        "def bad(x: float) -> int:\n"
        "    return 1\n"
    ))
    with pytest.raises(NatmodError) as e:
        collect_exports(src)
    assert str(e.value).startswith(f"{src}:5:")


def test_an_unknown_class_is_refused_by_name(tmp_path):
    src = write(tmp_path, (
        "class Thing:\n"
        "    pass\n"
        "\n"
        "\n"
        "def f(t: Thing) -> int:\n"
        "    return 1\n"
    ))
    with pytest.raises(NatmodError) as e:
        collect_exports(src)
    msg = str(e.value)
    assert "annotated 'Thing'" in msg
    # The refusal lists what DOES cross, so the reader is not left guessing.
    assert "bytearray" in msg and "uint8" in msg


# ── the generated adapter ────────────────────────────────────────────────────


def test_adapter_unboxes_boxes_and_registers(tmp_path):
    src = write(tmp_path, (
        "def add(a: int, b: int) -> int:\n"
        "    return a + b\n"
        "\n"
        "def brightness(buf: bytearray, n: int, scale: uint8) -> None:\n"
        "    buf[0] = scale\n"
    ))
    c = generate_adapter("kernels", collect_exports(src), src)

    # The kernels are declared with the widths PyMCU lowered them to.
    assert "extern int16_t add(int16_t, int16_t);" in c
    assert "extern void brightness(uint8_t *, int16_t, uint8_t);" in c
    # Unboxing, calling, boxing.
    assert "int16_t p0 = (int16_t)mp_obj_get_int(a0);" in c
    assert "mp_get_buffer_raise(a0, &b0, MP_BUFFER_RW);" in c
    assert "return mp_obj_new_int((mp_int_t)add(p0, p1));" in c
    assert "return mp_const_none;" in c
    # Registration under the source name.
    assert "mp_store_global(MP_QSTR_add," in c
    assert "mp_store_global(MP_QSTR_brightness," in c
    assert "MP_DYNRUNTIME_INIT_ENTRY" in c and "MP_DYNRUNTIME_INIT_EXIT" in c


def test_unsigned_return_is_boxed_unsigned(tmp_path):
    src = write(tmp_path, "def f(a: int) -> uint32:\n    return a\n")
    c = generate_adapter("m", collect_exports(src), src)
    assert "mp_obj_new_int_from_uint((mp_uint_t)f(p0))" in c


def test_bool_return_is_boxed_as_bool(tmp_path):
    src = write(tmp_path, "def f(a: int) -> bool:\n    return a\n")
    c = generate_adapter("m", collect_exports(src), src)
    assert "mp_obj_new_bool(f(p0))" in c


def test_more_than_three_arguments_pins_the_arity(tmp_path):
    """Past three parameters the fixed-arity macros run out, and the variadic one
    takes a MINIMUM. Pinning both bounds keeps a call with extra arguments from
    being accepted and then silently ignored."""
    src = write(tmp_path, (
        "def f(a: int, b: int, c: int, d: int) -> int:\n"
        "    return a\n"
    ))
    out = generate_adapter("m", collect_exports(src), src)
    assert "MP_DEFINE_CONST_FUN_OBJ_VAR_BETWEEN(nm_f_obj, 4, 4, nm_f);" in out
    assert "static mp_obj_t nm_f(size_t n_args, const mp_obj_t *args)" in out
    assert "mp_obj_t a3 = args[3];" in out


def test_three_arguments_still_uses_the_fixed_macro(tmp_path):
    src = write(tmp_path, "def f(a: int, b: int, c: int) -> int:\n    return a\n")
    out = generate_adapter("m", collect_exports(src), src)
    assert "MP_DEFINE_CONST_FUN_OBJ_3(nm_f_obj, nm_f);" in out

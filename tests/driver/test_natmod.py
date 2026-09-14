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
    check_no_module_state,
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
        "def brightness(buf: bytearray, scale: uint8) -> None:\n"
        "    buf[0] = scale\n"
    ))
    c = generate_adapter("kernels", collect_exports(src), src)

    # The kernels are declared with the widths PyMCU lowered them to.
    assert "extern int16_t add(int16_t, int16_t);" in c
    assert "extern void brightness(uint8_t *, uint8_t, uint32_t);" in c
    # Unboxing, calling, boxing.
    assert "mp_int_t r0 = mp_obj_get_int(a0);" in c
    assert "int16_t p0 = (int16_t)r0;" in c
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


# ── the checks the adapter puts between the interpreter and the kernel ───────


def test_an_out_of_range_int_raises_instead_of_wrapping(tmp_path):
    """add(40000, 1) returned -25535 on real silicon before this. A bare `int` is 16
    bits here by design, so the value cannot be carried -- but it must be refused, not
    delivered wrong."""
    src = write(tmp_path, "def add(a: int, b: int) -> int:\n    return a + b\n")
    c = generate_adapter("m", collect_exports(src), src)

    assert "mp_raise_ValueError(what);" in c
    assert 'nm_range(r0, -32768, 32767, "add(): a is out of range for int");' in c
    assert 'nm_range(r1, -32768, 32767, "add(): b is out of range for int");' in c


@pytest.mark.parametrize(
    "ann, lo, hi",
    [("uint8", 0, 255), ("int8", -128, 127), ("uint16", 0, 65535), ("int16", -32768, 32767)],
)
def test_each_width_is_checked_against_its_own_bounds(tmp_path, ann, lo, hi):
    src = write(tmp_path, f"def f(x: {ann}) -> None:\n    pass\n")
    c = generate_adapter("m", collect_exports(src), src)
    assert f"nm_range(r0, {lo}, {hi}," in c


def test_int32_is_not_checked_because_the_check_could_never_fail(tmp_path):
    """mp_int_t is 32 bits, so an int32 bound is the whole range the interpreter can
    hand over. Emitting the comparison would be dead code in every module."""
    src = write(tmp_path, "def f(x: int32) -> None:\n    pass\n")
    c = generate_adapter("m", collect_exports(src), src)
    assert "nm_range(" not in c
    assert "int32_t p0 = (int32_t)r0;" in c


def test_a_buffer_is_passed_with_its_own_length(tmp_path):
    """The positional convention this replaces was unsound, and measured so: plasma's
    count was pixels and its buffer three bytes per pixel, so a count the buffer could
    not support passed the check and wrote past the end. A length that travels WITH the
    buffer cannot disagree with it."""
    src = write(tmp_path, (
        "def brightness(buf: bytearray, scale: uint8) -> None:\n"
        "    buf[0] = scale\n"
    ))
    c = generate_adapter("m", collect_exports(src), src)

    assert "extern void brightness(uint8_t *, uint8_t, uint32_t);" in c
    assert "brightness((uint8_t *)b0.buf, p1, (uint32_t)b0.len);" in c
    # No length pairing, so no length check and no helper for one.
    assert "nm_fits" not in c


def test_every_buffer_gets_its_own_length_after_the_declared_arguments(tmp_path):
    src = write(tmp_path, (
        "def blit(dst: bytearray, src: bytes, scale: uint8) -> None:\n"
        "    dst[0] = scale\n"
    ))
    c = generate_adapter("m", collect_exports(src), src)
    assert "extern void blit(uint8_t *, const uint8_t *, uint8_t, uint32_t, uint32_t);" in c
    assert ("blit((uint8_t *)b0.buf, (const uint8_t *)b1.buf, p2, "
            "(uint32_t)b0.len, (uint32_t)b1.len);") in c


def test_a_bool_is_normalised_and_not_range_checked(tmp_path):
    src = write(tmp_path, "def f(flag: bool) -> None:\n    pass\n")
    c = generate_adapter("m", collect_exports(src), src)
    assert "mp_obj_is_true(a0)" in c
    assert "nm_range(" not in c


# ── module state ─────────────────────────────────────────────────────────────


def test_a_rebound_module_global_is_refused_by_name(tmp_path):
    src = write(tmp_path, (
        "COUNT: int = 0\n"
        "\n"
        "\n"
        "def bump() -> int:\n"
        "    global COUNT\n"
        "    COUNT = COUNT + 1\n"
        "    return COUNT\n"
    ))
    with pytest.raises(NatmodError) as e:
        check_no_module_state(src)
    msg = str(e.value)
    assert "'COUNT' is module state" in msg
    assert "keeps state only in what the caller passes in" in msg
    # Located at the `global`, not at the top of the file.
    assert msg.startswith(f"{src}:5:")


def test_writing_through_a_module_tables_subscript_is_refused(tmp_path):
    src = write(tmp_path, (
        "TABLE = [1, 2, 3, 4]\n"
        "\n"
        "\n"
        "def poke(i: int, v: int) -> None:\n"
        "    TABLE[i] = v\n"
    ))
    with pytest.raises(NatmodError) as e:
        check_no_module_state(src)
    assert "'TABLE' is module state" in str(e.value)


def test_a_read_only_module_table_is_allowed(tmp_path):
    """It becomes constant data in the module. Only WRITING needs storage that
    outlives the call."""
    src = write(tmp_path, (
        "TABLE = [1, 2, 3, 4]\n"
        "\n"
        "\n"
        "def lookup(i: int) -> int:\n"
        "    return TABLE[i]\n"
    ))
    check_no_module_state(src)   # does not raise


def test_the_interpreter_probe_ignores_the_current_directory():
    """A CircuitPython project has a `code.py` in it by definition, and `python -c` puts
    the current directory on sys.path, where it shadows the stdlib `code` that pyelftools
    pulls in. Measured on the plasma example: the probe failed with "No module named
    'board'", raised by the user's own example file. -P is what stops it."""
    import inspect

    from src.driver.commands import natmod as m

    src = inspect.getsource(m._python_for_cp_tools)
    assert '"-P", "-c", probe' in src

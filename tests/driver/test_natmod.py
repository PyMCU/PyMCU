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
        ("def f(s: str) -> int:\n    return 1\n", "take a bytes or bytearray instead"),
        ("def f(t: tuple) -> int:\n    return 1\n", "a tuple is an interpreter object"),
        ("def f(d: dict) -> int:\n    return 1\n", "a dict is an interpreter object"),
        ("def f(a) -> int:\n    return 1\n", "has no type annotation"),
        ("def f(a: int):\n    return 1\n", "has no return annotation"),
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
        "def bad(x: str) -> int:\n"
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


# ── the ARM backend is an optional plugin ────────────────────────────────────


def test_a_missing_arm_backend_is_refused_with_what_to_install(monkeypatch):
    import sys
    from src.driver.commands import natmod as natmod_cmd

    # None in sys.modules makes the import raise ImportError, as on an AVR-only setup.
    monkeypatch.setitem(sys.modules, "pymcu.toolchain.rp2040.llvm", None)
    with pytest.raises(NatmodError, match="pip install pymcu-arm"):
        natmod_cmd._arm_toolchain()


# ── errors raised by the kernel ──────────────────────────────────────────────


def _raising_adapter(tmp_path, can_raise=True):
    src = write(tmp_path, (
        "def div(a: int, b: int) -> int:\n"
        "    return a // b\n"
        "\n"
        "def poke(buf: bytearray) -> None:\n"
        "    buf[0] = 1\n"
    ))
    return generate_adapter("m", collect_exports(src), src, can_raise=can_raise)


def test_a_kernel_that_cannot_raise_gets_no_exception_machinery(tmp_path):
    c = _raising_adapter(tmp_path, can_raise=False)
    assert "pymcu_exn_take" not in c
    assert "nmx_" not in c


def test_the_adapter_asks_the_kernel_after_the_call_and_before_boxing(tmp_path):
    c = _raising_adapter(tmp_path)
    assert "extern uint32_t pymcu_exn_take(uint32_t *msg);" in c
    body = c.split("static mp_obj_t nm_div(")[1].split("}")[0]
    # the result is held, the kernel is asked, and only then is it turned into a Python int
    assert body.index("res = div(") < body.index("nmx_check();") < body.index("mp_obj_new_int(")


def test_a_void_export_is_checked_too(tmp_path):
    c = _raising_adapter(tmp_path)
    body = c.split("static mp_obj_t nm_poke(")[1].split("}")[0]
    assert body.index("poke(") < body.index("nmx_check();") < body.index("return mp_const_none;")


def test_every_code_the_compiler_raises_has_a_python_type(tmp_path):
    c = _raising_adapter(tmp_path)
    for code, name in {1: "ValueError", 6: "ZeroDivisionError", 8: "RuntimeError",
                       13: "OverflowError", 3: "IndexError", 4: "KeyError"}.items():
        assert f"case {code}: type = mp_load_global(MP_QSTR_{name});" in c
    # a class the program defined has no host name: it arrives as a RuntimeError
    assert "default: type = mp_load_global(MP_QSTR_RuntimeError);" in c


def test_a_raise_without_a_message_builds_the_exception_with_no_arguments(tmp_path):
    c = _raising_adapter(tmp_path)
    assert "if (msg == NULL)" in c
    assert "mp_call_function_n_kw(type, 0, 0, NULL)" in c

# ── what the compiler asks of `pymcu build` and a native module cannot grant ──


def test_runtime_string_names_the_line_that_builds_it(tmp_path):
    from src.driver.commands.natmod import explain_compiler_request

    src = write(tmp_path, (
        "def F(a: int) -> int:\n"
        "    x: int = a + 1\n"
        "    s: str = f'{a}'\n"
        "    return len(s)\n"
    ))
    err = explain_compiler_request(src, "strfmt")

    assert f"{src}:3:" in str(err)
    assert "f-string" in str(err) and "string builder" in str(err)


def test_an_fstring_without_a_value_is_not_the_culprit(tmp_path):
    from src.driver.commands.natmod import explain_compiler_request

    src = write(tmp_path, (
        "def F(a: int) -> int:\n"
        "    s = f'plain'\n"
        "    t = hex(a)\n"
        "    return 0\n"
    ))
    assert f"{src}:3:" in str(explain_compiler_request(src, "strfmt"))


def test_runtime_sized_bytearray_names_its_line(tmp_path):
    from src.driver.commands.natmod import explain_compiler_request

    src = write(tmp_path, (
        "def F(n: int) -> int:\n"
        "    ok = bytearray(4)\n"
        "    b = bytearray(n)\n"
        "    return 0\n"
    ))
    err = str(explain_compiler_request(src, "arena"))

    assert f"{src}:3:" in err and "allocator" in err


def test_round_with_digits_names_its_line(tmp_path):
    from src.driver.commands.natmod import explain_compiler_request

    src = write(tmp_path, "def F(a: int) -> int:\n    x: float = a\n    return int(round(x, 2))\n")

    assert f"{src}:3:" in str(explain_compiler_request(src, "round2"))


# ── floats cross as single precision ─────────────────────────────────────────


def test_float_parameter_and_return_convert(tmp_path):
    src = write(tmp_path, "def mix(x: float, n: int) -> float:\n    return x * n\n")
    (mix,) = collect_exports(src)

    assert [p.ctype for p in mix.params] == ["float", "int16_t"]
    assert mix.params[0].kind == "float"
    assert mix.ret_ctype == "float"


def test_float_adapter_unboxes_with_get_float_and_boxes_with_new_float(tmp_path):
    src = write(tmp_path, "def mix(x: float, n: int) -> float:\n    return x * n\n")
    c = generate_adapter("m", collect_exports(src), src)

    assert "extern float mix(float, int16_t);" in c
    assert "float p0 = (float)mp_obj_get_float(a0);" in c
    assert "return mp_obj_new_float(mix(p0, p1));" in c


def test_float_is_not_range_checked(tmp_path):
    """A float has no width to overflow: inf and nan are values, as in CPython."""
    src = write(tmp_path, "def f(x: float) -> float:\n    return x\n")
    c = generate_adapter("m", collect_exports(src), src)

    assert "nm_range" not in c


def test_a_double_is_refused_by_name(tmp_path):
    src = write(tmp_path, "def f(x: double) -> int:\n    return 1\n")
    with pytest.raises(NatmodError) as e:
        collect_exports(src)
    assert "annotated 'double'" in str(e.value) and "float" in str(e.value)


# ── what the command needs from the machine ──────────────────────────────────


def _install_ar_shim():
    import importlib.util

    from src.driver.commands import natmod as m
    spec = importlib.util.spec_from_file_location("natmod_ar_shim", m._AR_SHIM_DIR / "ar.py")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def _ar_member(name: str, data: bytes) -> bytes:
    header = (name.ljust(16) + "0".ljust(12) + "0".ljust(6) + "0".ljust(6)
              + "644".ljust(8) + str(len(data)).ljust(10) + "`\n").encode()
    return header + data + (b"\n" if len(data) % 2 else b"")


def test_the_bundled_ar_reader_reads_gnu_long_names_and_skips_the_symbol_table():
    import io
    shim = _install_ar_shim()
    long_name = "a_member_name_longer_than_sixteen_bytes.o"
    table = (long_name + "/\n").encode()
    blob = (b"!<arch>\n"
            + _ar_member("/", b"\0\0\0\0")                 # symbol table: skipped
            + _ar_member("//", table)                      # long-name table
            + _ar_member("short.o/", b"abc")               # odd size, padded
            + _ar_member("/0", b"hello world!"))           # long name, by offset
    archive = shim.Archive(io.BytesIO(blob))
    assert [e.name for e in archive] == ["short.o", long_name]
    assert archive.open("short.o", "rb").read() == b"abc"
    assert archive.open(long_name, "rb").read() == b"hello world!"


def test_the_bundled_ar_reader_refuses_something_that_is_not_an_archive():
    import io
    shim = _install_ar_shim()
    with pytest.raises(ValueError):
        shim.Archive(io.BytesIO(b"not an archive at all"))


def test_the_bundled_ar_reader_agrees_with_the_real_package_on_libgcc():
    import shutil
    import subprocess

    ar_pkg = pytest.importorskip("ar")
    gcc = shutil.which("arm-none-eabi-gcc")
    if not gcc:
        pytest.skip("no arm-none-eabi-gcc to ask for libgcc")
    libgcc = subprocess.run([gcc, "-mthumb", "-mcpu=cortex-m0", "--print-libgcc-file-name"],
                            capture_output=True, text=True).stdout.strip()
    shim = _install_ar_shim()
    mine = shim.Archive(open(libgcc, "rb"))
    real = ar_pkg.Archive(open(libgcc, "rb"))
    assert [e.name for e in mine] == [e.name for e in real]
    assert all(mine.open(e.name, "rb").read() == real.open(e.name, "rb").read() for e in mine)


def test_the_reader_is_only_put_on_the_path_when_the_interpreter_has_no_ar(monkeypatch):
    import subprocess

    from src.driver.commands import natmod as m
    monkeypatch.delenv("PYTHONPATH", raising=False)

    class R:
        def __init__(self, rc):
            self.returncode = rc

    monkeypatch.setattr(subprocess, "run", lambda *a, **k: R(1))
    assert str(m._AR_SHIM_DIR) in m._ar_environment("python").get("PYTHONPATH", "")
    monkeypatch.setattr(subprocess, "run", lambda *a, **k: R(0))
    assert "PYTHONPATH" not in m._ar_environment("python")


def test_missing_requirements_are_reported_together_one_line_each(monkeypatch, tmp_path):
    from src.driver.commands import natmod as m
    monkeypatch.delenv("PYMCU_CIRCUITPYTHON", raising=False)
    monkeypatch.delenv("PYMCU_NATMOD_CC", raising=False)
    monkeypatch.setattr(m.shutil, "which", lambda name: None)
    monkeypatch.setattr(m.subprocess, "run",
                        lambda *a, **k: type("R", (), {"returncode": 1})())

    with pytest.raises(NatmodError) as e:
        m.natmod_requirements(None, {}, tmp_path)

    lines = str(e.value).splitlines()
    assert lines[0].startswith("cannot build, ")
    items = [ln for ln in lines[1:] if ln.startswith("  - ")]
    assert len(items) == len(lines) - 1          # nothing wraps onto a second line
    text = "\n".join(items)
    assert "--circuitpython" in text             # the tree
    assert "arm-none-eabi-gcc" in text and "brew install" in text
    assert "pymcu-compiler[natmod]" in text      # pyelftools, with the extra that declares it


def test_a_directory_without_the_runtime_header_is_not_a_circuitpython_tree(tmp_path):
    from src.driver.commands import natmod as m
    with pytest.raises(NatmodError, match="not a CircuitPython source tree"):
        m._resolve_cp_tree(str(tmp_path), {}, tmp_path)


# ── returning a tuple of numbers ─────────────────────────────────────────────

MINMAX = (
    "def minmax(b: bytes, n: int) -> tuple[int, int]:\n"
    "    return (0, n)\n"
)


def test_a_tuple_return_converts_to_its_element_types(tmp_path):
    (e,) = collect_exports(write(tmp_path, MINMAX))

    assert e.ret_tuple == ["int", "int"]
    assert e.ret_ctype is None
    assert e.symbol == "minmax__nm"


def test_a_tuple_export_gets_a_kernel_wrapper_that_stores_little_endian(tmp_path):
    from src.driver.commands.natmod import tuple_kernel_source

    (e,) = collect_exports(write(tmp_path, MINMAX))
    src = tuple_kernel_source(e)

    assert "def minmax__nm(b: bytes, n: int, nm_out: bytearray) -> None:" in src
    assert "nm_r0, nm_r1 = minmax(b, n)" in src
    assert "nm_out[0] = ((nm_r0) & 255)" in src
    assert "nm_out[1] = ((nm_r0 >> 8) & 255)" in src
    assert "nm_out[3] = ((nm_r1 >> 8) & 255)" in src


def test_a_float_element_is_stored_as_its_bits(tmp_path):
    from src.driver.commands.natmod import tuple_kernel_source

    (e,) = collect_exports(write(tmp_path, "def f(x: float) -> tuple[float, int8]:\n    return (x, 1)\n"))
    src = tuple_kernel_source(e)

    assert "nm_bits0: uint32 = bitcast(uint32, nm_r0)" in src
    assert "nm_out[4] = ((nm_r1) & 255)" in src


def test_the_adapter_builds_the_tuple_from_the_buffer_the_kernel_filled(tmp_path):
    src = write(tmp_path, MINMAX)
    c = generate_adapter("m", collect_exports(src), src)

    # The kernel's symbol takes the out buffer, then a length for the input buffer and one for it.
    assert "extern void minmax__nm(const uint8_t *, int16_t, uint8_t *, uint32_t, uint32_t);" in c
    assert "uint8_t nm_out[4];" in c
    assert "minmax__nm((const uint8_t *)b0.buf, p1, nm_out, (uint32_t)b0.len, (uint32_t)4);" in c
    assert "items[0] = mp_obj_new_int((mp_int_t)(int16_t)(" in c
    assert "return mp_obj_new_tuple(2, items);" in c
    assert "MP_QSTR_minmax," in c   # the Python name is the user's, not the kernel's


def test_unsigned_bool_and_float_elements_are_boxed_by_their_type(tmp_path):
    src = write(tmp_path, "def f() -> tuple[uint8, bool, float, uint32]:\n    return (1, True, 1.0, 2)\n")
    c = generate_adapter("m", collect_exports(src), src)

    assert "mp_obj_new_int_from_uint" in c
    assert "mp_obj_new_bool(" in c
    assert "mp_obj_new_float(cv.f)" in c
    assert "uint8_t nm_out[10];" in c


@pytest.mark.parametrize("ret, expected", [
    ("tuple[int, ...]", "unknown length"),
    ("tuple[bytearray]", "an element annotated 'bytearray'"),
    ("tuple[int, tuple[int, int]]", "an element annotated 'tuple[int, int]'"),
    ("tuple[str, int]", "an element annotated 'str'"),
    ("tuple[" + ", ".join(["int"] * 17) + "]", "from 1 to 16"),
])
def test_a_tuple_return_that_cannot_be_written_is_refused_at_its_def(tmp_path, ret, expected):
    src = write(tmp_path, f"def f() -> {ret}:\n    return 0\n")
    with pytest.raises(NatmodError) as e:
        collect_exports(src)
    assert expected in str(e.value) and str(e.value).startswith(f"{src}:1:")


def test_a_tuple_parameter_is_still_refused(tmp_path):
    with pytest.raises(NatmodError) as e:
        collect_exports(write(tmp_path, "def f(t: tuple[int, int]) -> int:\n    return 0\n"))
    assert "annotated 'tuple[int, int]'" in str(e.value)


def test_a_one_element_tuple_is_read_by_index_not_by_unpacking(tmp_path):
    from src.driver.commands.natmod import tuple_kernel_source

    (e,) = collect_exports(write(tmp_path, "def f(a: int) -> tuple[int]:\n    return (a,)\n"))

    assert "nm_r0 = f(a)[0]" in tuple_kernel_source(e)

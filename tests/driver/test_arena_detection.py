# tests/driver/test_arena_detection.py
#
# docs/rfcs/0004-arena-allocator.md. Two layers: the pure sizing heuristic
# (_fold_int_expr / _detect_and_size_arena_usage), fast and exhaustive on its own, and one
# end-to-end `pymcu build` check that the injected import, generated shim and printed build
# line all show up together for a real (fake-compiler-backed) build. The compiler's own
# once-rule enforcement and IR shape are covered in tests/unit/IR/ArenaAllocatorTests.cs --
# nothing here re-tests that; this file is about what the DRIVER does before the compiler
# ever sees the sources.

from pathlib import Path

from src.driver.commands.build import _fold_int_expr, _detect_and_size_arena_usage
from typer.testing import CliRunner
from src.driver.main import app

runner = CliRunner()


def _invoke_build(*args: str):
    return runner.invoke(app, ["build"] + list(args), catch_exceptions=False)


# ---------------------------------------------------------------------------
# _fold_int_expr: what folds and what does not.
# ---------------------------------------------------------------------------

class TestFoldIntExpr:
    def test_plain_literal_folds(self):
        assert _fold_int_expr("64") == 64

    def test_addition_of_literals_folds(self):
        assert _fold_int_expr("4 + 60") == 64

    def test_multiplication_of_literals_folds(self):
        assert _fold_int_expr("4 * 16") == 64

    def test_mixed_add_and_multiply_folds(self):
        assert _fold_int_expr("4 * 16 + 1") == 65

    def test_a_name_does_not_fold(self):
        # The whole point: a genuinely runtime size is not a heuristic gap, it is what
        # routes the allocation to the arena in the first place.
        assert _fold_int_expr("n") is None

    def test_a_call_does_not_fold(self):
        assert _fold_int_expr("read_len()") is None

    def test_subtraction_does_not_fold(self):
        # Deliberately narrower than the compiler's own folding (RFC "Sizing"): under-
        # counting here only widens the reservation, never undersizes one the compiler
        # accepts.
        assert _fold_int_expr("68 - 4") is None

    def test_boolean_literal_does_not_count_as_an_int(self):
        # `bool` is `int` in Python's own ast, and True/False are not a buffer size.
        assert _fold_int_expr("True") is None


# ---------------------------------------------------------------------------
# _detect_and_size_arena_usage: the whole-project scan.
# ---------------------------------------------------------------------------

class TestDetectAndSizeArenaUsage:
    def test_no_bytearray_calls_at_all(self, tmp_path: Path):
        (tmp_path / "main.py").write_text("x: int = 1\n")
        used, reserved, exact = _detect_and_size_arena_usage(tmp_path, None)
        assert (used, reserved, exact) == (False, 0, True)

    def test_only_constant_sized_calls_still_fold_exactly(self, tmp_path: Path):
        # Over-inclusive by design (matches _detect_fstring_value_usage's own doc comment):
        # a compile-time-constant bytearray(N) is caught by the regex too, but it folds
        # cleanly, so it costs nothing -- see ConstantSizedBytearrayIsUnaffected in the C#
        # suite for the half of this invariant that lives in the compiler.
        (tmp_path / "main.py").write_text("buf: bytearray = bytearray(20)\n")
        used, reserved, exact = _detect_and_size_arena_usage(tmp_path, None)
        assert (used, reserved, exact) == (True, 20, True)

    def test_one_runtime_sized_call_falls_back_to_the_board_default(self, tmp_path: Path):
        (tmp_path / "main.py").write_text(
            "n: int = 5\nbuf: bytearray = bytearray(n)\n")
        used, reserved, exact = _detect_and_size_arena_usage(tmp_path, None)
        assert used is True
        assert exact is False
        assert reserved == 256  # _ARENA_BOARD_DEFAULT_BYTES

    def test_explicit_override_wins_over_both_fold_and_default(self, tmp_path: Path):
        (tmp_path / "main.py").write_text(
            "n: int = 5\nbuf: bytearray = bytearray(n)\n")
        used, reserved, exact = _detect_and_size_arena_usage(tmp_path, 512)
        assert (used, reserved, exact) == (True, 512, False)

    def test_sums_across_multiple_files_and_calls(self, tmp_path: Path):
        (tmp_path / "a.py").write_text("x: bytearray = bytearray(10)\n")
        (tmp_path / "b.py").write_text(
            "y: bytearray = bytearray(6)\nz: bytearray = bytearray(4)\n")
        used, reserved, exact = _detect_and_size_arena_usage(tmp_path, None)
        assert (used, reserved, exact) == (True, 20, True)

    def test_a_single_non_folding_call_among_several_trips_the_fallback(self, tmp_path: Path):
        # One genuinely runtime call is enough to give up on the exact sum entirely, even
        # alongside calls that would have folded -- the board default has to cover ALL of
        # them, not just the ones this heuristic could not size.
        (tmp_path / "a.py").write_text("x: bytearray = bytearray(10)\n")
        (tmp_path / "b.py").write_text("n: int = 5\ny: bytearray = bytearray(n)\n")
        used, reserved, exact = _detect_and_size_arena_usage(tmp_path, None)
        assert (used, reserved, exact) == (True, 256, False)

    def test_commented_out_call_is_ignored(self, tmp_path: Path):
        (tmp_path / "main.py").write_text("# buf = bytearray(20)\nx: int = 1\n")
        used, _, _ = _detect_and_size_arena_usage(tmp_path, None)
        assert used is False


# ---------------------------------------------------------------------------
# Literal buffer forms: bytearray([...]), bytearray((...)), bytearray(b"..."),
# bytearray("...") are laid out statically by the compiler and never allocate
# from the arena, so they must not mark the program an arena user.
# ---------------------------------------------------------------------------

class TestLiteralBufferArgumentsAreNotArenaUsage:
    def test_a_list_literal_is_a_compile_time_buffer(self, tmp_path: Path):
        (tmp_path / "main.py").write_text("buf: bytearray = bytearray([0x15, 0x2A])\n")
        used, reserved, exact = _detect_and_size_arena_usage(tmp_path, None)
        assert (used, reserved, exact) == (False, 0, True)

    def test_a_bytes_literal_is_a_compile_time_buffer(self, tmp_path: Path):
        (tmp_path / "main.py").write_text('buf: bytearray = bytearray(b"\\x15\\x2a")\n')
        used, reserved, exact = _detect_and_size_arena_usage(tmp_path, None)
        assert (used, reserved, exact) == (False, 0, True)

    def test_a_str_literal_is_a_compile_time_buffer(self, tmp_path: Path):
        (tmp_path / "main.py").write_text('buf: bytearray = bytearray("abc")\n')
        used, reserved, exact = _detect_and_size_arena_usage(tmp_path, None)
        assert (used, reserved, exact) == (False, 0, True)

    def test_a_tuple_literal_is_a_compile_time_buffer(self, tmp_path: Path):
        (tmp_path / "main.py").write_text("buf: bytearray = bytearray((1, 2))\n")
        used, reserved, exact = _detect_and_size_arena_usage(tmp_path, None)
        assert (used, reserved, exact) == (False, 0, True)

    def test_an_int_size_still_counts_exactly(self, tmp_path: Path):
        (tmp_path / "main.py").write_text("buf: bytearray = bytearray(16)\n")
        used, reserved, exact = _detect_and_size_arena_usage(tmp_path, None)
        assert (used, reserved, exact) == (True, 16, True)

    def test_a_name_still_falls_back_to_the_board_default(self, tmp_path: Path):
        (tmp_path / "main.py").write_text(
            "n: int = 5\nbuf: bytearray = bytearray(n)\n")
        used, reserved, exact = _detect_and_size_arena_usage(tmp_path, None)
        assert (used, reserved, exact) == (True, 256, False)

    def test_a_literal_and_a_sized_call_in_one_file(self, tmp_path: Path):
        # The literal contributes nothing; the sized call folds exactly, so the
        # reservation is its size alone.
        (tmp_path / "main.py").write_text(
            "a: bytearray = bytearray([1, 2])\nb: bytearray = bytearray(16)\n")
        used, reserved, exact = _detect_and_size_arena_usage(tmp_path, None)
        assert (used, reserved, exact) == (True, 16, True)

    def test_an_argument_truncated_at_an_inner_paren_lands_safe(self, tmp_path: Path):
        # The capture regex stops at the first ')' unless the argument starts
        # with a balanced group, so bytearray([f(1), 2]) arrives as '[f(1' and
        # fails ast.parse. A list with a call inside is not a compile-time
        # buffer, and an unparseable argument stays unknown: used, not exact,
        # board default -- the safe side.
        (tmp_path / "main.py").write_text(
            "buf: bytearray = bytearray([f(1), 2])\n")
        used, reserved, exact = _detect_and_size_arena_usage(tmp_path, None)
        assert (used, reserved, exact) == (True, 256, False)


# ---------------------------------------------------------------------------
# End-to-end: `pymcu build` injects the import, generates the shim and prints the line.
# ---------------------------------------------------------------------------

def _project(tmp_path: Path, main_body: str, extra_keys: str = "") -> None:
    (tmp_path / "src").mkdir(exist_ok=True)
    (tmp_path / "src" / "main.py").write_text(main_body)
    (tmp_path / "pyproject.toml").write_text(
        "[tool.pymcu]\n"
        'target = "atmega328p"\n'
        "frequency = 16000000\n"
        'sources = "src"\n'
        'entry = "main.py"\n'
        + extra_keys
    )


class TestArenaBuildIntegration:
    def test_no_bytearray_call_at_all_prints_no_arena_line(
            self, tmp_path, monkeypatch, mock_toolchain, mock_compiler):
        # Not `bytearray(8)` -- the detector is over-inclusive by design (see
        # test_only_constant_sized_calls_still_fold_exactly above) and DOES print a line
        # for a compile-time-constant size, correctly, since folding it costs nothing. The
        # zero-cost gate this is actually testing is a program with no bytearray(...) call
        # anywhere: nothing scanned, nothing generated, nothing printed.
        monkeypatch.chdir(tmp_path)
        _project(tmp_path, "x: int = 1\n")
        result = _invoke_build()
        assert "Arena:" not in result.output
        assert not (tmp_path / "dist" / "_generated" / "pymcu" / "arena.py").exists()

    def test_runtime_bytearray_injects_import_shim_and_prints_the_line(
            self, tmp_path, monkeypatch, mock_toolchain, mock_compiler, unwrapped):
        monkeypatch.chdir(tmp_path)
        _project(tmp_path, "n: int = 5\nbuf: bytearray = bytearray(n)\n")
        result = _invoke_build()

        assert "Arena: reserved 256 B" in unwrapped(result.output)

        generated_entry = tmp_path / "dist" / "_generated" / "main.py"
        assert generated_entry.exists()
        assert "import pymcu.arena as _pymcu_arena" in generated_entry.read_text()

        shim = tmp_path / "dist" / "_generated" / "pymcu" / "arena.py"
        assert shim.exists()
        assert "ARENA_SIZE: uint16 = 256" in shim.read_text()

    def test_arena_size_override_is_reported_and_used(
            self, tmp_path, monkeypatch, mock_toolchain, mock_compiler, unwrapped):
        monkeypatch.chdir(tmp_path)
        _project(tmp_path, "n: int = 5\nbuf: bytearray = bytearray(n)\n",
                 extra_keys="arena_size = 400\n")
        result = _invoke_build()
        assert "Arena: reserved 400 B" in unwrapped(result.output)
        shim = tmp_path / "dist" / "_generated" / "pymcu" / "arena.py"
        assert "ARENA_SIZE: uint16 = 400" in shim.read_text()

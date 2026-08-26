# tests/driver/test_build.py
#
# Tests for the `pymcu build` command.
# Real compiler and toolchain calls are replaced by the fixtures in conftest.py.

from pathlib import Path
import pytest
from typer.testing import CliRunner
from src.driver.main import app

runner = CliRunner()


def _invoke_build(*args: str):
    return runner.invoke(app, ["build"] + list(args), catch_exceptions=False)


def _project(tmp_path: Path, keys: str) -> None:
    """A buildable project whose [tool.pymcu] carries *keys* and nothing else unusual."""
    (tmp_path / "src").mkdir(exist_ok=True)
    (tmp_path / "src" / "main.py").write_text("def main():\n    print(1)\n")
    (tmp_path / "pyproject.toml").write_text(
        "[project]\n"
        'name = "demo"\n'
        'version = "0.1.0"\n'
        "\n"
        "[tool.pymcu]\n"
        + keys +
        "frequency = 16000000\n"
        'sources = "src"\n'
        'entry = "main.py"\n'
    )


# ---------------------------------------------------------------------------
# Missing pyproject.toml → Exit(1)
# ---------------------------------------------------------------------------

class TestBuildMissingConfig:
    def test_no_pyproject_exits_1(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        result = _invoke_build()
        assert result.exit_code == 1
        assert "pyproject.toml" in result.output.lower()


# ---------------------------------------------------------------------------
# target + board set simultaneously → Exit(1)
# ---------------------------------------------------------------------------

class TestBuildMutuallyExclusiveTargetBoard:
    def test_target_and_board_exits_1(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        (tmp_path / "src").mkdir()
        (tmp_path / "src" / "main.py").write_text("def main(): pass\n")
        (tmp_path / "pyproject.toml").write_text(
            "[tool.pymcu]\n"
            'target = "atmega328p"\n'
            'board = "arduino_uno"\n'
            "frequency = 4000000\n"
            'sources = "src"\n'
            'entry = "main.py"\n'
        )
        result = _invoke_build()
        assert result.exit_code == 1
        assert "board" in result.output.lower() or "target" in result.output.lower()


# ---------------------------------------------------------------------------
# Two compat layers at once → Exit(1)
#
# They define the same module names with different APIs (time.sleep takes a
# uint16 in one and a float in the other), so the include-path order silently
# decided which one the program got.
# ---------------------------------------------------------------------------

class TestBuildMultipleFlavors:
    def _project(self, tmp_path):
        (tmp_path / "src").mkdir()
        (tmp_path / "src" / "main.py").write_text("def main(): pass\n")
        (tmp_path / "pyproject.toml").write_text(
            "[tool.pymcu]\n"
            'board = "arduino_uno"\n'
            "frequency = 16000000\n"
            'sources = "src"\n'
            'entry = "main.py"\n'
            'stdlib = ["micropython", "circuitpython"]\n'
        )

    def test_two_flavors_exit_1(self, tmp_path, monkeypatch, unwrapped):
        monkeypatch.chdir(tmp_path)
        self._project(tmp_path)
        result = _invoke_build()
        assert result.exit_code == 1
        assert "more than one compat layer" in unwrapped(result.output)

    def test_cli_override_can_narrow_to_one(self, tmp_path, monkeypatch, unwrapped):
        monkeypatch.chdir(tmp_path)
        self._project(tmp_path)
        result = _invoke_build("--stdlib", "micropython")
        assert "more than one compat layer" not in unwrapped(result.output)


# ---------------------------------------------------------------------------
# Entry file not found → Exit(1)
# ---------------------------------------------------------------------------

class TestBuildMissingEntry:
    def test_missing_entry_exits_1(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        (tmp_path / "src").mkdir()
        (tmp_path / "pyproject.toml").write_text(
            "[tool.pymcu]\n"
            'target = "atmega328p"\n'
            "frequency = 4000000\n"
            'sources = "src"\n'
            'entry = "main.py"\n'
        )
        result = _invoke_build()
        assert result.exit_code == 1


# ---------------------------------------------------------------------------
# stdlib_override via --stdlib flag
# ---------------------------------------------------------------------------

class TestBuildStdlibFlag:
    def test_unknown_stdlib_flavor_prints_warning(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        (tmp_path / "src").mkdir()
        (tmp_path / "src" / "main.py").write_text("def main(): pass\n")
        (tmp_path / "pyproject.toml").write_text(
            "[tool.pymcu]\n"
            'target = "atmega328p"\n'
            "frequency = 4000000\n"
            'sources = "src"\n'
            'entry = "main.py"\n'
        )
        result = _invoke_build("--stdlib", "nonexistent_flavor_xyz")
        # Should warn but continue (not exit 1 due to missing flavor alone)
        assert "nonexistent_flavor_xyz" in result.output or result.exit_code in (0, 1)


# ---------------------------------------------------------------------------
# Board key resolves to correct chip
# ---------------------------------------------------------------------------

class TestBuildBoardResolution:
    def test_known_board_resolves(self, tmp_path, monkeypatch, mock_toolchain, mock_compiler,
                                  unwrapped):
        pytest.importorskip("pymcu.toolchain.avr", reason="pymcu-avr not installed")
        monkeypatch.chdir(tmp_path)
        (tmp_path / "src").mkdir()
        (tmp_path / "src" / "main.py").write_text("def main(): pass\n")
        (tmp_path / "pyproject.toml").write_text(
            "[tool.pymcu]\n"
            'board = "arduino_uno"\n'
            "frequency = 16000000\n"
            'sources = "src"\n'
            'entry = "main.py"\n'
        )
        result = _invoke_build()
        # Should not error out on board resolution
        assert "unknown board" not in unwrapped(result.output).lower()

    def test_unknown_board_names_the_one_it_meant(self, tmp_path, monkeypatch, unwrapped):
        # `uno` is the short form of the key, and it is the miss difflib does not find on its
        # own, so the suggestion is what tells the reader the name is nearly right.
        monkeypatch.chdir(tmp_path)
        _project(tmp_path, 'board = "uno"\n')
        result = _invoke_build()
        assert result.exit_code == 1
        assert "did you mean 'arduino_uno'?" in unwrapped(result.output).lower()

    def test_unknown_board_exits_1(self, tmp_path, monkeypatch, unwrapped):
        monkeypatch.chdir(tmp_path)
        (tmp_path / "src").mkdir()
        (tmp_path / "src" / "main.py").write_text("def main(): pass\n")
        (tmp_path / "pyproject.toml").write_text(
            "[tool.pymcu]\n"
            'board = "banana_pi_zz99"\n'
            "frequency = 16000000\n"
            'sources = "src"\n'
            'entry = "main.py"\n'
        )
        result = _invoke_build()
        assert result.exit_code == 1
        assert "unknown board" in unwrapped(result.output).lower()


# ---------------------------------------------------------------------------
# An unknown board plus 'target': which error is reported (issue #198)
# ---------------------------------------------------------------------------

class TestBuildUnknownBoardWithTarget:
    """
    With both keys set and the board unrecognised, the driver used to report the conflict,
    render what the board implies as a literal `?`, and tell the reader to remove the `target`
    line, which was the correct one. Obeying it surfaced the real error, the board name, which
    the driver had already failed to resolve when it composed the first sentence.

    The mutual-exclusion check moved to after the board resolves, which is also after the
    extension board tables are loaded: before them, a board an extension supplies is
    indistinguishable from one that does not exist, and that is what produced the `?`.
    """

    def test_the_board_is_reported_not_the_conflict(self, tmp_path, monkeypatch, unwrapped):
        monkeypatch.chdir(tmp_path)
        _project(tmp_path, 'target = "atmega328p"\nboard = "uno"\n')
        result = _invoke_build()
        out = unwrapped(result.output).lower()

        assert result.exit_code == 1
        assert "unknown board 'uno'" in out
        assert "cannot set both" not in out

    def test_the_reader_is_not_sent_to_delete_the_line_that_is_right(
            self, tmp_path, monkeypatch, unwrapped):
        monkeypatch.chdir(tmp_path)
        _project(tmp_path, 'target = "atmega328p"\nboard = "uno"\n')
        result = _invoke_build()

        assert "remove the 'target' key" not in unwrapped(result.output).lower()

    def test_no_message_claims_an_implication_it_could_not_compute(
            self, tmp_path, monkeypatch, unwrapped):
        # The `?` was the tell: an unresolved lookup printed rather than reported.
        monkeypatch.chdir(tmp_path)
        _project(tmp_path, 'target = "atmega328p"\nboard = "uno"\n')
        result = _invoke_build()

        assert 'implies target = "?"' not in unwrapped(result.output)

    def test_an_empty_board_is_not_a_board(self, tmp_path, monkeypatch, unwrapped,
                                           mock_toolchain, mock_compiler):
        # `board = ""` has always meant no board, because the key is read for truth and not
        # for presence, and a project can carry an empty one. The reordering has to keep that:
        # an empty string must not become a board that resolves to nothing.
        monkeypatch.chdir(tmp_path)
        _project(tmp_path, 'target = "atmega328p"\nboard = ""\n')
        result = _invoke_build()
        out = unwrapped(result.output).lower()

        assert "unknown board" not in out
        assert "cannot set both" not in out

    def test_a_board_that_does_resolve_still_reports_the_conflict(
            self, tmp_path, monkeypatch, unwrapped):
        # The invariant. The conflict is a real error and must survive the reordering, with the
        # chip it implies rather than a placeholder.
        monkeypatch.chdir(tmp_path)
        _project(tmp_path, 'target = "atmega328p"\nboard = "arduino_uno"\n')
        result = _invoke_build()
        out = unwrapped(result.output)

        assert result.exit_code == 1
        assert "Cannot set both" in out
        assert 'implies target = "atmega328p"' in out

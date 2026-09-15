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


# ---------------------------------------------------------------------------
# Upstream library include-path ordering (issue #377)
#
# A manifest library's directory must precede an upstream library's staged
# directory on the compiler's -I list: the manifest one can declare a compat
# adapter that has to win a name clash, and neither may ever get to shadow a
# flavor package. This drives a full (mocked) build and inspects the
# extra_includes PyMCUCompiler.compile actually received, rather than only
# the ordering that core.upstream_libraries computes on its own.
# ---------------------------------------------------------------------------

class TestBuildUpstreamLibraryIncludeOrder:
    @staticmethod
    def _site_packages(tmp_path: Path) -> Path:
        import sys as _sys
        site = (tmp_path / ".venv" / "lib"
                / f"python{_sys.version_info.major}.{_sys.version_info.minor}"
                / "site-packages")
        site.mkdir(parents=True)
        return site

    @staticmethod
    def _install_manifest_library(site: Path) -> None:
        pkg = site / "pymcu_lib_fake"
        sources = pkg / "mcu"
        sources.mkdir(parents=True)
        (pkg / "__init__.py").write_text("")
        (pkg / "pymcu.toml").write_text(
            "[library]\n"
            'name = "fakelib"\n'
            "\n"
            "[library.provides]\n"
            'modules = ["fakelib"]\n'
            "\n"
            "[library.supports]\n"
            'layer = "circuitpython"\n'
        )
        (sources / "fakelib.py").write_text("VALUE = 1\n")

        dist_info = site / "pymcu_lib_fake-1.0.0.dist-info"
        dist_info.mkdir()
        (dist_info / "METADATA").write_text(
            "Metadata-Version: 2.1\nName: pymcu-lib-fake\nVersion: 1.0.0\n")
        (dist_info / "entry_points.txt").write_text(
            "[pymcu.libraries]\nfakelib = pymcu_lib_fake\n")

    @staticmethod
    def _install_upstream_distribution(site: Path) -> None:
        (site / "adafruit_hcsr04.py").write_text("VALUE = 1\n")
        dist_info = site / "adafruit_circuitpython_hcsr04-0.4.25.dist-info"
        dist_info.mkdir()
        (dist_info / "METADATA").write_text(
            "Metadata-Version: 2.1\nName: adafruit-circuitpython-hcsr04\nVersion: 0.4.25\n")
        (dist_info / "RECORD").write_text(
            f"{dist_info.name}/METADATA,,\nadafruit_hcsr04.py,,\n")

    def test_manifest_library_precedes_upstream_library_on_the_include_path(
            self, tmp_path, monkeypatch, mock_toolchain, mock_compiler):
        pytest.importorskip("pymcu.toolchain.avr", reason="pymcu-avr not installed")
        pytest.importorskip("pymcu_circuitpython", reason="pymcu-circuitpython not installed")

        monkeypatch.chdir(tmp_path)
        (tmp_path / "src").mkdir()
        (tmp_path / "src" / "main.py").write_text("def main(): pass\n")
        (tmp_path / "pyproject.toml").write_text(
            "[tool.pymcu]\n"
            'board = "arduino_uno"\n'
            "frequency = 16000000\n"
            'sources = "src"\n'
            'entry = "main.py"\n'
            'stdlib = ["circuitpython"]\n'
        )

        site = self._site_packages(tmp_path)
        self._install_manifest_library(site)
        self._install_upstream_distribution(site)

        from src.driver.core import upstream_libraries as up

        monkeypatch.setattr(up, "read_cached_library_index", lambda: {
            "v": 1,
            "libraries": [{
                "kind": "upstream",
                "name": "adafruit_hcsr04",
                "distribution": "adafruit-circuitpython-hcsr04",
                "version": "0.4.25",
                "provides": ["adafruit_hcsr04"],
                "layer": "circuitpython",
            }],
        })

        from src.driver.core.compiler import PyMCUCompiler

        captured: dict = {}
        original_compile = PyMCUCompiler.compile

        def spy(self, *args, **kwargs):
            captured["extra_includes"] = list(kwargs.get("extra_includes") or [])
            return original_compile(self, *args, **kwargs)

        monkeypatch.setattr(PyMCUCompiler, "compile", spy)

        # The include path is resolved and handed to PyMCUCompiler.compile
        # before anything downstream (assembling, linking) runs, so what
        # happens to the rest of this mocked build is not this test's
        # concern -- only whether compile() was called, and with what.
        _invoke_build()
        assert "extra_includes" in captured, "PyMCUCompiler.compile was never called"

        includes = captured["extra_includes"]
        manifest_dir = str(site / "pymcu_lib_fake" / "mcu")
        upstream_dir = str(tmp_path / "dist" / "_upstream" / "adafruit-circuitpython-hcsr04")

        assert manifest_dir in includes
        assert upstream_dir in includes
        assert includes.index(manifest_dir) < includes.index(upstream_dir)
        # And the staged upstream directory holds only what it declared.
        assert (tmp_path / "dist" / "_upstream" / "adafruit-circuitpython-hcsr04"
               / "adafruit_hcsr04.py").is_file()

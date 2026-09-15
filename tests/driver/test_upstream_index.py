# Tests for the upstream half of `pymcu index build`: parsing libraries.txt's
# `upstream ...` line form, and measuring a submission's committed example.

import json
from pathlib import Path

import pytest

from src.driver.core import library_index as idx
from src.driver.core import upstream_index as uidx


class TestRepresentativeBoard:
    def test_circuitpython_avr_gets_arduino_uno(self):
        assert uidx._representative_board("circuitpython", "atmega328p") == "arduino_uno"

    def test_circuitpython_arm_gets_raspberry_pi_pico(self):
        assert uidx._representative_board("circuitpython", "rp2040") == "raspberry_pi_pico"

    def test_native_layer_has_no_board(self):
        assert uidx._representative_board("native", "atmega328p") is None

    def test_a_layer_arch_combination_with_no_board_falls_back_to_none(self):
        # There is no CircuitPython board for PIC in this project today.
        assert uidx._representative_board("circuitpython", "pic16f877a") is None


class TestParseLibrariesFile:
    def test_a_bare_name_is_a_manifest_distribution(self, tmp_path):
        f = tmp_path / "libraries.txt"
        f.write_text("pymcu-lib-dht\n")
        manifest, upstream, problems = idx.read_libraries_file(f)
        assert manifest == ["pymcu-lib-dht"]
        assert upstream == []
        assert problems == []

    def test_an_upstream_line_is_parsed(self, tmp_path):
        f = tmp_path / "libraries.txt"
        f.write_text(
            "upstream adafruit-circuitpython-hcsr04 "
            "provides=adafruit_hcsr04 layer=circuitpython "
            "example=upstream-examples/adafruit-circuitpython-hcsr04/hcsr04_simpletest.py\n"
        )
        manifest, upstream, problems = idx.read_libraries_file(f)
        assert problems == []
        assert manifest == []
        assert len(upstream) == 1
        sub = upstream[0]
        assert sub.distribution == "adafruit-circuitpython-hcsr04"
        assert sub.provides == ("adafruit_hcsr04",)
        assert sub.layer == "circuitpython"
        assert sub.example == (
            "upstream-examples/adafruit-circuitpython-hcsr04/hcsr04_simpletest.py"
        )

    def test_multiple_provides_are_comma_separated(self, tmp_path):
        f = tmp_path / "libraries.txt"
        f.write_text(
            "upstream some-dist provides=a,b,c layer=native example=x/y.py\n"
        )
        _, upstream, problems = idx.read_libraries_file(f)
        assert problems == []
        assert upstream[0].provides == ("a", "b", "c")

    def test_layer_defaults_to_native(self, tmp_path):
        f = tmp_path / "libraries.txt"
        f.write_text("upstream some-dist provides=a example=x/y.py\n")
        _, upstream, problems = idx.read_libraries_file(f)
        assert problems == []
        assert upstream[0].layer == "native"

    def test_comments_and_blank_lines_are_ignored(self, tmp_path):
        f = tmp_path / "libraries.txt"
        f.write_text("# a comment\n\npymcu-lib-dht\n   \n# another\n")
        manifest, upstream, problems = idx.read_libraries_file(f)
        assert manifest == ["pymcu-lib-dht"]
        assert upstream == problems == []

    def test_an_optional_name_overrides_the_default(self, tmp_path):
        f = tmp_path / "libraries.txt"
        f.write_text(
            "upstream adafruit-circuitpython-hcsr04 name=hcsr04 "
            "provides=adafruit_hcsr04 example=x/y.py\n"
        )
        _, upstream, problems = idx.read_libraries_file(f)
        assert problems == []
        assert upstream[0].name == "hcsr04"

    @pytest.mark.parametrize("line,expected_fragment", [
        ("upstream", "needs a distribution name"),
        ("upstream some-dist", "needs provides="),
        ("upstream some-dist provides=a", "needs example="),
        ("upstream some-dist provides=a example=x.py layer=fortran", "unknown layer"),
        ("upstream some-dist provides=a example=x.py junk", "unrecognized token"),
    ])
    def test_malformed_lines_are_reported_not_raised(self, tmp_path, line, expected_fragment):
        f = tmp_path / "libraries.txt"
        f.write_text(line + "\n")
        manifest, upstream, problems = idx.read_libraries_file(f)
        assert manifest == []
        assert upstream == []
        assert len(problems) == 1
        assert expected_fragment in problems[0]

    def test_one_bad_line_does_not_stop_the_rest_of_the_file(self, tmp_path):
        f = tmp_path / "libraries.txt"
        f.write_text("upstream\npymcu-lib-dht\nupstream ok provides=a example=x.py\n")
        manifest, upstream, problems = idx.read_libraries_file(f)
        assert manifest == ["pymcu-lib-dht"]
        assert len(upstream) == 1
        assert len(problems) == 1


def _submission(**overrides) -> idx.UpstreamSubmission:
    fields = dict(
        distribution="adafruit-circuitpython-hcsr04",
        provides=("adafruit_hcsr04",),
        layer="circuitpython",
        example="upstream-examples/adafruit-circuitpython-hcsr04/hcsr04_simpletest.py",
    )
    fields.update(overrides)
    return idx.UpstreamSubmission(**fields)


class TestMeasureUpstreamExample:
    def test_no_committed_example_is_unsupported(self, tmp_path):
        result = uidx.measure_upstream_example(
            _submission(), "atmega328p", pymcu=Path("/nonexistent/pymcu"),
            example_source=tmp_path / "missing.py",
        )
        assert result.build == idx.BUILD_UNSUPPORTED
        assert "no measurement program" in result.detail

    def test_a_failing_build_is_reported_failed(self, tmp_path, monkeypatch):
        example = tmp_path / "hcsr04_simpletest.py"
        example.write_text("import adafruit_hcsr04\n")

        def fake_run(cmd, cwd, capture_output, text, env):
            class R:
                returncode = 1
                stdout = ""
                stderr = "error: something went wrong"
            return R()

        monkeypatch.setattr(uidx.subprocess, "run", fake_run)
        result = uidx.measure_upstream_example(
            _submission(), "atmega328p", pymcu=Path("/fake/pymcu"), example_source=example,
        )
        assert result.build == idx.BUILD_FAILED
        assert "something went wrong" in result.detail

    def test_a_missing_backend_is_unmeasured_not_failed(self, tmp_path, monkeypatch):
        """
        The measuring machine not having a backend installed says nothing
        about the library -- it must not be published as `failed`, which is
        the same distinction measure_example() already draws for manifest
        libraries.
        """
        example = tmp_path / "example.py"
        example.write_text("import adafruit_hcsr04\n")

        def fake_run(cmd, cwd, capture_output, text, env):
            class R:
                returncode = 1
                stdout = ""
                stderr = "error: backend \"pymcu-compiler[arm]\" is not installed"
            return R()

        monkeypatch.setattr(uidx.subprocess, "run", fake_run)
        result = uidx.measure_upstream_example(
            _submission(), "rp2040", pymcu=Path("/fake/pymcu"), example_source=example,
        )
        assert result.build == idx.BUILD_UNMEASURED
        assert "backend for rp2040 not installed" in result.detail

    def test_a_successful_build_reports_ok_and_flash(self, tmp_path, monkeypatch):
        example = tmp_path / "hcsr04_simpletest.py"
        example.write_text("import adafruit_hcsr04\n")

        def fake_run(cmd, cwd, capture_output, text, env):
            # The synthetic project must declare the submission's layer, and
            # a NAMED board rather than a bare chip: a CircuitPython example
            # imports `board`, which is only generated for a named one.
            pyproject = (Path(cwd) / "pyproject.toml").read_text()
            assert 'stdlib = ["circuitpython"]' in pyproject
            assert 'board = "arduino_uno"' in pyproject
            assert "target" not in pyproject
            class R:
                returncode = 0
                stdout = "Flash: 4264 bytes\n"
                stderr = ""
            return R()

        monkeypatch.setattr(uidx.subprocess, "run", fake_run)
        result = uidx.measure_upstream_example(
            _submission(), "atmega328p", pymcu=Path("/fake/pymcu"), example_source=example,
        )
        assert result.build == idx.BUILD_OK
        assert result.flash == 4264

    def test_a_native_layer_submission_still_uses_a_bare_target(self, tmp_path, monkeypatch):
        example = tmp_path / "example.py"
        example.write_text("VALUE = 1\n")

        def fake_run(cmd, cwd, capture_output, text, env):
            pyproject = (Path(cwd) / "pyproject.toml").read_text()
            assert 'target = "atmega328p"' in pyproject
            assert "board" not in pyproject
            class R:
                returncode = 0
                stdout = "Flash: 200 bytes\n"
                stderr = ""
            return R()

        monkeypatch.setattr(uidx.subprocess, "run", fake_run)
        uidx.measure_upstream_example(
            _submission(layer="native"), "atmega328p",
            pymcu=Path("/fake/pymcu"), example_source=example,
        )

    def test_the_measurement_subprocess_can_discover_the_submission_itself(
            self, tmp_path, monkeypatch):
        """
        Regression: the very first measurement of a submission is exactly the
        case where no published index describes it yet, so the `pymcu build`
        subprocess this function shells out to would otherwise never stage it
        (core.upstream_libraries.resolve_upstream_for_target reads a cached or
        overridden index, and neither exists for a brand new submission).
        PYMCU_UPSTREAM_INDEX is how this function hands that one entry to the
        subprocess instead.
        """
        example = tmp_path / "hcsr04_simpletest.py"
        example.write_text("import adafruit_hcsr04\n")

        captured = {}

        def fake_run(cmd, cwd, capture_output, text, env):
            index_path = env.get("PYMCU_UPSTREAM_INDEX")
            assert index_path, "PYMCU_UPSTREAM_INDEX was not set for the subprocess"
            captured["index"] = json.loads(Path(index_path).read_text())
            class R:
                returncode = 0
                stdout = "Flash: 4062 bytes\n"
                stderr = ""
            return R()

        monkeypatch.setattr(uidx.subprocess, "run", fake_run)
        uidx.measure_upstream_example(
            _submission(), "atmega328p", pymcu=Path("/fake/pymcu"),
            example_source=example, version="0.4.25",
        )

        entries = captured["index"]["libraries"]
        assert len(entries) == 1
        assert entries[0]["kind"] == "upstream"
        assert entries[0]["distribution"] == "adafruit-circuitpython-hcsr04"
        assert entries[0]["version"] == "0.4.25"
        assert entries[0]["provides"] == ["adafruit_hcsr04"]
        assert entries[0]["layer"] == "circuitpython"


class TestUpstreamIndexEntry:
    def test_to_json_shape(self):
        entry = uidx.UpstreamIndexEntry(
            submission=_submission(), version="0.4.25",
            summary="CircuitPython library for HC-SR04 sensors.", license="MIT",
            repository="https://github.com/adafruit/Adafruit_CircuitPython_HCSR04",
        )
        entry.targets = {
            "atmega328p": idx.TargetResult("atmega328p", idx.BUILD_OK, flash=4264),
            "rp2040": idx.TargetResult("rp2040", idx.BUILD_FAILED, detail="no board module"),
        }
        payload = entry.to_json("0.1.0a10", "2026-09-14")

        assert payload["kind"] == "upstream"
        assert payload["distribution"] == "adafruit-circuitpython-hcsr04"
        assert payload["version"] == "0.4.25"
        assert payload["provides"] == ["adafruit_hcsr04"]
        assert payload["layer"] == "circuitpython"
        assert payload["license"] == "MIT"
        assert payload["status"] == "active"
        assert payload["measured"]["targets"]["atmega328p"] == {"build": "ok", "flash": 4264}
        assert "readme" not in payload

    def test_status_is_broken_when_nothing_builds(self):
        entry = uidx.UpstreamIndexEntry(submission=_submission(), version="0.4.25")
        entry.targets = {"atmega328p": idx.TargetResult("atmega328p", idx.BUILD_FAILED)}
        assert entry.to_json("0.1.0a10", "2026-09-14")["status"] == "broken"

    def test_name_defaults_to_the_first_provided_module(self):
        entry = uidx.UpstreamIndexEntry(submission=_submission(name=""), version="0.4.25")
        assert entry.to_json("0.1.0a10", "2026-09-14")["name"] == "adafruit_hcsr04"

    def test_an_explicit_name_wins(self):
        entry = uidx.UpstreamIndexEntry(submission=_submission(name="hcsr04"), version="0.4.25")
        assert entry.to_json("0.1.0a10", "2026-09-14")["name"] == "hcsr04"


class TestBuildUpstreamEntry:
    def test_not_installed_is_a_problem_not_a_crash(self, tmp_path):
        entry, problem = uidx.build_upstream_entry(
            _submission(), pymcu=Path("/fake/pymcu"), repo_root=tmp_path, env_paths=[str(tmp_path)],
        )
        assert entry is None
        assert "not installed" in problem

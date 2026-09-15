# Tests for the upstream half of `pymcu index build`: parsing libraries.txt's
# `upstream ...` line form, and measuring a submission's committed example.

from pathlib import Path

import pytest

from src.driver.core import library_index as idx
from src.driver.core import upstream_index as uidx


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

    def test_a_successful_build_reports_ok_and_flash(self, tmp_path, monkeypatch):
        example = tmp_path / "hcsr04_simpletest.py"
        example.write_text("import adafruit_hcsr04\n")

        def fake_run(cmd, cwd, capture_output, text, env):
            # The synthetic project must declare the submission's layer.
            pyproject = (Path(cwd) / "pyproject.toml").read_text()
            assert 'stdlib = ["circuitpython"]' in pyproject
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

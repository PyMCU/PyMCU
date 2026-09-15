# Tests for the `pymcu index build`/`verify` CLI plumbing around upstream
# submissions: libraries.txt is parsed once, both the manifest distributions
# and the upstream ones reach `_prepare_venv`, and the upstream submissions
# plus the repo root they resolve `example=` against reach `build_index`.

from pathlib import Path

import pytest
from typer.testing import CliRunner

from src.driver.commands import library_index as cli

runner = CliRunner()


@pytest.fixture
def no_real_work(monkeypatch, tmp_path):
    """Replace everything that would touch a real venv, compiler or network."""
    monkeypatch.setattr(cli, "_pymcu_executable", lambda: Path("/fake/pymcu"))
    monkeypatch.setattr(cli, "_prepare_venv", lambda *a, **kw: True)
    monkeypatch.setattr(cli, "_compiler_version", lambda venv=None: "0.1.0a10")

    venv = tmp_path / ".index-venv"
    venv.mkdir()
    monkeypatch.chdir(tmp_path)
    return venv


class TestIndexBuildParsesUpstreamLines:
    def test_prepare_venv_installs_both_kinds_of_distribution(self, tmp_path, monkeypatch,
                                                               no_real_work):
        libraries_file = tmp_path / "libraries.txt"
        libraries_file.write_text(
            "pymcu-lib-dht\n"
            "upstream adafruit-circuitpython-hcsr04 provides=adafruit_hcsr04 "
            "layer=circuitpython example=upstream-examples/hcsr04.py\n"
        )

        captured = {}

        def fake_prepare_venv(distributions, venv, *, pre):
            captured["distributions"] = list(distributions)
            return True

        monkeypatch.setattr(cli, "_prepare_venv", fake_prepare_venv)
        monkeypatch.setattr(cli, "build_index",
                            lambda *a, **kw: ({"v": 1, "libraries": []}, []))

        result = runner.invoke(cli.index_app, [
            "build", "--from", str(libraries_file), "--venv", str(no_real_work),
            "--output", str(tmp_path / "index.json"),
        ])

        assert result.exit_code == 0, result.output
        assert captured["distributions"] == [
            "pymcu-lib-dht", "adafruit-circuitpython-hcsr04",
        ]

    def test_build_index_receives_the_parsed_submissions_and_repo_root(
            self, tmp_path, monkeypatch, no_real_work):
        libraries_file = tmp_path / "libraries.txt"
        libraries_file.write_text(
            "upstream adafruit-circuitpython-hcsr04 provides=adafruit_hcsr04 "
            "layer=circuitpython example=upstream-examples/hcsr04.py\n"
        )

        captured = {}

        def fake_build_index(venv, *, pymcu, compiler_version, generated,
                             upstream=None, repo_root=None):
            captured["upstream"] = list(upstream or [])
            captured["repo_root"] = repo_root
            return {"v": 1, "libraries": []}, []

        monkeypatch.setattr(cli, "build_index", fake_build_index)

        result = runner.invoke(cli.index_app, [
            "build", "--from", str(libraries_file), "--venv", str(no_real_work),
            "--output", str(tmp_path / "index.json"),
        ])

        assert result.exit_code == 0, result.output
        assert len(captured["upstream"]) == 1
        assert captured["upstream"][0].distribution == "adafruit-circuitpython-hcsr04"
        assert captured["repo_root"] == libraries_file.parent

    def test_a_malformed_upstream_line_is_reported_and_the_rest_still_builds(
            self, tmp_path, monkeypatch, no_real_work, unwrapped):
        libraries_file = tmp_path / "libraries.txt"
        libraries_file.write_text("upstream broken-line\npymcu-lib-dht\n")

        monkeypatch.setattr(cli, "build_index",
                            lambda *a, **kw: ({"v": 1, "libraries": []}, []))

        result = runner.invoke(cli.index_app, [
            "build", "--from", str(libraries_file), "--venv", str(no_real_work),
            "--output", str(tmp_path / "index.json"),
        ])

        assert result.exit_code == 0, result.output
        assert "needs provides=" in unwrapped(result.output)


class TestIndexVerifyAcceptsFrom:
    def test_without_from_no_upstream_submissions_are_passed(self, monkeypatch, no_real_work):
        captured = {}

        def fake_build_index(venv, *, pymcu, compiler_version, generated,
                             upstream=None, repo_root=None):
            captured["upstream"] = upstream
            captured["repo_root"] = repo_root
            return {"v": 1, "libraries": []}, []

        monkeypatch.setattr(cli, "build_index", fake_build_index)
        result = runner.invoke(cli.index_app, ["verify", "--venv", str(no_real_work)])

        assert result.exit_code == 0, result.output
        assert captured["upstream"] == []
        assert captured["repo_root"] is None

    def test_with_from_the_upstream_submission_reaches_build_index(
            self, tmp_path, monkeypatch, no_real_work):
        libraries_file = tmp_path / "libraries.txt"
        libraries_file.write_text(
            "upstream adafruit-circuitpython-hcsr04 provides=adafruit_hcsr04 "
            "layer=circuitpython example=upstream-examples/hcsr04.py\n"
        )
        captured = {}

        def fake_build_index(venv, *, pymcu, compiler_version, generated,
                             upstream=None, repo_root=None):
            captured["upstream"] = list(upstream or [])
            return {"v": 1, "libraries": []}, []

        monkeypatch.setattr(cli, "build_index", fake_build_index)
        result = runner.invoke(cli.index_app, [
            "verify", "--venv", str(no_real_work), "--from", str(libraries_file),
        ])

        assert result.exit_code == 0, result.output
        assert len(captured["upstream"]) == 1

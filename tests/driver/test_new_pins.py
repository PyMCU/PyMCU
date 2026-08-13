# tests/driver/test_new_pins.py
#
# Requirement pinning in the scaffolded project.
#
# `pymcu new` reads versions from the environment running the CLI, which is not
# the environment the generated project installs into. Under pipx that gap is
# the normal case: the CLI lives in an isolated venv and the stdlib flavors are
# not among its dependencies, so importlib.metadata cannot see them. That used
# to fall back to a bare package name, which pip only resolves to a prerelease
# while the package has published nothing stable -- fine today, broken the day
# a 1.0 lands and the project still needs an alpha.

import importlib.metadata as importlib_metadata
from unittest.mock import patch

import pytest
from packaging.requirements import Requirement
from typer.testing import CliRunner

from src.driver.commands.new import _pin, _PRERELEASE_FLOOR
from src.driver.main import app

runner = CliRunner()

# Stand-in for pipx isolation: everything resolves except the stdlib flavor.
_HIDDEN = "pymcu-micropython"


def _isolated_version(name: str) -> str:
    if name == _HIDDEN:
        raise importlib_metadata.PackageNotFoundError(name)
    return importlib_metadata.version(name)


@pytest.fixture
def pipx_isolation():
    """
    Hide the flavor from importlib.metadata, as pipx effectively does.

    Needed even for the direct helper tests: this repo's venv has
    pymcu-micropython installed at exactly _PRERELEASE_FLOOR, so without the
    patch the assertions would pass on the installed version and never
    exercise the fallback at all.
    """
    with patch("importlib.metadata.version", side_effect=_isolated_version):
        yield


def _scaffold(tmp_path, monkeypatch, *extra):
    monkeypatch.chdir(tmp_path)
    with patch("importlib.metadata.version", side_effect=_isolated_version):
        result = runner.invoke(
            app,
            ["new", "blinky", "--board", "arduino_uno", "--stdlib", "micropython",
             "--no-git", *extra],
            catch_exceptions=False,
        )
    assert result.exit_code == 0, result.output
    return tmp_path / "blinky"


class TestPinHelper:
    def test_installed_package_pins_to_its_version(self):
        assert _pin("pymcu-compiler").startswith("pymcu-compiler>=")

    def test_extra_is_carried_into_the_requirement(self):
        req = Requirement(_pin("pymcu-compiler", extra="[avr]"))
        assert req.extras == {"avr"}

    def test_missing_package_still_gets_a_specifier(self, pipx_isolation):
        # The bug: this used to be the bare name.
        assert _pin(_HIDDEN) == f"{_HIDDEN}>={_PRERELEASE_FLOOR}"

    def test_every_emitted_requirement_is_valid(self, pipx_isolation):
        for text in (_pin("pymcu-compiler", extra="[avr]"), _pin(_HIDDEN)):
            Requirement(text)   # raises if malformed


class TestPrereleaseAcceptance:
    def test_the_fallback_specifier_admits_prereleases(self, pipx_isolation):
        # This is the whole point: a specifier naming a prerelease marks the
        # requirement prerelease-friendly, so plain `pip install` resolves it
        # without --pre even once a stable release exists.
        assert Requirement(_pin(_HIDDEN)).specifier.prereleases is True

    def test_a_bare_name_does_not(self):
        assert Requirement(_HIDDEN).specifier.prereleases is None

    def test_the_floor_admits_the_versions_actually_published(self, pipx_isolation):
        spec = Requirement(_pin(_HIDDEN)).specifier
        for version in ("0.1.0a1", "0.1.0a1.post1", "0.1.0a5", "1.0.0"):
            assert spec.contains(version), version


class TestGeneratedProject:
    """Both emitted files go through the same helper; check both."""

    def test_requirements_txt_pins_the_hidden_flavor(self, tmp_path, monkeypatch):
        project = _scaffold(tmp_path, monkeypatch, "--pkg-manager", "pip")
        lines = (project / "requirements.txt").read_text().splitlines()

        flavor = [l for l in lines if l.startswith(_HIDDEN)]
        assert flavor == [f"{_HIDDEN}>={_PRERELEASE_FLOOR}"]
        # And nothing else lost its specifier along the way.
        assert all(">=" in line for line in lines if line.strip())

    def test_pyproject_dependencies_pin_the_hidden_flavor(self, tmp_path, monkeypatch):
        project = _scaffold(tmp_path, monkeypatch, "--pkg-manager", "uv")
        text = (project / "pyproject.toml").read_text()

        assert f"{_HIDDEN}>={_PRERELEASE_FLOOR}" in text
        assert f'"{_HIDDEN}"' not in text, "bare, unpinned flavor leaked into pyproject"

    def test_every_generated_line_is_a_valid_requirement(self, tmp_path, monkeypatch):
        # Deliberately not asserting prereleases on the installed packages: their
        # pin follows whatever version this environment has, and a stable one
        # would rightly drop the prerelease marker. The flavor that goes through
        # the fallback is covered above.
        project = _scaffold(tmp_path, monkeypatch, "--pkg-manager", "pip")
        for line in (project / "requirements.txt").read_text().splitlines():
            if line.strip():
                Requirement(line)

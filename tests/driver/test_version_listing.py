# tests/driver/test_version_listing.py
#
# What `pymcu version` reports.
#
# From the Windows 11 ARM trial: the table listed pymcu-compiler and
# pymcu-stdlib and nothing else, so on a machine with pymcu-avr installed it
# said nothing about the backend doing the actual work -- the first thing you
# want to compare when a build behaves differently on two machines. The list
# was hardcoded, which is how it fell behind the ecosystem; companions are
# discovered now.

from unittest.mock import patch

import pytest
from typer.testing import CliRunner

from src.driver.commands.version import _describe, _discover_companions
from src.driver.main import app

runner = CliRunner()


class _Dist:
    def __init__(self, name):
        self.metadata = {"Name": name}


class _EntryPoint:
    def __init__(self, dist_name):
        self.dist = type("D", (), {"name": dist_name})()


@pytest.fixture
def fake_environment():
    """A fixed set of installed packages.

    The first version of these tests asserted that pymcu-avr showed up, which
    passed here and failed on every CI runner: the machine had the backend and
    the runners did not. A discovery test has to bring its own environment,
    or it is testing the machine instead of the code.
    """
    dists = [_Dist(n) for n in (
        "pymcu-compiler", "pymcu-stdlib", "pymcu-avr", "pymcu-micropython",
        "pymcu-sdk", "PyMCU_Weird_Case", "rich", "typer",
    )]

    def _entry_points(group):
        return [_EntryPoint("pymcu-avr")] if group == "pymcu.backends" else []

    with patch("importlib.metadata.distributions", return_value=dists), \
         patch("importlib.metadata.entry_points", side_effect=_entry_points):
        yield


class TestDiscovery:
    def test_an_installed_backend_is_found(self, fake_environment):
        assert "pymcu-avr" in dict(_discover_companions(set()))

    def test_a_backend_is_described_as_one(self, fake_environment):
        assert "Codegen Backend" in dict(_discover_companions(set()))["pymcu-avr"]

    def test_the_core_two_are_not_repeated(self, fake_environment):
        already = {"pymcu-compiler", "pymcu-stdlib"}
        assert already.isdisjoint(dict(_discover_companions(already)))

    def test_non_pymcu_packages_are_left_out(self, fake_environment):
        found = dict(_discover_companions(set()))
        assert "rich" not in found and "typer" not in found

    def test_names_are_normalized(self, fake_environment):
        # Distribution metadata is free to use underscores and capitals.
        assert "pymcu-weird-case" in dict(_discover_companions(set()))

    def test_no_duplicates(self, fake_environment):
        found = _discover_companions(set())
        assert len(found) == len({name for name, _ in found})

    def test_it_survives_a_dist_without_metadata(self):
        broken = type("Broken", (), {"metadata": None})()
        with patch("importlib.metadata.distributions", return_value=[broken]), \
             patch("importlib.metadata.entry_points", return_value=[]):
            assert _discover_companions(set()) == []


class TestDescriptions:
    def test_an_unknown_package_gets_no_label(self):
        # A wrong label is worse than none: everything used to be filed as a
        # compatibility layer, including the SDKs and the toolchain binaries.
        assert _describe("pymcu-something-new") == ""

    def test_the_recognised_shapes(self):
        assert _describe("pymcu-sdk") == "SDK"
        assert _describe("pymcu-toolchain-sdk") == "SDK"
        assert _describe("pymcu-pic-toolchain") == "Toolchain Binaries"
        assert _describe("pymcu-micropython") == "Compatibility Layer"


class TestTheCommand:
    def test_a_discovered_backend_reaches_the_table(self, fake_environment, unwrapped):
        result = runner.invoke(app, ["version"])
        assert result.exit_code == 0
        assert "pymcu-avr" in unwrapped(result.output)

    def test_the_core_packages_are_always_listed(self, unwrapped):
        # These two are printed installed or not, so this one needs no fake
        # environment -- it holds on any machine.
        out = unwrapped(runner.invoke(app, ["version"]).output)
        for expected in ("pymcu-compiler", "pymcu-stdlib", "python"):
            assert expected in out

    def test_it_works_with_no_companions_at_all(self, unwrapped):
        # A bare install: nothing but the compiler and the stdlib.
        with patch("src.driver.commands.version._discover_companions", return_value=[]):
            result = runner.invoke(app, ["version"])
        assert result.exit_code == 0
        assert "pymcu-compiler" in unwrapped(result.output)

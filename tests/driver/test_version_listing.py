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

from typer.testing import CliRunner

from src.driver.commands.version import _describe, _discover_companions
from src.driver.main import app

runner = CliRunner()


class TestDiscovery:
    def test_the_installed_backend_is_found(self):
        names = dict(_discover_companions(set()))
        assert "pymcu-avr" in names, names

    def test_a_backend_is_described_as_one(self):
        assert "Codegen Backend" in dict(_discover_companions(set()))["pymcu-avr"]

    def test_the_core_two_are_not_repeated(self):
        already = {"pymcu-compiler", "pymcu-stdlib"}
        assert already.isdisjoint(dict(_discover_companions(already)))

    def test_only_pymcu_packages(self):
        assert all(name.startswith("pymcu")
                   for name in dict(_discover_companions(set())))

    def test_no_duplicates(self):
        found = _discover_companions(set())
        assert len(found) == len({name for name, _ in found})


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
    def test_the_backend_reaches_the_table(self, unwrapped):
        result = runner.invoke(app, ["version"])
        assert result.exit_code == 0
        assert "pymcu-avr" in unwrapped(result.output)

    def test_the_core_packages_are_still_there(self, unwrapped):
        out = unwrapped(runner.invoke(app, ["version"]).output)
        for expected in ("pymcu-compiler", "pymcu-stdlib", "python"):
            assert expected in out

# tests/driver/test_library_index.py
#
# Tests for the index generator: which chips get measured, how a measurement is
# compared against what the manifest promised, and the shape of the JSON.
# The compile step itself is stubbed -- what is under test is the bookkeeping
# around it, not the compiler.

from pathlib import Path

import pytest

from src.driver.core import library_index as idx
from src.driver.core.libraries import Library


def _library(tmp_path: Path, **overrides) -> Library:
    package_dir = tmp_path / "pymcu_lib_dht11"
    package_dir.mkdir(parents=True, exist_ok=True)
    values = dict(
        name="dht11",
        distribution="pymcu-lib-dht11",
        version="0.2.0",
        package_dir=package_dir,
        summary="DHT11 sensor",
        modules=["dht11"],
        arch=["avr"],
        layer="native",
    )
    values.update(overrides)
    return Library(**values)


class TestChipSelection:
    def test_one_chip_per_architecture(self, tmp_path):
        chips = idx.chips_to_measure(_library(tmp_path))
        assert set(chips) == set(idx.REPRESENTATIVE_CHIPS.values())

    def test_declared_chips_are_added(self, tmp_path):
        chips = idx.chips_to_measure(_library(tmp_path, chips=["attiny85"]))
        assert "attiny85" in chips
        assert set(idx.REPRESENTATIVE_CHIPS.values()) <= set(chips)

    def test_undeclared_architectures_are_measured_too(self, tmp_path):
        """
        The index has to be able to state 'does not build there', so a library
        is compiled for architectures it never claimed.
        """
        chips = idx.chips_to_measure(_library(tmp_path, arch=["avr"]))
        assert idx.REPRESENTATIVE_CHIPS["arm"] in chips


class TestManifestComparison:
    def test_declared_but_not_building_is_flagged(self, tmp_path):
        targets = {
            "atmega328p": idx.TargetResult("atmega328p", idx.BUILD_FAILED, detail="boom"),
        }
        warnings = idx.compare_with_manifest(_library(tmp_path), targets)
        assert warnings and "declares avr" in warnings[0] and "boom" in warnings[0]

    def test_building_without_declaring_is_flagged(self, tmp_path):
        targets = {"rp2040": idx.TargetResult("rp2040", idx.BUILD_OK, flash=200)}
        warnings = idx.compare_with_manifest(_library(tmp_path, arch=["avr"]), targets)
        assert warnings and "without declaring it" in warnings[0]

    def test_agreement_is_silent(self, tmp_path):
        targets = {
            "atmega328p": idx.TargetResult("atmega328p", idx.BUILD_OK, flash=412),
            "rp2040": idx.TargetResult("rp2040", idx.BUILD_FAILED),
        }
        assert idx.compare_with_manifest(_library(tmp_path), targets) == []


class TestFlashParsing:
    def test_reads_the_build_report(self):
        output = (
            "Building...\n"
            "Flash: 412 / 32768 bytes (1% of program storage)\n"
            "       308 bytes of your code + 104 bytes of interrupt vector table\n"
        )
        assert idx._parse_flash(output)[0] == 412

    def test_missing_report_is_none(self):
        assert idx._parse_flash("Build successful!\n")[0] is None


class TestEntryJson:
    def test_status_is_broken_when_nothing_builds(self, tmp_path):
        entry = idx.IndexEntry(library=_library(tmp_path))
        entry.targets = {"atmega328p": idx.TargetResult("atmega328p", idx.BUILD_FAILED)}
        payload = entry.to_json("0.1.0a5", "2026-08-16")
        assert payload["status"] == "broken"

    def test_status_is_active_when_something_builds(self, tmp_path):
        entry = idx.IndexEntry(library=_library(tmp_path))
        entry.targets = {
            "atmega328p": idx.TargetResult("atmega328p", idx.BUILD_OK, flash=412, ram=6),
            "rp2040": idx.TargetResult("rp2040", idx.BUILD_FAILED),
        }
        payload = entry.to_json("0.1.0a5", "2026-08-16")
        assert payload["status"] == "active"
        assert payload["measured"]["targets"]["atmega328p"] == {
            "build": "ok", "flash": 412, "ram": 6,
        }
        assert payload["measured"]["compiler"] == "0.1.0a5"

    def test_the_client_can_read_what_the_generator_writes(self, tmp_path):
        """The generator's output has to satisfy the filter `pymcu install` applies."""
        from src.driver.commands import libraries as cmd

        entry = idx.IndexEntry(library=_library(tmp_path))
        entry.targets = {
            "atmega328p": idx.TargetResult("atmega328p", idx.BUILD_OK, flash=412),
            "rp2040": idx.TargetResult("rp2040", idx.BUILD_FAILED),
        }
        index = {"v": 1, "libraries": [entry.to_json("0.1.0a5", "2026-08-16")]}

        found = cmd.find_entry(index, "dht11")
        assert found is not None
        assert cmd.entry_verdict(found, "atmega328p", []) == []
        assert cmd.entry_verdict(found, "rp2040", [])


class TestMeasurement:
    def test_a_library_without_an_example_is_unsupported(self, tmp_path):
        result = idx.measure_example(_library(tmp_path), "atmega328p",
                                     pymcu=Path("/nonexistent/pymcu"))
        assert result.build == idx.BUILD_UNSUPPORTED
        assert "no example" in result.detail

    def test_the_build_runs_with_the_filter_disabled(self, tmp_path, monkeypatch):
        """
        Measuring with the compatibility filter on would only measure the
        manifest: the build would skip the library for every architecture it
        does not declare, and the index would echo the author back to himself.
        """
        example = tmp_path / "pymcu_lib_dht11" / "examples" / "basic"
        example.mkdir(parents=True)
        (example / "pyproject.toml").write_text('[tool.pymcu]\nboard = "arduino_uno"\n')
        (example / "src").mkdir()
        (example / "src" / "main.py").write_text("def main():\n    pass\n")

        seen = {}

        class _Result:
            returncode = 0
            stdout = "Flash: 100 / 32768 bytes\n"
            stderr = ""

        def _fake_run(cmd, **kwargs):
            seen.update(kwargs.get("env") or {})
            return _Result()

        monkeypatch.setattr(idx.subprocess, "run", _fake_run)

        lib = _library(tmp_path, examples={"basic": "examples/basic"})
        result = idx.measure_example(lib, "rp2040", pymcu=Path("pymcu"))

        assert seen.get("PYMCU_LIBRARY_FILTER") == "0"
        assert result.build == idx.BUILD_OK
        assert result.flash == 100

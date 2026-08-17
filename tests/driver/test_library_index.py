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


class TestExampleSource:
    """
    Where a measurement's input comes from now that wheels carry no examples.

    Examples belong at the distribution root, with the tests and the docs, so
    they travel in the sdist. That keeps them out of the package -- when they
    were inside it, the whole package went on the include path and `import
    examples` resolved from any user's firmware.
    """

    def _with_example(self, tmp_path: Path) -> Library:
        example = tmp_path / "examples" / "basic"
        (example / "src").mkdir(parents=True)
        (example / "pyproject.toml").write_text('[tool.pymcu]\nboard = "arduino_uno"\n')
        (example / "src" / "main.py").write_text("def main():\n    pass\n")
        return _library(tmp_path, examples={"basic": "examples/basic"})

    def test_a_checkout_is_used_without_touching_the_network(self, tmp_path, monkeypatch):
        def _no_network(*_args, **_kwargs):
            raise AssertionError("a checkout on disk must not be fetched again")

        monkeypatch.setattr(idx, "fetch_sdist", _no_network)

        # package_dir is tmp_path/pymcu_lib_dht11, so examples/ at tmp_path is
        # the distribution root -- exactly the published layout.
        lib = self._with_example(tmp_path)
        source, how = idx.example_source(lib, "", tmp_path / "work")

        assert how == "checkout"
        assert (source / "src" / "main.py").exists()

    def test_an_installed_library_falls_back_to_the_sdist(self, tmp_path, monkeypatch):
        unpacked = tmp_path / "sdist-root"
        (unpacked / "examples" / "basic" / "src").mkdir(parents=True)

        monkeypatch.setattr(idx, "fetch_sdist", lambda *_a, **_k: unpacked)

        lib = _library(tmp_path, examples={"basic": "examples/basic"})
        source, how = idx.example_source(lib, "", tmp_path / "work")

        assert how == "sdist"
        assert source == unpacked / "examples" / "basic"

    def test_no_sdist_is_reported_rather_than_read_as_a_build_failure(self, tmp_path,
                                                                     monkeypatch):
        """
        A library nobody can measure is not a library that fails to compile.

        Reporting the two the same way would mark a perfectly good library
        `broken` in the index because its author published no sdist.
        """
        monkeypatch.setattr(idx, "fetch_sdist", lambda *_a, **_k: None)

        lib = _library(tmp_path, examples={"basic": "examples/basic"})
        source, how = idx.example_source(lib, "", tmp_path / "work")

        assert source is None
        assert how == "no sdist available"

    def test_a_library_without_examples_says_so(self, tmp_path):
        source, how = idx.example_source(_library(tmp_path), "", tmp_path / "work")
        assert source is None
        assert how == "none declared"

    def test_the_sdist_is_fetched_once_per_library_not_once_per_chip(self, tmp_path,
                                                                     monkeypatch):
        """Three chips used to mean three downloads of the same archive."""
        unpacked = tmp_path / "sdist-root"
        (unpacked / "examples" / "basic" / "src").mkdir(parents=True)
        (unpacked / "examples" / "basic" / "pyproject.toml").write_text(
            '[tool.pymcu]\nboard = "arduino_uno"\n')
        (unpacked / "examples" / "basic" / "src" / "main.py").write_text(
            "def main():\n    pass\n")

        calls = []

        def _fetch(distribution, version, dest):
            calls.append(distribution)
            return unpacked

        monkeypatch.setattr(idx, "fetch_sdist", _fetch)

        class _Result:
            returncode = 0
            stdout = "Flash: 100 / 32768 bytes\n"
            stderr = ""

        monkeypatch.setattr(idx.subprocess, "run", lambda *_a, **_k: _Result())

        lib = _library(tmp_path, examples={"basic": "examples/basic"})
        entry = idx.build_entry(lib, pymcu=Path("pymcu"))

        assert len(calls) == 1
        assert len(entry.targets) > 1
        assert entry.example["source"].strip() == "def main():\n    pass"

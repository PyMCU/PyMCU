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

    def _failing_build(self, tmp_path, monkeypatch, chip, **lib_kwargs):
        example = tmp_path / "pymcu_lib_dht11" / "examples" / "basic"
        example.mkdir(parents=True, exist_ok=True)
        (example / "pyproject.toml").write_text('[tool.pymcu]\nboard = "arduino_uno"\n')
        (example / "src").mkdir(exist_ok=True)
        (example / "src" / "main.py").write_text("def main():\n    pass\n")

        class _Result:
            returncode = 1
            stdout = ""
            stderr = 'main.py:-1:1: error: TypeError: cannot shift by the string "PB5"\n'

        monkeypatch.setattr(idx.subprocess, "run", lambda cmd, **kw: _Result())
        lib = _library(tmp_path, examples={"basic": "examples/basic"}, **lib_kwargs)
        return idx.measure_example(lib, chip, pymcu=Path("pymcu"))

    def test_failing_where_the_author_never_claimed_is_unsupported(self, tmp_path, monkeypatch):
        """
        The published index said pymcu-lib-dht `failed` on rp2040, quoting a
        compiler internals error, for a library whose manifest says
        arch = ["avr"]. That reads as "this library is broken" about something
        it never promised. The build still runs -- code beating a cautious
        manifest is worth publishing -- but a failure there is out of scope,
        not a defect.
        """
        result = self._failing_build(tmp_path, monkeypatch, "rp2040", arch=["avr"])
        assert result.build == idx.BUILD_UNSUPPORTED
        assert "PB5" in result.detail

    def test_failing_on_a_declared_architecture_is_still_a_failure(self, tmp_path, monkeypatch):
        """A broken promise is exactly what the index must be able to state."""
        result = self._failing_build(tmp_path, monkeypatch, "atmega328p", arch=["avr"])
        assert result.build == idx.BUILD_FAILED

    def test_declaring_nothing_claims_everything(self, tmp_path, monkeypatch):
        """Silence in the manifest is not a way out of being measured."""
        result = self._failing_build(tmp_path, monkeypatch, "rp2040", arch=[])
        assert result.build == idx.BUILD_FAILED

    def test_a_declared_chip_list_wins_over_the_architecture(self, tmp_path, monkeypatch):
        result = self._failing_build(tmp_path, monkeypatch, "atmega328p",
                                    arch=["avr"], chips=["attiny85"])
        assert result.build == idx.BUILD_UNSUPPORTED

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

        monkeypatch.setattr(idx, "fetch_sdist", lambda *_a, **_k: (unpacked, "sdist"))

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
        monkeypatch.setattr(idx, "fetch_sdist",
                            lambda d, v, _dest: (None, f"{d} {v} publishes no sdist"))

        lib = _library(tmp_path, examples={"basic": "examples/basic"})
        source, how = idx.example_source(lib, "", tmp_path / "work")

        assert source is None
        # The reason has to name the actual cause. "no sdist available" was
        # printed for two libraries that do publish one, when what had
        # happened was a TLS failure on the measuring machine.
        assert how == "pymcu-lib-dht11 0.2.0 publishes no sdist"

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
            return unpacked, "sdist"

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


class TestOurFaultsAreNotTheirFaults:
    """
    Two failures that reported someone else's library as broken.

    Both showed up generating the first real index, and both said the wrong
    thing in the same way: they took something about the measuring machine and
    published it as a fact about the library being measured.
    """

    def test_a_missing_backend_is_unmeasured_not_failed(self, tmp_path, monkeypatch):
        """
        The index said "does not build for rp2040" about two AVR libraries.

        What had happened is that the environment doing the measuring had only
        the AVR backend installed, so the compiler refused the target before
        it ever looked at the library.
        """
        example = tmp_path / "examples" / "basic"
        (example / "src").mkdir(parents=True)
        (example / "pyproject.toml").write_text('[tool.pymcu]\nboard = "arduino_uno"\n')
        (example / "src" / "main.py").write_text("def main():\n    pass\n")

        class _Result:
            returncode = 1
            stdout = ''
            stderr = ('No backend for rp2040. Install it with:\n'
                      '  pip install "pymcu-compiler[arm]"\n')

        monkeypatch.setattr(idx.subprocess, "run", lambda *_a, **_k: _Result())

        lib = _library(tmp_path, examples={"basic": "examples/basic"})
        result = idx.measure_example(lib, "rp2040", pymcu=Path("pymcu"),
                                    example_dir=example)

        assert result.build == idx.BUILD_UNMEASURED
        assert "not installed" in result.detail

    def test_an_unmeasured_target_is_not_held_against_the_manifest(self, tmp_path):
        """
        Telling an author their supports.arch is wrong needs a real build.

        With a missing backend there is no measurement, so a warning here
        would be an accusation resting on a build that never ran.
        """
        lib = _library(tmp_path, arch=["avr", "arm"])
        targets = {
            "atmega328p": idx.TargetResult("atmega328p", idx.BUILD_OK, flash=900),
            "rp2040": idx.TargetResult("rp2040", idx.BUILD_UNMEASURED,
                                       detail="backend for rp2040 not installed"),
        }
        assert idx.compare_with_manifest(lib, targets) == []

    def test_a_real_failure_is_still_reported(self, tmp_path):
        """The quiet path must not swallow the case it exists to catch."""
        lib = _library(tmp_path, arch=["avr", "arm"])
        targets = {
            "rp2040": idx.TargetResult("rp2040", idx.BUILD_FAILED,
                                       detail="CompileError: not supported here"),
        }
        warnings = idx.compare_with_manifest(lib, targets)
        assert len(warnings) == 1
        assert "declares arm" in warnings[0]


class TestFailureReason:
    """What gets published as the reason a build failed."""

    def test_the_temporary_path_is_not_the_reason(self):
        """
        A diagnostic starts with the path of the copy that was compiled.

        The first real index recorded that path as the reason two libraries
        did not build: it filled the whole 200-character budget on its own and
        said nothing at all.
        """
        output = (
            '/private/var/folders/t_/n_fcfh5n0yv8fryx9b9sy2m40000gn/T/tmph5jb45kc/'
            'example/src/main.py:-1:1: error: TypeError: cannot shift by the string '
            '"PB5" -- a number is expected here.\n'
            'Compilation Error: Compilation failed (see diagnostics above)\n'
        )
        reason = idx._failure_reason(output)
        assert reason.startswith("TypeError: cannot shift by the string")
        assert "tmph5jb45kc" not in reason

    def test_output_without_a_diagnostic_falls_back_to_the_last_line(self):
        reason = idx._failure_reason("something went wrong\nand then stopped\n")
        assert reason == "and then stopped"

    def test_empty_output_still_says_something(self):
        assert idx._failure_reason("   \n") == "build failed"

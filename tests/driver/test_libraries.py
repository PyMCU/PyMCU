# tests/driver/test_libraries.py
#
# Tests for library discovery: manifest parsing, target compatibility, module
# collisions and where the driver looks for a project's libraries.

from pathlib import Path

import pytest

from src.driver.core import libraries as core


MANIFEST = """
[library]
name = "dht11"
summary = "DHT11 temperature and humidity sensor"
license = "MIT"

[library.provides]
modules = ["dht11"]

[library.supports]
arch = ["avr"]
layer = "native"
adapters = ["micropython"]

[library.requires]
language-level = 1
"""


def _make_package(tmp_path: Path, manifest: str = MANIFEST, *, name: str = "pymcu_lib_dht11") -> Path:
    pkg = tmp_path / "src" / name
    pkg.mkdir(parents=True)
    (tmp_path / "pyproject.toml").write_text("[project]\nname = 'pymcu-lib-dht11'\n")
    (pkg / "pymcu.toml").write_text(manifest)
    (pkg / "dht11.py").write_text(
        "from pymcu.chips import __CHIP__\n"
        "from pymcu.exceptions import CompileError\n"
        "from pymcu.types import uint16, inline\n"
        "\n"
        "\n"
        "class DHT11:\n"
        "\n"
        "    @inline\n"
        "    def __init__(self, pin: str):\n"
        "        self.name = pin\n"
        "\n"
        "    @inline\n"
        "    def read(self) -> uint16:\n"
        "        match __CHIP__.arch:\n"
        "            case \"avr\":\n"
        "                return 0\n"
        "            case _:\n"
        "                raise CompileError(\"DHT11 is not supported here\")\n"
    )
    adapter = pkg / "compat" / "micropython"
    adapter.mkdir(parents=True)
    (adapter / "dht11.py").write_text("from dht11 import DHT11\n")
    return pkg


def _library(pkg: Path, manifest: str = MANIFEST) -> core.Library:
    return core.parse_manifest(
        pkg / "pymcu.toml", distribution="pymcu-lib-dht11", version="0.2.0", package_dir=pkg
    )


# ---------------------------------------------------------------------------
# Manifest
# ---------------------------------------------------------------------------

class TestManifest:
    def test_parses_a_valid_manifest(self, tmp_path):
        lib = _library(_make_package(tmp_path))
        assert lib.name == "dht11"
        assert lib.modules == ["dht11"]
        assert lib.arch == ["avr"]
        assert lib.layer == "native"
        assert lib.version == "0.2.0"

    def test_version_in_manifest_is_refused(self, tmp_path):
        manifest = MANIFEST.replace('name = "dht11"', 'name = "dht11"\nversion = "0.2.0"')
        pkg = _make_package(tmp_path, manifest)
        with pytest.raises(core.ManifestError) as exc:
            _library(pkg, manifest)
        assert "version" in str(exc.value)

    def test_unknown_layer_is_refused(self, tmp_path):
        manifest = MANIFEST.replace('layer = "native"', 'layer = "arduino"')
        pkg = _make_package(tmp_path, manifest)
        with pytest.raises(core.ManifestError):
            _library(pkg, manifest)

    def test_modules_are_required(self, tmp_path):
        manifest = MANIFEST.replace('modules = ["dht11"]', "modules = []")
        pkg = _make_package(tmp_path, manifest)
        with pytest.raises(core.ManifestError):
            _library(pkg, manifest)


# ---------------------------------------------------------------------------
# Compatibility
# ---------------------------------------------------------------------------

class TestCompatibility:
    def test_matching_arch_is_usable(self, tmp_path, monkeypatch):
        monkeypatch.setattr(core, "chip_arch", lambda chip: "avr")
        lib = _library(_make_package(tmp_path))
        assert core.check_compatibility(lib, chip="atmega328p", flavors=[]) == []

    def test_wrong_arch_names_the_chip(self, tmp_path, monkeypatch):
        monkeypatch.setattr(core, "chip_arch", lambda chip: "arm")
        lib = _library(_make_package(tmp_path))
        reasons = core.check_compatibility(lib, chip="rp2350", flavors=[])
        assert reasons and "rp2350" in reasons[0] and "avr" in reasons[0]

    def test_chips_list_narrows_further(self, tmp_path, monkeypatch):
        manifest = MANIFEST.replace('arch = ["avr"]', 'arch = ["avr"]\nchips = ["attiny85"]')
        pkg = _make_package(tmp_path, manifest)
        monkeypatch.setattr(core, "chip_arch", lambda chip: "avr")
        reasons = core.check_compatibility(_library(pkg, manifest), chip="atmega328p", flavors=[])
        assert reasons and "attiny85" in reasons[0]

    def test_layer_library_needs_that_flavor(self, tmp_path, monkeypatch):
        manifest = MANIFEST.replace('layer = "native"', 'layer = "micropython"')
        pkg = _make_package(tmp_path, manifest)
        monkeypatch.setattr(core, "chip_arch", lambda chip: "avr")
        lib = _library(pkg, manifest)
        assert core.check_compatibility(lib, chip="atmega328p", flavors=["circuitpython"])
        assert core.check_compatibility(lib, chip="atmega328p", flavors=["micropython"]) == []

    def test_future_language_level_is_refused(self, tmp_path, monkeypatch):
        manifest = MANIFEST.replace("language-level = 1", "language-level = 99")
        pkg = _make_package(tmp_path, manifest)
        monkeypatch.setattr(core, "chip_arch", lambda chip: "avr")
        reasons = core.check_compatibility(_library(pkg, manifest), chip="atmega328p", flavors=[])
        assert any("language level" in r for r in reasons)


class TestCollisionsAndPaths:
    def test_two_libraries_claiming_one_module_collide(self, tmp_path):
        first = _library(_make_package(tmp_path / "a"))
        second = _library(_make_package(tmp_path / "b"))
        object.__setattr__(second, "distribution", "pymcu-lib-other")
        collisions = core.find_module_collisions([first, second])
        assert collisions and "dht11" in collisions[0]

    def test_adapter_comes_before_the_package(self, tmp_path):
        lib = _library(_make_package(tmp_path))
        paths = core.include_paths([lib], ["micropython"])
        assert paths[0].endswith("compat/micropython")
        assert paths[1] == str(lib.package_dir)

    def test_no_adapter_without_the_flavor(self, tmp_path):
        lib = _library(_make_package(tmp_path))
        assert core.include_paths([lib], []) == [str(lib.package_dir)]


class TestChipArch:
    def test_reads_arch_from_the_stdlib_chip_file(self):
        assert core.chip_arch("atmega328p") == "avr"
        assert core.chip_arch("rp2040") == "arm"

    def test_unknown_chip_is_empty(self):
        assert core.chip_arch("not_a_chip") == ""


class TestVenvDiscovery:
    """
    The driver may run outside the project's environment (pipx, a global
    install, or the moment right after installing into the .venv), so discovery
    has to read that environment rather than sys.path.
    """

    def test_libraries_are_read_from_an_explicit_search_path(self, tmp_path):
        site = tmp_path / "site-packages"
        pkg = site / "pymcu_lib_dht11"
        pkg.mkdir(parents=True)
        (pkg / "pymcu.toml").write_text(MANIFEST)
        (pkg / "dht11.py").write_text("")

        dist_info = site / "pymcu_lib_dht11-0.2.0.dist-info"
        dist_info.mkdir()
        (dist_info / "METADATA").write_text(
            "Metadata-Version: 2.1\nName: pymcu-lib-dht11\nVersion: 0.2.0\n")
        (dist_info / "entry_points.txt").write_text(
            "[pymcu.libraries]\ndht11 = pymcu_lib_dht11\n")

        found, problems = core.discover_libraries(search_path=[str(site)])
        assert problems == []
        assert [lib.name for lib in found] == ["dht11"]
        assert found[0].distribution == "pymcu-lib-dht11"
        assert found[0].version == "0.2.0"

    def test_site_packages_of_a_venv_layout(self, tmp_path):
        target = tmp_path / "lib" / "python3.14" / "site-packages"
        target.mkdir(parents=True)
        assert core.site_packages_of(tmp_path) == [str(target)]

    def test_build_looks_into_the_project_venv_when_running_elsewhere(self, tmp_path):
        site = tmp_path / ".venv" / "lib" / "python3.14" / "site-packages"
        site.mkdir(parents=True)
        assert core.search_path_for_project(tmp_path) == [str(site)]

    def test_no_search_path_when_already_inside_that_venv(self, tmp_path, monkeypatch):
        site = tmp_path / ".venv" / "lib" / "python3.14" / "site-packages"
        site.mkdir(parents=True)
        monkeypatch.setattr(core.sys, "prefix", str(tmp_path / ".venv"))
        assert core.search_path_for_project(tmp_path) is None

    def test_no_search_path_without_a_venv(self, tmp_path):
        assert core.search_path_for_project(tmp_path) is None




class TestReadme:
    """
    A library page without the author's own words is a stub. The readme is
    already inside the wheel, so it is read from there: no network, and no
    second copy to fall out of step with the installed version.
    """

    def _dist(self, tmp_path, body, headers=""):
        site = tmp_path / "site-packages"
        info = site / "pymcu_lib_dht11-0.2.0.dist-info"
        info.mkdir(parents=True)
        (info / "METADATA").write_text(
            "Metadata-Version: 2.1\n"
            "Name: pymcu-lib-dht11\n"
            "Version: 0.2.0\n"
            "Description-Content-Type: text/markdown\n"
            f"{headers}\n{body}"
        )
        return [str(site)]

    def test_the_body_of_metadata_is_the_readme(self, tmp_path):
        search = self._dist(tmp_path, "# dht11\n\nA sensor driver.\n")
        text, kind = core.read_description("pymcu-lib-dht11", search)
        assert "# dht11" in text and "A sensor driver." in text
        assert kind == "text/markdown"

    def test_the_legacy_description_header_still_works(self, tmp_path):
        site = tmp_path / "site-packages"
        info = site / "pymcu_lib_dht11-0.2.0.dist-info"
        info.mkdir(parents=True)
        (info / "METADATA").write_text(
            "Metadata-Version: 2.1\n"
            "Name: pymcu-lib-dht11\n"
            "Version: 0.2.0\n"
            "Description: An older wheel put it here.\n\n"
        )
        text, _kind = core.read_description("pymcu-lib-dht11", [str(site)])
        assert "older wheel" in text

    def test_underscores_and_dashes_name_the_same_package(self, tmp_path):
        search = self._dist(tmp_path, "Readme body.\n")
        assert core.read_description("pymcu_lib_dht11", search)[0] == "Readme body."

    def test_a_missing_package_is_empty_not_an_error(self, tmp_path):
        assert core.read_description("nothing-here", [str(tmp_path)]) == ("", "")

    def test_a_very_long_readme_is_cut_and_says_so(self, tmp_path):
        search = self._dist(tmp_path, "line\n" * 20000)
        text, _kind = core.read_description("pymcu-lib-dht11", search)
        assert len(text) <= core.README_LIMIT + 40
        assert text.endswith("[…truncated]")


class TestExample:
    """
    The example on a library's page is the same file the index compiles to
    measure it, so the code someone reads and the byte figure they see cannot
    describe different things.
    """

    def _with_example(self, tmp_path, body="from x import y\n"):
        pkg = _make_package(tmp_path)
        example = pkg / "examples" / "basic" / "src"
        example.mkdir(parents=True)
        (example / "main.py").write_text(body)
        (pkg / "examples" / "basic" / "pyproject.toml").write_text('[tool.pymcu]\nboard = "arduino_uno"\n')
        manifest = MANIFEST + '\n[library.examples]\nbasic = "examples/basic"\n'
        (pkg / "pymcu.toml").write_text(manifest)
        return _library(pkg, manifest)

    def test_it_reads_the_entry_point(self, tmp_path):
        lib = self._with_example(tmp_path, "from dht11 import DHT11\n")
        example = core.read_example(lib)
        assert example["file"] == "main.py"
        assert example["name"] == "basic"
        assert "from dht11 import DHT11" in example["source"]

    def test_no_example_is_an_empty_dict(self, tmp_path):
        lib = _library(_make_package(tmp_path))
        assert core.read_example(lib) == {}

    def test_a_long_example_is_cut_and_marked(self, tmp_path):
        lib = self._with_example(tmp_path, "x = 1\n" * 4000)
        example = core.read_example(lib)
        assert len(example["source"]) <= core.EXAMPLE_LIMIT + 40
        assert example["source"].endswith("# …truncated")

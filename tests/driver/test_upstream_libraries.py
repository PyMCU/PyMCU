# Tests for upstream libraries: index entries that name a plain PyPI
# distribution instead of a PyMCU manifest, and how the driver stages what
# they provide onto the compiler's include path.

import json
from pathlib import Path

import pytest

from src.driver.core import upstream_libraries as up


def _write_dist(site: Path, *, distribution: str, version: str,
                module: str, is_package: bool) -> Path:
    """
    An installed distribution laid out the way pip/uv leave one: a
    `<name>-<version>.dist-info` directory (METADATA + RECORD) beside the
    module it installed. RECORD is what `Distribution.files` reads, so it has
    to list every path -- exactly the information staging is supposed to use
    instead of importing anything.
    """
    safe = distribution.replace("-", "_")
    dist_info = site / f"{safe}-{version}.dist-info"
    dist_info.mkdir(parents=True)
    (dist_info / "METADATA").write_text(
        f"Metadata-Version: 2.1\nName: {distribution}\nVersion: {version}\n"
    )

    record_lines = [f"{dist_info.name}/METADATA,,"]
    if is_package:
        pkg = site / module
        pkg.mkdir()
        (pkg / "__init__.py").write_text(f"# {module}\nVALUE = 1\n")
        (pkg / "core.py").write_text("VALUE = 2\n")
        record_lines += [f"{module}/__init__.py,,", f"{module}/core.py,,"]
    else:
        (site / f"{module}.py").write_text(f"# {module}\nVALUE = 1\n")
        record_lines.append(f"{module}.py,,")
    (dist_info / "RECORD").write_text("\n".join(record_lines) + "\n")
    return dist_info


def _index(distribution="adafruit-circuitpython-hcsr04", version="0.4.25",
          provides=("adafruit_hcsr04",), layer="circuitpython",
          name="", kind="upstream") -> dict:
    entry = {
        "kind": kind,
        "name": name or distribution,
        "distribution": distribution,
        "version": version,
        "provides": list(provides),
        "layer": layer,
        "repository": "https://github.com/adafruit/Adafruit_CircuitPython_HCSR04",
    }
    return {"v": 1, "libraries": [entry]}


class TestUpstreamEntries:
    def test_parses_a_well_formed_entry(self):
        found = up.upstream_entries(_index())
        assert len(found) == 1
        assert found[0].distribution == "adafruit-circuitpython-hcsr04"
        assert found[0].provides == ("adafruit_hcsr04",)
        assert found[0].layer == "circuitpython"

    def test_ignores_manifest_entries(self):
        index = {"v": 1, "libraries": [{"kind": "library", "distribution": "pymcu-lib-dht"}]}
        assert up.upstream_entries(index) == []

    def test_ignores_an_entry_with_no_provides(self):
        index = _index(provides=())
        assert up.upstream_entries(index) == []

    def test_unknown_layer_falls_back_to_native(self):
        index = _index(layer="something-else")
        assert up.upstream_entries(index)[0].layer == "native"

    def test_malformed_index_yields_nothing(self):
        assert up.upstream_entries({}) == []
        assert up.upstream_entries([]) == []  # type: ignore[arg-type]


class TestDiscoverInstalled:
    def test_finds_an_installed_distribution_by_name(self, tmp_path):
        _write_dist(tmp_path, distribution="adafruit-circuitpython-hcsr04",
                   version="0.4.25", module="adafruit_hcsr04", is_package=False)
        entries = up.upstream_entries(_index())

        found = up.discover_installed_upstream(entries, [str(tmp_path)])
        assert len(found) == 1
        assert found[0].version == "0.4.25"

    def test_not_installed_is_not_reported(self, tmp_path):
        entries = up.upstream_entries(_index())
        assert up.discover_installed_upstream(entries, [str(tmp_path)]) == []

    def test_reports_the_version_actually_installed_not_the_indexed_one(self, tmp_path):
        _write_dist(tmp_path, distribution="adafruit-circuitpython-hcsr04",
                   version="0.4.26", module="adafruit_hcsr04", is_package=False)
        entries = up.upstream_entries(_index(version="0.4.25"))

        found = up.discover_installed_upstream(entries, [str(tmp_path)])
        assert found[0].version == "0.4.26"


class TestStageModules:
    def test_stages_a_single_file_module(self, tmp_path):
        site = tmp_path / "site-packages"
        _write_dist(site, distribution="adafruit-circuitpython-hcsr04", version="0.4.25",
                   module="adafruit_hcsr04", is_package=False)
        entry = up.upstream_entries(_index())[0]

        stage_root = tmp_path / "dist" / "_upstream"
        staged = up.stage_modules(entry, [str(site)], stage_root)

        assert staged == stage_root / "adafruit-circuitpython-hcsr04"
        assert (staged / "adafruit_hcsr04.py").read_text() == "# adafruit_hcsr04\nVALUE = 1\n"
        # Nothing else from site-packages travels along.
        assert list(staged.iterdir()) == [staged / "adafruit_hcsr04.py"]

    def test_stages_a_package_module(self, tmp_path):
        site = tmp_path / "site-packages"
        _write_dist(site, distribution="pymcu-lib-neopixel-upstream", version="1.0.0",
                   module="some_pkg", is_package=True)
        entry = up.UpstreamEntry(name="some_pkg", distribution="pymcu-lib-neopixel-upstream",
                                 version="1.0.0", provides=("some_pkg",), layer="native")

        staged = up.stage_modules(entry, [str(site)], tmp_path / "dist" / "_upstream")
        assert (staged / "some_pkg" / "__init__.py").is_file()
        assert (staged / "some_pkg" / "core.py").is_file()

    def test_not_installed_stages_nothing(self, tmp_path):
        entry = up.UpstreamEntry(name="x", distribution="not-installed", version="1.0",
                                 provides=("x",), layer="native")
        assert up.stage_modules(entry, [str(tmp_path)], tmp_path / "dist" / "_upstream") is None

    def test_a_declared_module_that_is_not_actually_there_stages_nothing(self, tmp_path):
        site = tmp_path / "site-packages"
        _write_dist(site, distribution="adafruit-circuitpython-hcsr04", version="0.4.25",
                   module="adafruit_hcsr04", is_package=False)
        entry = up.UpstreamEntry(name="x", distribution="adafruit-circuitpython-hcsr04",
                                 version="0.4.25", provides=("does_not_exist",), layer="native")
        assert up.stage_modules(entry, [str(site)], tmp_path / "dist" / "_upstream") is None

    def test_restaging_replaces_stale_files(self, tmp_path):
        site = tmp_path / "site-packages"
        _write_dist(site, distribution="adafruit-circuitpython-hcsr04", version="0.4.25",
                   module="adafruit_hcsr04", is_package=False)
        entry = up.upstream_entries(_index())[0]
        stage_root = tmp_path / "dist" / "_upstream"

        target = stage_root / "adafruit-circuitpython-hcsr04"
        target.mkdir(parents=True)
        (target / "leftover.py").write_text("stale\n")

        staged = up.stage_modules(entry, [str(site)], stage_root)
        assert not (staged / "leftover.py").exists()
        assert (staged / "adafruit_hcsr04.py").exists()


class TestResolveUpstreamForTarget:
    def _installed(self, tmp_path, *, layer="circuitpython"):
        site = tmp_path / "site-packages"
        _write_dist(site, distribution="adafruit-circuitpython-hcsr04", version="0.4.25",
                   module="adafruit_hcsr04", is_package=False)
        cache = tmp_path / "cache.json"
        cache.write_text(json.dumps(_index(layer=layer)))
        return site, cache

    def test_no_cached_index_means_nothing_upstream(self, tmp_path, monkeypatch):
        monkeypatch.setattr(up, "read_cached_library_index", lambda: {})
        includes, skipped, errors = up.resolve_upstream_for_target(
            search_path=[str(tmp_path)], flavors=[], stage_root=tmp_path / "_upstream")
        assert (includes, skipped, errors) == ([], [], [])

    def test_included_when_the_flavor_is_declared(self, tmp_path, monkeypatch):
        site, cache = self._installed(tmp_path)
        monkeypatch.setattr(up, "read_cached_library_index",
                            lambda: json.loads(cache.read_text()))

        includes, skipped, errors = up.resolve_upstream_for_target(
            search_path=[str(site)], flavors=["circuitpython"],
            stage_root=tmp_path / "dist" / "_upstream")

        assert errors == []
        assert skipped == []
        assert includes == [str(tmp_path / "dist" / "_upstream" / "adafruit-circuitpython-hcsr04")]

    def test_skipped_when_the_flavor_is_not_declared(self, tmp_path, monkeypatch):
        site, cache = self._installed(tmp_path)
        monkeypatch.setattr(up, "read_cached_library_index",
                            lambda: json.loads(cache.read_text()))

        includes, skipped, errors = up.resolve_upstream_for_target(
            search_path=[str(site)], flavors=[], stage_root=tmp_path / "dist" / "_upstream")

        assert includes == []
        assert errors == []
        assert "circuitpython" in skipped[0]

    def test_enforce_false_ignores_the_layer_mismatch(self, tmp_path, monkeypatch):
        site, cache = self._installed(tmp_path)
        monkeypatch.setattr(up, "read_cached_library_index",
                            lambda: json.loads(cache.read_text()))

        includes, skipped, errors = up.resolve_upstream_for_target(
            search_path=[str(site)], flavors=[], stage_root=tmp_path / "dist" / "_upstream",
            enforce=False)

        assert skipped == []
        assert len(includes) == 1

    def test_upstream_index_env_override_bypasses_the_cache(self, tmp_path, monkeypatch):
        site, cache = self._installed(tmp_path)
        monkeypatch.setattr(up, "read_cached_library_index", lambda: (_ for _ in ()).throw(
            AssertionError("must not read the cache when PYMCU_UPSTREAM_INDEX is set")))
        monkeypatch.setenv("PYMCU_UPSTREAM_INDEX", str(cache))

        includes, skipped, errors = up.resolve_upstream_for_target(
            search_path=[str(site)], flavors=["circuitpython"],
            stage_root=tmp_path / "dist" / "_upstream")

        assert errors == []
        assert len(includes) == 1

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


def _write_modules_dist(site: Path, *, distribution: str, version: str,
                        modules: tuple[str, ...]) -> Path:
    safe = distribution.replace("-", "_").replace(".", "_")
    dist_info = site / f"{safe}-{version}.dist-info"
    dist_info.mkdir(parents=True)
    (dist_info / "METADATA").write_text(
        f"Metadata-Version: 2.1\nName: {distribution}\nVersion: {version}\n"
    )
    (dist_info / "top_level.txt").write_text("\n".join(modules) + "\n")
    records = [f"{dist_info.name}/METADATA,,", f"{dist_info.name}/top_level.txt,,"]
    for module in modules:
        (site / f"{module}.py").write_text(f"# {module}\nVALUE = 1\n")
        records.append(f"{module}.py,,")
    (dist_info / "RECORD").write_text("\n".join(records) + "\n")
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

    def test_an_unindexed_distribution_is_a_fallback_candidate(self, tmp_path):
        _write_dist(tmp_path, distribution="adafruit-circuitpython-hcsr04",
                    version="0.4.25", module="adafruit_hcsr04", is_package=False)

        found = up.discover_fallback_distributions([], [str(tmp_path)])

        assert [(item.name, item.modules) for item in found] == [
            ("adafruit-circuitpython-hcsr04", ("adafruit_hcsr04",))
        ]


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
        includes, skipped, errors, warned = up.resolve_upstream_for_target(
            search_path=[str(tmp_path)], flavors=[], stage_root=tmp_path / "_upstream")
        assert (includes, skipped, errors, warned) == ([], [], [], [])

    def test_included_when_the_flavor_is_declared(self, tmp_path, monkeypatch):
        site, cache = self._installed(tmp_path)
        monkeypatch.setattr(up, "read_cached_library_index",
                            lambda: json.loads(cache.read_text()))

        includes, skipped, errors, warned = up.resolve_upstream_for_target(
            search_path=[str(site)], flavors=["circuitpython"],
            stage_root=tmp_path / "dist" / "_upstream")

        assert errors == []
        assert skipped == []
        assert warned == []
        assert includes == [str(tmp_path / "dist" / "_upstream" / "adafruit-circuitpython-hcsr04")]

    def test_skipped_when_the_flavor_is_not_declared(self, tmp_path, monkeypatch):
        site, cache = self._installed(tmp_path)
        monkeypatch.setattr(up, "read_cached_library_index",
                            lambda: json.loads(cache.read_text()))

        includes, skipped, errors, warned = up.resolve_upstream_for_target(
            search_path=[str(site)], flavors=[], stage_root=tmp_path / "dist" / "_upstream")

        assert includes == []
        assert errors == []
        assert warned == []
        assert "circuitpython" in skipped[0]

    def test_enforce_false_ignores_the_layer_mismatch(self, tmp_path, monkeypatch):
        site, cache = self._installed(tmp_path)
        monkeypatch.setattr(up, "read_cached_library_index",
                            lambda: json.loads(cache.read_text()))

        includes, skipped, errors, warned = up.resolve_upstream_for_target(
            search_path=[str(site)], flavors=[], stage_root=tmp_path / "dist" / "_upstream",
            enforce=False)

        assert skipped == []
        assert warned == []
        assert len(includes) == 1

    def test_upstream_index_env_override_bypasses_the_cache(self, tmp_path, monkeypatch):
        site, cache = self._installed(tmp_path)
        monkeypatch.setattr(up, "read_cached_library_index", lambda: (_ for _ in ()).throw(
            AssertionError("must not read the cache when PYMCU_UPSTREAM_INDEX is set")))
        monkeypatch.setenv("PYMCU_UPSTREAM_INDEX", str(cache))

        includes, skipped, errors, warned = up.resolve_upstream_for_target(
            search_path=[str(site)], flavors=["circuitpython"],
            stage_root=tmp_path / "dist" / "_upstream")

        assert errors == []
        assert warned == []
        assert len(includes) == 1

    def test_fetched_index_bypasses_an_empty_cache(self, tmp_path, monkeypatch):
        site, _ = self._installed(tmp_path)
        monkeypatch.setattr(up, "read_cached_library_index", lambda: (_ for _ in ()).throw(
            AssertionError("the fetched index must be used directly")))

        includes, skipped, errors, warned = up.resolve_upstream_for_target(
            search_path=[str(site)], flavors=["circuitpython"],
            stage_root=tmp_path / "dist" / "_upstream", index=_index())

        assert errors == []
        assert skipped == []
        assert warned == []
        assert len(includes) == 1


class TestFallbackStaging:
    def test_exposes_an_imported_module_and_warns_once_for_its_distribution(
            self, tmp_path):
        site = tmp_path / "site-packages"
        site.mkdir()
        _write_modules_dist(
            site, distribution="example-drivers", version="1.0",
            modules=("sensor_a", "sensor_b"),
        )
        main = tmp_path / "main.py"
        main.write_text("import sensor_a\nimport sensor_b\n")

        includes, skipped, errors, warned = up.resolve_upstream_for_target(
            search_path=[str(site)], flavors=[],
            stage_root=tmp_path / "_upstream", index={},
            entry_point=main, earlier_roots=[str(tmp_path)],
        )

        assert skipped == errors == []
        assert warned == ["example-drivers"]
        assert len(includes) == 1
        staged = Path(includes[0])
        assert (staged / "sensor_a.py").is_file()
        assert (staged / "sensor_b.py").is_file()

    def test_blinka_modules_are_derived_from_metadata_and_excluded(self, tmp_path):
        site = tmp_path / "site-packages"
        site.mkdir()
        _write_modules_dist(
            site, distribution="Adafruit-Blinka", version="9.0",
            modules=("board", "blinka_test_only"),
        )
        found = up._installed_distributions([str(site)])
        assert up.excluded_module_names(found) == {"board", "blinka_test_only"}

        main = tmp_path / "main.py"
        main.write_text("import blinka_test_only\n")
        includes, _, _, warned = up.resolve_upstream_for_target(
            search_path=[str(site)], flavors=[], stage_root=tmp_path / "_upstream",
            index={}, entry_point=main, earlier_roots=[str(tmp_path)],
        )
        assert includes == []
        assert warned == []

    def test_compat_layer_wins_over_an_unindexed_distribution(self, tmp_path):
        site = tmp_path / "site-packages"
        site.mkdir()
        _write_modules_dist(
            site, distribution="host-digitalio", version="1.0",
            modules=("digitalio",),
        )
        compat = tmp_path / "compat"
        compat.mkdir()
        (compat / "digitalio.py").write_text("VALUE = 1\n")
        main = tmp_path / "main.py"
        main.write_text("from digitalio import DigitalInOut\n")

        includes, _, _, warned = up.resolve_upstream_for_target(
            search_path=[str(site)], flavors=["circuitpython"],
            stage_root=tmp_path / "_upstream", index={}, entry_point=main,
            earlier_roots=[str(tmp_path), str(compat)],
        )

        assert includes == []
        assert warned == []

    def test_indexed_distribution_uses_only_index_modules_without_warning(
            self, tmp_path):
        site = tmp_path / "site-packages"
        site.mkdir()
        _write_modules_dist(
            site, distribution="example-drivers", version="1.0",
            modules=("indexed_driver", "undeclared_sibling"),
        )
        index = _index(
            distribution="example-drivers", version="1.0",
            provides=("indexed_driver",), layer="native",
        )
        main = tmp_path / "main.py"
        main.write_text("import indexed_driver\n")

        includes, _, errors, warned = up.resolve_upstream_for_target(
            search_path=[str(site)], flavors=[], stage_root=tmp_path / "_upstream",
            index=index, entry_point=main, earlier_roots=[str(tmp_path)],
        )

        assert errors == warned == []
        assert len(includes) == 1
        staged = Path(includes[0])
        assert (staged / "indexed_driver.py").is_file()
        assert not (staged / "undeclared_sibling.py").exists()

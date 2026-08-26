# tests/driver/test_project_config.py
#
# Tests for reading and writing [tool.pymcu]. The file belongs to the user, so
# what is asserted here is as much about what stays untouched as about what
# changes.

from pathlib import Path

import tomlkit

from src.driver.core import project_config as cfg


PROJECT = """\
# Kept by hand -- this comment must survive every write.
[project]
name = "demo"
version = "0.1.0"
dependencies = ["pymcu-stdlib>=0.1.0a5"]

[tool.pymcu]
board = "arduino_uno"
frequency = 16000000
sources = "src"
entry = "main.py"
"""


def _project(tmp_path: Path, body: str = PROJECT):
    path = tmp_path / "pyproject.toml"
    path.write_text(body)
    return path, tomlkit.loads(path.read_text())


class TestDescribe:
    def test_reads_the_board_and_what_it_implies(self, tmp_path):
        path, doc = _project(tmp_path)
        settings = cfg.describe(doc, tmp_path)
        assert settings["board"] == "arduino_uno"
        assert settings["chip"] == "atmega328p"
        assert settings["toolchain"] == "avr"
        assert settings["programmer"] == "avrdude"
        assert settings["layer"] == "native"
        assert settings["frequency"] == 16_000_000
        assert settings["frequency_explicit"] is True

    def test_a_project_on_a_bare_target_has_no_board(self, tmp_path):
        path, doc = _project(tmp_path, '[tool.pymcu]\ntarget = "attiny85"\n')
        settings = cfg.describe(doc, tmp_path)
        assert settings["board"] == ""
        assert settings["target"] == "attiny85"
        assert settings["chip"] == "attiny85"

    def test_missing_sources_are_reported(self, tmp_path):
        path, doc = _project(tmp_path)
        settings = cfg.describe(doc, tmp_path)
        assert settings["sources_exist"] is False
        assert settings["entry_exists"] is False

        (tmp_path / "src").mkdir()
        (tmp_path / "src" / "main.py").write_text("def main():\n    pass\n")
        settings = cfg.describe(tomlkit.loads(path.read_text()), tmp_path)
        assert settings["sources_exist"] and settings["entry_exists"]


class TestApplyChanges:
    def test_the_rest_of_the_file_is_left_alone(self, tmp_path):
        path, doc = _project(tmp_path)
        cfg.apply_changes(path, doc, layer="micropython")
        text = path.read_text()
        assert "# Kept by hand" in text
        assert 'name = "demo"' in text
        assert 'stdlib = ["micropython"]' in text

    def test_choosing_a_board_brings_its_clock(self, tmp_path):
        """
        A frequency left over from another board compiles, and every delay is
        wrong -- so the board's own clock follows it in.
        """
        path, doc = _project(tmp_path)
        result = cfg.apply_changes(path, doc, board="raspberry_pi_pico")
        assert result.ok
        assert result.changed["chip"] == "rp2040"
        assert result.changed["frequency"] == 125_000_000
        assert "frequency = 125000000" in path.read_text()

    def test_an_explicit_frequency_wins_over_the_board_default(self, tmp_path):
        path, doc = _project(tmp_path)
        cfg.apply_changes(path, doc, board="raspberry_pi_pico", frequency=48_000_000)
        assert "frequency = 48000000" in path.read_text()

    def test_setting_a_board_clears_target(self, tmp_path):
        """The build refuses a project that declares both."""
        path, doc = _project(tmp_path, '[tool.pymcu]\ntarget = "attiny85"\n')
        cfg.apply_changes(path, doc, board="arduino_uno")
        text = path.read_text()
        assert 'board = "arduino_uno"' in text
        assert "target" not in text

    def test_going_native_removes_the_layer(self, tmp_path):
        path, doc = _project(tmp_path, PROJECT + 'stdlib = ["micropython"]\n')
        cfg.apply_changes(path, doc, layer="native")
        # The key itself, not the substring: the dependency list mentions
        # pymcu-stdlib and must survive.
        settings = cfg.describe(tomlkit.loads(path.read_text()), tmp_path)
        assert settings["layers"] == []
        assert settings["layer"] == "native"
        assert "pymcu-stdlib" in path.read_text()

    def test_unknown_board_is_refused_without_writing(self, tmp_path):
        path, doc = _project(tmp_path)
        before = path.read_text()
        result = cfg.apply_changes(path, doc, board="teensy41")
        assert not result.ok and "teensy41" in result.message
        assert path.read_text() == before

    def test_a_refused_board_offers_the_name_it_was_close_to(self, tmp_path):
        # The layer refusal in the same function lists what it will accept; this one named
        # the board and stopped, and `uno` for `arduino_uno` is the miss people write.
        path, doc = _project(tmp_path)
        result = cfg.apply_changes(path, doc, board="uno")
        assert not result.ok
        assert "Did you mean 'arduino_uno'?" in result.message

    def test_a_refused_board_with_nothing_close_points_at_the_listing(self, tmp_path):
        path, doc = _project(tmp_path)
        result = cfg.apply_changes(path, doc, board="teensy41")
        assert "Did you mean" not in result.message
        assert "pymcu boards" in result.message

    def test_a_board_a_layer_supplies_is_still_resolved(self, tmp_path, monkeypatch):
        """
        The invariant on the line that was edited. This setter reads the merged table, and a
        board that only a compat layer declares has to keep resolving through it.
        """
        monkeypatch.setattr(
            "src.driver.core.boards.load_extension_board_chips",
            lambda flavor: {"acme_board": "atmega328p"} if flavor == "micropython" else {},
        )
        path, doc = _project(
            tmp_path,
            '[tool.pymcu]\nboard = "arduino_uno"\nstdlib = ["micropython"]\n')
        result = cfg.apply_changes(path, doc, board="acme_board")
        assert result.ok
        assert result.changed["chip"] == "atmega328p"

    def test_unknown_layer_is_refused_without_writing(self, tmp_path):
        path, doc = _project(tmp_path)
        before = path.read_text()
        result = cfg.apply_changes(path, doc, layer="arduino")
        assert not result.ok
        assert path.read_text() == before

    def test_a_frequency_of_zero_is_refused(self, tmp_path):
        path, doc = _project(tmp_path)
        result = cfg.apply_changes(path, doc, frequency=0)
        assert not result.ok and "positive" in result.message

    def test_no_arguments_changes_nothing(self, tmp_path):
        path, doc = _project(tmp_path)
        before = path.read_text()
        result = cfg.apply_changes(path, doc)
        assert result.ok and result.changed == {}
        assert path.read_text() == before


class TestAvailableBoards:
    def test_groups_carry_the_chip_and_clock(self):
        groups = cfg.available_boards([])
        assert groups
        arduino = next(g for g in groups if g["group"] == "Arduino")
        uno = next(b for b in arduino["boards"] if b["name"] == "arduino_uno")
        assert uno["chip"] == "atmega328p"
        assert uno["frequency"] == 16_000_000

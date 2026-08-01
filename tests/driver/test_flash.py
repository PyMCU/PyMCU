# tests/driver/test_flash.py
#
# Tests for the `pymcu flash` command: artifact selection per target family and
# programmer dispatch.  No programmer is ever installed or run — the programmer
# lookup is replaced by a recording fake.

from pathlib import Path
import pytest
from typer.testing import CliRunner
from src.driver.main import app

runner = CliRunner()


class FakeProgrammer:
    """Records what the driver hands to a programmer plugin."""

    firmware_artifacts = None  # set per-test to exercise the plugin override

    def __init__(self):
        self.flashed: list[tuple[Path, str, str | None, int | None]] = []
        self.raises: Exception | None = None

    def is_cached(self) -> bool:
        return True

    def install(self):  # pragma: no cover — is_cached() is always True here
        raise AssertionError("install() must not be called when is_cached() is True")

    def flash(self, hex_file, chip, *, port=None, baud=None):
        self.flashed.append((Path(hex_file), chip, port, baud))
        if self.raises is not None:
            raise self.raises


@pytest.fixture
def fake_programmer(monkeypatch):
    """Patch get_programmer() and return (programmer, requested_names)."""
    prog = FakeProgrammer()
    requested: list[str] = []

    def _get_programmer(name, console):
        requested.append(name)
        return prog

    monkeypatch.setattr("src.driver.commands.flash.get_programmer", _get_programmer)
    prog.requested = requested
    return prog


def _project(tmp_path, monkeypatch, pymcu_toml: str, artifacts: tuple[str, ...] = ()):
    (tmp_path / "pyproject.toml").write_text(pymcu_toml)
    if artifacts:
        (tmp_path / "dist").mkdir(exist_ok=True)
        for name in artifacts:
            (tmp_path / "dist" / name).write_bytes(b"\x00")
    monkeypatch.chdir(tmp_path)
    return tmp_path


def _invoke_flash(*args: str):
    return runner.invoke(app, ["flash"] + list(args), catch_exceptions=False)


AVR_TOML = '[tool.pymcu]\ntarget = "atmega328p"\n'
PICO_TOML = '[tool.pymcu]\nboard = "raspberry_pi_pico"\n'
PICO2_TOML = '[tool.pymcu]\ntarget = "rp2350"\n'


# ---------------------------------------------------------------------------
# Artifact selection per target family
# ---------------------------------------------------------------------------

class TestArtifactSelection:
    def test_avr_flashes_the_hex(self, tmp_path, monkeypatch, fake_programmer):
        _project(tmp_path, monkeypatch, AVR_TOML, ("firmware.hex",))
        result = _invoke_flash()
        assert result.exit_code == 0
        assert fake_programmer.flashed[0][0].name == "firmware.hex"

    def test_rp2040_flashes_the_bin(self, tmp_path, monkeypatch, fake_programmer):
        # ARM builds emit a flat flash image, not Intel HEX.
        _project(tmp_path, monkeypatch, PICO_TOML, ("firmware.bin",))
        result = _invoke_flash()
        assert result.exit_code == 0
        assert fake_programmer.flashed[0][0].name == "firmware.bin"
        assert fake_programmer.flashed[0][1] == "rp2040"

    def test_rp2350_prefers_uf2_over_bin(self, tmp_path, monkeypatch, fake_programmer):
        _project(tmp_path, monkeypatch, PICO2_TOML, ("firmware.bin", "firmware.uf2"))
        result = _invoke_flash()
        assert result.exit_code == 0
        assert fake_programmer.flashed[0][0].name == "firmware.uf2"

    def test_rp2040_without_bin_reports_the_arm_artifacts(
        self, tmp_path, monkeypatch, fake_programmer
    ):
        # A stale AVR hex must not be mistaken for an ARM firmware image.
        _project(tmp_path, monkeypatch, PICO_TOML, ("firmware.hex",))
        result = _invoke_flash()
        assert result.exit_code == 1
        assert "firmware.uf2" in result.output
        assert "firmware.bin" in result.output
        assert fake_programmer.flashed == []

    def test_avr_without_hex_exits_1(self, tmp_path, monkeypatch, fake_programmer):
        _project(tmp_path, monkeypatch, AVR_TOML, ("firmware.bin",))
        result = _invoke_flash()
        assert result.exit_code == 1
        assert "firmware.hex" in result.output

    def test_programmer_may_declare_its_own_artifacts(
        self, tmp_path, monkeypatch, fake_programmer
    ):
        # A plugin that flashes something else entirely overrides the family default.
        fake_programmer.firmware_artifacts = ("firmware.elf",)
        _project(tmp_path, monkeypatch, PICO_TOML, ("firmware.bin", "firmware.elf"))
        result = _invoke_flash()
        assert result.exit_code == 0
        assert fake_programmer.flashed[0][0].name == "firmware.elf"


# ---------------------------------------------------------------------------
# Programmer dispatch
# ---------------------------------------------------------------------------

class TestProgrammerDispatch:
    def test_avr_defaults_to_avrdude(self, tmp_path, monkeypatch, fake_programmer):
        _project(tmp_path, monkeypatch, AVR_TOML, ("firmware.hex",))
        assert _invoke_flash().exit_code == 0
        assert fake_programmer.requested == ["avrdude"]

    def test_rp_board_dispatches_to_the_rp2040_plugin(
        self, tmp_path, monkeypatch, fake_programmer
    ):
        _project(tmp_path, monkeypatch, PICO_TOML, ("firmware.bin",))
        assert _invoke_flash().exit_code == 0
        assert fake_programmer.requested == ["rp2040"]

    def test_flash_section_overrides_the_default(
        self, tmp_path, monkeypatch, fake_programmer
    ):
        toml = AVR_TOML + '\n[tool.pymcu.flash]\nprogrammer = "pk2cmd"\nport = "/dev/ttyX"\nbaud = 57600\n'
        _project(tmp_path, monkeypatch, toml, ("firmware.hex",))
        assert _invoke_flash().exit_code == 0
        assert fake_programmer.requested == ["pk2cmd"]
        assert fake_programmer.flashed[0][2:] == ("/dev/ttyX", 57600)

    def test_cli_port_wins_over_config(self, tmp_path, monkeypatch, fake_programmer):
        toml = AVR_TOML + '\n[tool.pymcu.flash]\nport = "/dev/ttyX"\n'
        _project(tmp_path, monkeypatch, toml, ("firmware.hex",))
        assert _invoke_flash("--port", "/dev/ttyCLI").exit_code == 0
        assert fake_programmer.flashed[0][2] == "/dev/ttyCLI"

    def test_legacy_programmer_section_still_works_with_a_warning(
        self, tmp_path, monkeypatch, fake_programmer
    ):
        # Projects scaffolded before 0.15 carry [tool.pymcu.programmer].
        toml = AVR_TOML + '\n[tool.pymcu.programmer]\nname = "pk2cmd"\n'
        _project(tmp_path, monkeypatch, toml, ("firmware.hex",))
        result = _invoke_flash()
        assert result.exit_code == 0
        assert fake_programmer.requested == ["pk2cmd"]
        assert "deprecated" in result.output.lower()
        assert "tool.pymcu.flash" in result.output

    def test_flash_section_wins_over_the_legacy_one(
        self, tmp_path, monkeypatch, fake_programmer
    ):
        toml = (
            AVR_TOML
            + '\n[tool.pymcu.flash]\nprogrammer = "avrdude"\n'
            + '\n[tool.pymcu.programmer]\nname = "pk2cmd"\n'
        )
        _project(tmp_path, monkeypatch, toml, ("firmware.hex",))
        result = _invoke_flash()
        assert result.exit_code == 0
        assert fake_programmer.requested == ["avrdude"]
        assert "deprecated" not in result.output.lower()

    def test_unknown_programmer_exits_1(self, tmp_path, monkeypatch):
        monkeypatch.setattr(
            "src.driver.commands.flash.get_programmer", lambda name, console: None
        )
        toml = AVR_TOML + '\n[tool.pymcu.flash]\nprogrammer = "nope"\n'
        _project(tmp_path, monkeypatch, toml, ("firmware.hex",))
        result = _invoke_flash()
        assert result.exit_code == 1
        assert "unknown programmer" in result.output.lower()


# ---------------------------------------------------------------------------
# End-to-end with the real rp2040 plugin (skipped when pymcu-arm is absent)
# ---------------------------------------------------------------------------

class TestRealRp2040Plugin:
    def test_uf2_is_copied_to_the_bootsel_volume(self, tmp_path, monkeypatch):
        # The whole ARM path with no mock programmer: `pymcu flash` must pick
        # dist/firmware.uf2 over the .bin and hand it to the plugin, which
        # drag-and-drops it onto the RPI-RP2 volume when picotool is absent.
        pytest.importorskip("pymcu.programmer.rp2040")
        from pymcu.programmer.rp2040 import Rp2040Programmer

        volume = tmp_path / "RPI-RP2"
        volume.mkdir()
        (volume / "INFO_UF2.TXT").write_text("Model: Raspberry Pi RP2\n")
        monkeypatch.setattr("shutil.which", lambda cmd: None)
        monkeypatch.setattr(
            Rp2040Programmer, "find_uf2_volume", staticmethod(lambda: volume)
        )

        _project(tmp_path, monkeypatch, PICO_TOML, ("firmware.bin", "firmware.uf2"))
        result = _invoke_flash()

        assert result.exit_code == 0
        assert (volume / "firmware.uf2").exists()
        assert not (volume / "firmware.bin").exists()


# ---------------------------------------------------------------------------
# Failure reporting
# ---------------------------------------------------------------------------

class TestFlashFailures:
    def test_no_pyproject_exits_1(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        result = _invoke_flash()
        assert result.exit_code == 1
        assert "pyproject.toml" in result.output

    def test_no_target_or_board_exits_1(self, tmp_path, monkeypatch):
        _project(tmp_path, monkeypatch, "[tool.pymcu]\nfrequency = 16000000\n")
        result = _invoke_flash()
        assert result.exit_code == 1
        assert "no 'target' or 'board'" in result.output.lower()

    def test_programmer_runtime_error_is_reported(
        self, tmp_path, monkeypatch, fake_programmer
    ):
        fake_programmer.raises = RuntimeError("device not responding")
        _project(tmp_path, monkeypatch, AVR_TOML, ("firmware.hex",))
        result = _invoke_flash()
        assert result.exit_code == 1
        assert "flash failed" in result.output.lower()

    def test_programmer_os_error_is_reported_as_a_flash_failure(
        self, tmp_path, monkeypatch, fake_programmer
    ):
        # e.g. the rp2040 plugin raising FileNotFoundError for a missing .uf2.
        fake_programmer.raises = FileNotFoundError("expected a .uf2 file")
        _project(tmp_path, monkeypatch, PICO_TOML, ("firmware.bin",))
        result = _invoke_flash()
        assert result.exit_code == 1
        assert "flash failed" in result.output.lower()

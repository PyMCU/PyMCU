# tests/driver/test_driver_ux.py
#
# Messages and numbers the user reads, all from the Windows 11 ARM report:
# advice that did not apply under pipx, help text rich had eaten, an install
# tip that ignored Windows, an unattended download policy, and a flash figure
# that disagreed with the published one.

from pathlib import Path
from unittest.mock import patch

import pytest
from rich.console import Console
from typer.testing import CliRunner

from src.driver.commands.build import _install_deps_hint, _parse_hex_flash_bytes
from src.driver.main import app
from src.driver.programmers.avrdude import AvrdudeProgrammer

runner = CliRunner()


class TestInstallHint:
    """Advice has to point at the project's environment, not the CLI's."""

    def test_uv_project(self, tmp_path):
        (tmp_path / "uv.lock").touch()
        assert _install_deps_hint(tmp_path) == "uv sync"

    def test_poetry_project(self, tmp_path):
        (tmp_path / "poetry.lock").touch()
        with patch("shutil.which", return_value=None):
            assert _install_deps_hint(tmp_path) == "poetry install"

    def test_pip_project(self, tmp_path):
        (tmp_path / "requirements.txt").touch()
        with patch("shutil.which", return_value=None):
            assert _install_deps_hint(tmp_path) == "pip install -r requirements.txt"

    def test_never_suggests_installing_into_the_cli(self, tmp_path):
        # `pip install pymcu-micropython` was the old advice. Under pipx it
        # installs into neither the CLI venv nor the project's, so the build
        # failed exactly the same way afterwards.
        (tmp_path / "requirements.txt").touch()
        for layout in ("uv.lock", "poetry.lock", "requirements.txt"):
            (tmp_path / layout).touch()
            assert "pymcu-micropython" not in _install_deps_hint(tmp_path)


class TestHelpMarkup:
    """Rich treats [tool.pymcu...] as a tag and silently drops it."""

    @pytest.mark.parametrize(("command", "expected"), [
        ("flash", "[tool.pymcu.flash]"),
        ("sync", "[tool.pymcu]"),
    ])
    def test_config_table_names_survive_rendering(self, command, expected):
        result = runner.invoke(app, [command, "--help"])
        assert expected in result.output, result.output

    def test_flash_help_keeps_both_mentions(self):
        result = runner.invoke(app, ["flash", "--help"])
        assert result.output.count("[tool.pymcu.flash]") >= 2


class TestAvrdudeInstallTip:
    def test_names_a_package_manager_for_each_platform(self):
        import src.driver.programmers.avrdude as mod
        source = Path(mod.__file__).read_text()
        for manager in ("winget install avrdude", "brew install avrdude",
                        "apt install avrdude"):
            assert manager in source, manager


class TestUnattendedDownloadPolicy:
    """An unwatched download is only acceptable if the bytes can be checked."""

    def _programmer(self):
        return AvrdudeProgrammer(Console(quiet=True))

    def test_refuses_without_a_hash(self):
        programmer = self._programmer()
        asset = {"url": "https://example.invalid/x.tar.gz", "hash": "PLACEHOLDER",
                 "archive_type": "tar.gz", "bin_path": "avrdude"}
        with patch.object(AvrdudeProgrammer, "_get_platform_info", return_value=asset), \
             patch("src.driver.core.base_tool._is_non_interactive", return_value=True):
            with pytest.raises(RuntimeError, match="cannot be verified"):
                programmer.install()

    def test_proceeds_when_the_asset_is_pinned(self):
        # Reaches the download instead of bailing; the download itself is stubbed.
        programmer = self._programmer()
        with patch("src.driver.core.base_tool._is_non_interactive", return_value=True), \
             patch.object(AvrdudeProgrammer, "is_cached", return_value=False), \
             patch.object(AvrdudeProgrammer, "_download_file",
                          side_effect=RuntimeError("reached the download")) as dl:
            with pytest.raises(RuntimeError, match="reached the download"):
                programmer.install()
        assert dl.called

    def test_every_shipped_asset_can_be_verified(self):
        for os_key, assets in AvrdudeProgrammer.METADATA["platforms"].items():
            for arch, info in assets.items():
                assert info["hash"].lower() != "placeholder", f"{os_key}/{arch}"


class TestFlashMetric:
    """
    The driver subtracts the constant startup preamble so the figure reflects
    user code. It counted 106 bytes -- the 26-slot vector table plus
    __bad_interrupt -- while the published disassembly counts only the table,
    treating __bad_interrupt as the code it is. That made the tool report 36
    where the article says 38.
    """

    def _hex(self, tmp_path: Path, data_bytes: int) -> Path:
        # Minimal Intel HEX: enough type-00 records to carry data_bytes.
        lines = []
        remaining, addr = data_bytes, 0
        while remaining:
            n = min(16, remaining)
            payload = "00" * n
            lines.append(f":{n:02X}{addr:04X}00{payload}00")
            addr += n
            remaining -= n
        lines.append(":00000001FF")
        path = tmp_path / "firmware.hex"
        path.write_text("\n".join(lines))
        return path

    def test_only_the_vector_table_is_deducted(self, tmp_path):
        assert _parse_hex_flash_bytes(self._hex(tmp_path, 104 + 38)) == 38

    def test_the_published_blink_figure_is_reproduced(self, tmp_path):
        # 142 bytes of image is what the gist's blink assembles to.
        assert _parse_hex_flash_bytes(self._hex(tmp_path, 142)) == 38

    def test_an_image_smaller_than_the_table_is_not_negative(self, tmp_path):
        assert _parse_hex_flash_bytes(self._hex(tmp_path, 64)) == 64

    def test_unreadable_file_reports_zero(self, tmp_path):
        assert _parse_hex_flash_bytes(tmp_path / "missing.hex") == 0

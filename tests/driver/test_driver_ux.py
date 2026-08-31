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

from src.driver.commands.build import _flash_report_lines, _install_deps_hint, _parse_hex_flash_bytes
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
    What gets reported after a build, and why it is two numbers now.

    The driver used to subtract the 104-byte AVR preamble and present the
    remainder as "N of 32768 bytes of program storage". That under-reported
    occupancy: the whole image is what the programmer writes, so avrdude and
    PyMCU disagreed by 104 bytes on every AVR build. The image is now the
    headline figure and the preamble gets named on its own line, which keeps
    the number the published write-ups quote without claiming it is occupancy.
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

    def test_the_whole_image_is_counted(self, tmp_path):
        # No deduction here: this is what lands on the chip.
        assert _parse_hex_flash_bytes(self._hex(tmp_path, 104 + 38)) == 142

    def test_the_published_blink_figure_is_reproduced(self, tmp_path):
        # 142 bytes of image is what the gist's blink assembles to. The article quotes the
        # split as 38 + 104; the total is right and the split was two bytes out, so it is
        # 40 + 102 here. See the note below for why.
        lines = _flash_report_lines(142, 32768, "atmega328p")
        assert "142 / 32768" in lines[0]
        assert "40 bytes of your code" in lines[1]
        assert "102 bytes of interrupt vector table" in lines[1]

    def test_the_scaffold_blink_figure_is_reproduced(self, tmp_path):
        # The scaffold uses value(1)/value(0) rather than toggle(): 150 total, 48 + 102.
        lines = _flash_report_lines(150, 32768, "atmega328p")
        assert "150 / 32768" in lines[0]
        assert "48 bytes of your code" in lines[1]

    # Those two call the three-argument form, which has no assembly to read and falls back to
    # the constant. The constant is the ATmega328P's table and it is 102, not 104:
    #
    #     the assembler pads each slot out to the stride EXCEPT the last, because nothing
    #     follows it, so 26 four-byte slots occupy 25*4 + 2 = 102
    #
    # Verified against the linked ELF, where `__bad_interrupt` is at 0x66 on the ATmega328P
    # and at 0x34 on the ATtiny13. The ATtiny figures were already exact, because there the
    # slot IS an RJMP and the padding question does not arise.
    #
    # The two bytes were code being charged to the table. Totals do not move, so the article's
    # 142 and 150 still hold and only the split does: 38 + 104 -> 40 + 102, 46 + 104 ->
    # 48 + 102.

    # --- the table is measured, not assumed ---------------------------------
    #
    # A vector-table slot is 4 bytes on the parts with JMP/CALL and 2 on the parts without,
    # so the 104-byte constant is the ATmega's table and double every ATtiny's. The guard is
    # `startswith("at")`, which is both families, and an attiny13 was told it had "8 bytes of
    # your code + 104 bytes of interrupt vector table" for a 112-byte image whose table is 52
    # and whose code is 60. Two wrong numbers, and the smaller one is the one a reader would
    # act on. Issue #235.
    #
    # Of the four below, two DISCRIMINATE: the attiny table and the shorter table, which give
    # 52 and 20 where the constant gives 104. The two ATmega ones cannot, because they need
    # the fourth argument to exist, so against the unfixed driver they fail on the signature
    # rather than on the answer. What holds the ATmega end on both sides is the pair of
    # published-figure tests above, which call the three-argument form and must keep passing.

    @staticmethod
    def _asm(tmp_path, stride: int, slots: int):
        """A vector table as the backend emits it: `.org` per slot, then __bad_interrupt."""
        body = "".join(f".org 0x{i * stride:X}\n\tRJMP\t__bad_interrupt\n"
                       for i in range(slots))
        (tmp_path / "firmware.gas.asm").write_text(
            body + "\n__bad_interrupt:\n\tRJMP\tmain\nmain:\n\tCLR\tR1\n")
        return tmp_path

    def test_an_attiny_table_is_read_from_the_assembly(self, tmp_path):
        d = self._asm(tmp_path, stride=2, slots=26)      # 2-byte slots: 52 bytes
        lines = _flash_report_lines(112, 1024, "attiny13", d)
        assert "52 bytes of interrupt vector table" in lines[1]
        assert "60 bytes of your code" in lines[1]

    def test_an_atmega_table_is_read_and_the_last_slot_is_not_padded(self, tmp_path):
        # 25 padded 4-byte slots plus the last slot's RJMP: 102, not 26 x 4. Checked against
        # the linked ELF, where __bad_interrupt sits at 0x66.
        d = self._asm(tmp_path, stride=4, slots=26)
        lines = _flash_report_lines(150, 32768, "atmega328p", d)
        assert "102 bytes of interrupt vector table" in lines[1]
        assert "48 bytes of your code" in lines[1]

    def test_a_shorter_table_is_followed_rather_than_assumed(self, tmp_path):
        """pymcu-avr#16 will cut the slot COUNT per part. The report has to follow it.

        This is what says the fix reads the table rather than swapping one constant for two:
        with ten slots the answer is 20, which neither 104 nor 52 would give.
        """
        d = self._asm(tmp_path, stride=2, slots=10)
        lines = _flash_report_lines(112, 1024, "attiny13", d)
        assert "20 bytes of interrupt vector table" in lines[1]

    def test_no_assembly_falls_back_to_the_atmegas_real_table(self, tmp_path):
        """A caller with no artifacts gets the constant, and the constant is 102.

        Unreachable from a real build, which always has the assembly. It is pinned anyway
        because the constant is the one place a reader looks up "how big is the table", and
        104 is the figure this issue exists to refute.
        """
        lines = _flash_report_lines(150, 32768, "atmega328p", tmp_path)
        assert "102 bytes of interrupt vector table" in lines[1]

    def test_pic_images_lose_nothing(self, tmp_path):
        # A PIC14 image has no AVR vector table. Deducting 104 from one
        # under-reported its size by exactly that much.
        assert _parse_hex_flash_bytes(self._hex(tmp_path, 300)) == 300
        assert len(_flash_report_lines(300, 8192, "pic16f877a")) == 1

    def test_an_image_smaller_than_the_preamble_gets_no_breakdown(self, tmp_path):
        # Nothing sensible to split, and a negative "your code" would be absurd.
        assert len(_flash_report_lines(64, 32768, "atmega328p")) == 1

    def test_unreadable_file_reports_zero(self, tmp_path):
        assert _parse_hex_flash_bytes(tmp_path / "missing.hex") == 0

# tests/driver/test_toolchain_riscv.py
#
# Unit tests for the RISC-V toolchain plugin and the WCH-Link programmer.
# No real binaries are invoked: the external tools are mocked out.

from pathlib import Path
from unittest.mock import patch, MagicMock

import pytest

from rich.console import Console

pymcu_toolchain_riscv = pytest.importorskip(
    "pymcu.toolchain.riscv",
    reason="pymcu-backend-riscv (external backend) not installed",
)

from pymcu.toolchain.riscv import RiscvToolchainPlugin, WchLinkProgrammer
from pymcu.toolchain.riscv.riscvgas import RiscvGasToolchain
from src.driver.core.boards import (
    default_programmer,
    default_toolchain,
    firmware_artifacts,
)


@pytest.fixture
def console():
    return Console(quiet=True)


# ---------------------------------------------------------------------------
# Chip support
# ---------------------------------------------------------------------------

class TestSupports:
    def test_accepts_ch32v003(self):
        assert RiscvGasToolchain.supports("ch32v003")
        assert RiscvGasToolchain.supports("CH32V003")

    def test_accepts_generic_arch_names(self):
        assert RiscvGasToolchain.supports("riscv")
        assert RiscvGasToolchain.supports("rv32ec")

    def test_rejects_other_families(self):
        assert not RiscvGasToolchain.supports("atmega328p")
        assert not RiscvGasToolchain.supports("pic16f84a")
        assert not RiscvGasToolchain.supports("rp2040")

    def test_plugin_delegates_to_toolchain(self):
        assert RiscvToolchainPlugin.supports("ch32v003")
        assert not RiscvToolchainPlugin.supports("atmega328p")

    def test_plugin_metadata(self):
        assert RiscvToolchainPlugin.family == "riscv"
        assert RiscvToolchainPlugin.default_chip == "ch32v003"


# ---------------------------------------------------------------------------
# Driver wiring: a CH32V part must route to the RISC-V toolchain and WCH-Link
# ---------------------------------------------------------------------------

class TestDriverDefaults:
    def test_toolchain_for_ch32v(self):
        assert default_toolchain("ch32v003") == "riscv"

    def test_programmer_for_ch32v(self):
        assert default_programmer("ch32v003") == "wch-link"

    def test_flashable_artifacts_prefer_flat_image(self):
        # WCH-Link writes a raw image; the hex is only a fallback.
        assert firmware_artifacts("ch32v003") == ("firmware.bin", "firmware.hex")


# ---------------------------------------------------------------------------
# Toolchain identity and packaged data
# ---------------------------------------------------------------------------

class TestToolchain:
    def test_name_selects_the_build_pipeline(self, console):
        # build.py dispatches on this string.
        assert RiscvGasToolchain(console, "ch32v003").get_name() == "riscv-as"

    def test_linker_script_ships_with_the_package(self, console):
        script = RiscvGasToolchain(console, "ch32v003").packaged_linker_script()
        assert script.exists()
        text = script.read_text()
        # The startup code emitted by the backend depends on these symbols.
        for symbol in ("_sidata", "_sdata", "_edata", "_sbss", "_ebss"):
            assert symbol in text
        assert "0x20000000" in text   # RAM base

    def test_unknown_chip_is_rejected_with_a_useful_message(self, console):
        tc = RiscvGasToolchain(console, "ch32v999")
        with pytest.raises(RuntimeError, match="No RISC-V toolchain profile"):
            tc.packaged_linker_script()

    def test_ch32v003_uses_the_embedded_abi(self, console):
        # RV32EC must link as ilp32e; mixing ABIs is a hard link error.
        assert RiscvGasToolchain(console, "ch32v003")._mabi() == "ilp32e"

    def test_ch32v203_uses_the_full_abi(self, console):
        assert RiscvGasToolchain(console, "ch32v203")._mabi() == "ilp32"

    def test_ch32v203_has_its_own_linker_script(self, console):
        script = RiscvGasToolchain(console, "ch32v203").packaged_linker_script()
        assert script.name == "ch32v203.ld"
        text = script.read_text()
        assert "LENGTH = 64K" in text      # flash
        assert "LENGTH = 20K" in text      # RAM

    def test_both_wch_parts_are_supported(self):
        assert RiscvGasToolchain.supports("ch32v003")
        assert RiscvGasToolchain.supports("ch32v203")

    def test_missing_toolchain_reports_install_instructions(self, console):
        tc = RiscvGasToolchain(console, "ch32v003")
        with patch("shutil.which", return_value=None):
            assert tc.is_cached() is False
            with pytest.raises(RuntimeError, match="brew install riscv-gnu-toolchain"):
                tc.install()

    def test_assemble_passes_the_abi_and_no_march(self, console, tmp_path):
        # -march is deliberately omitted: the .asm carries .attribute arch.
        asm = tmp_path / "firmware.asm"
        asm.write_text("\t.text\n")
        tc = RiscvGasToolchain(console, "ch32v003")

        with patch.object(tc, "_find_bin", return_value="riscv32-unknown-elf-as"), \
             patch("pymcu.toolchain.riscv.riscvgas._run") as run:
            run.return_value = MagicMock(returncode=0)
            obj = tc.assemble(asm)

        cmd = run.call_args[0][0]
        assert "-mabi=ilp32e" in cmd
        assert not any(str(c).startswith("-march") for c in cmd)
        assert obj.name == "firmware.o"

    def test_assemble_surfaces_assembler_errors(self, console, tmp_path):
        asm = tmp_path / "firmware.asm"
        asm.write_text("\tbogus\n")
        tc = RiscvGasToolchain(console, "ch32v003")

        with patch.object(tc, "_find_bin", return_value="riscv32-unknown-elf-as"), \
             patch("pymcu.toolchain.riscv.riscvgas._run") as run:
            run.return_value = MagicMock(returncode=1, stderr=b"unknown opcode", stdout=b"")
            with pytest.raises(RuntimeError, match="unknown opcode"):
                tc.assemble(asm)

    def test_link_uses_elf32_emulation_and_the_packaged_script(self, console, tmp_path):
        obj = tmp_path / "firmware.o"
        obj.write_bytes(b"")
        tc = RiscvGasToolchain(console, "ch32v003")

        with patch.object(tc, "_find_bin", return_value="riscv32-unknown-elf-ld"), \
             patch("pymcu.toolchain.riscv.riscvgas._run") as run:
            run.return_value = MagicMock(returncode=0)
            elf = tc.link(obj, [], tmp_path)

        cmd = [str(c) for c in run.call_args[0][0]]
        assert "-m" in cmd and "elf32lriscv" in cmd
        assert "-T" in cmd
        assert cmd[cmd.index("-T") + 1].endswith("ch32v003.ld")
        assert elf.name == "firmware.elf"

    def test_bin_and_hex_conversions_use_the_right_format(self, console, tmp_path):
        elf = tmp_path / "firmware.elf"
        elf.write_bytes(b"")
        tc = RiscvGasToolchain(console, "ch32v003")

        with patch.object(tc, "_find_bin", return_value="riscv32-unknown-elf-objcopy"), \
             patch("pymcu.toolchain.riscv.riscvgas._run") as run:
            run.return_value = MagicMock(returncode=0)
            hex_file = tc.elf_to_hex(elf)
            hex_cmd = [str(c) for c in run.call_args[0][0]]
            bin_file = tc.elf_to_bin(elf)
            bin_cmd = [str(c) for c in run.call_args[0][0]]

        assert hex_file.name == "firmware.hex"
        assert hex_cmd[hex_cmd.index("-O") + 1] == "ihex"
        assert bin_file.name == "firmware.bin"
        assert bin_cmd[bin_cmd.index("-O") + 1] == "binary"

    def test_prefix_detection_falls_back_to_riscv64(self, console):
        # Homebrew ships riscv64-unknown-elf-* and only symlinks some riscv32 names.
        tc = RiscvGasToolchain(console, "ch32v003")

        def only_riscv64(name):
            return f"/usr/bin/{name}" if name.startswith("riscv64-unknown-elf-") else None

        with patch("shutil.which", side_effect=only_riscv64):
            assert tc.is_cached() is True
            assert tc._find_bin("as") == "/usr/bin/riscv64-unknown-elf-as"


# ---------------------------------------------------------------------------
# WCH-Link programmer
# ---------------------------------------------------------------------------

class TestWchLinkProgrammer:
    def test_name_and_artifacts(self, console):
        prog = WchLinkProgrammer(console)
        assert prog.get_name() == "wch-link"
        assert "firmware.bin" in prog.firmware_artifacts

    def test_reports_both_tools_when_none_installed(self, console):
        prog = WchLinkProgrammer(console)
        with patch("shutil.which", return_value=None):
            assert prog.is_cached() is False
            with pytest.raises(RuntimeError, match="wlink and minichlink"):
                prog.install()

    def test_wlink_command_shape(self):
        cmd = WchLinkProgrammer._build_command("wlink", Path("/tmp/firmware.bin"), "ch32v003")
        assert cmd[:2] == ["wlink", "flash"]
        assert "CH32V003" in cmd
        assert "/tmp/firmware.bin" in cmd

    def test_minichlink_command_writes_and_reboots(self):
        cmd = WchLinkProgrammer._build_command("minichlink", Path("/tmp/firmware.bin"), "ch32v003")
        assert cmd[0] == "minichlink"
        assert "-w" in cmd and "flash" in cmd
        assert "-b" in cmd   # reboot into the freshly written image

    def test_explicit_tool_is_honoured(self, console):
        prog = WchLinkProgrammer(console, tool="minichlink")
        with patch("shutil.which", side_effect=lambda n: "/usr/bin/minichlink" if n == "minichlink" else None):
            assert prog._resolve_tool() == "minichlink"

    def test_missing_image_is_reported_before_invoking_the_tool(self, console, tmp_path):
        prog = WchLinkProgrammer(console)
        with patch("shutil.which", return_value="/usr/bin/wlink"), \
             patch("subprocess.run") as run:
            with pytest.raises(RuntimeError, match="Firmware image not found"):
                prog.flash(tmp_path / "nope.bin", "ch32v003")
            run.assert_not_called()

    def test_flash_failure_surfaces_tool_output(self, console, tmp_path):
        image = tmp_path / "firmware.bin"
        image.write_bytes(b"\x00")
        prog = WchLinkProgrammer(console)

        with patch("shutil.which", return_value="/usr/bin/wlink"), \
             patch("subprocess.run") as run:
            run.return_value = MagicMock(returncode=1, stderr=b"no probe found", stdout=b"")
            with pytest.raises(RuntimeError, match="no probe found"):
                prog.flash(image, "ch32v003")

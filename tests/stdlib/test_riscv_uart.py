# tests/stdlib/test_riscv_uart.py
#
# print() on the CH32V003 goes through USART1: the HAL enables the clocks, routes PD5 as an
# alternate-function output, programs BRR from the build's frequency and polls TXE. The values
# are read off the generated assembly because a wrong address or divisor is invisible until a
# terminal shows noise. Running the firmware is done in CH32VSharp (the emulator) and in the
# oracle runner; here the assembly is pinned.

import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
BACKEND = REPO / "build" / "bin" / "pymcuc-riscv"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not (PYMCUC.exists() and BACKEND.exists()),
    reason="compiler binaries not built (run `just build` and `just build-backend-riscv`)",
)


def compile_program(tmp_path: Path, source: str, freq: int = 24_000_000):
    src = tmp_path / "main.py"
    src.write_text(source)
    mir = tmp_path / "firmware.mir"
    frontend = subprocess.run(
        [str(PYMCUC), str(src), "-o", str(tmp_path / "out.bin"), "--arch", "riscv",
         "--target", "ch32v003", "--freq", str(freq), "-I", str(STDLIB), "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    if "[BUILD_OK]" not in frontend.stdout:
        return frontend, None
    asm = tmp_path / "firmware.asm"
    backend = subprocess.run(
        [str(BACKEND), str(mir), "-o", str(asm), "--target", "ch32v003", "--arch", "riscv"],
        capture_output=True, text=True,
    )
    assert backend.returncode == 0, backend.stdout + backend.stderr
    return frontend, asm.read_text()


UART_MAIN = (
    "from pymcu.hal.uart import UART\n"
    "from pymcu.hal.console import print_str\n"
    "u = UART({baud})\n"
    "u.write(65)\n"
)


def test_the_uart_clocks_the_port_and_the_usart(tmp_path):
    _, asm = compile_program(tmp_path, UART_MAIN.format(baud=115200))

    assert "li\tt2, 0x40021018" in asm          # RCC_APB2PCENR
    assert "ori\tt0, t0, 32" in asm            # IOPDEN, bit 5
    assert "li\tt1, 16384" in asm              # USART1EN, bit 14


def test_the_baud_divisor_comes_from_the_build_frequency(tmp_path):
    _, asm = compile_program(tmp_path, UART_MAIN.format(baud=115200), freq=24_000_000)
    assert "li\tt0, 208" in asm                 # (24 MHz + 57600) // 115200
    _, asm = compile_program(tmp_path, UART_MAIN.format(baud=9600), freq=48_000_000)
    assert "li\tt0, 5000" in asm                # 48 MHz / 9600


def test_a_byte_is_written_to_datar_after_polling_txe(tmp_path):
    _, asm = compile_program(tmp_path, UART_MAIN.format(baud=115200))

    assert "li\tt2, 0x40013800" in asm          # USART1_STATR
    assert "li\tt2, 0x40013804" in asm          # USART1_DATAR


def test_pd5_becomes_an_alternate_function_output(tmp_path):
    _, asm = compile_program(tmp_path, UART_MAIN.format(baud=115200))

    assert "li\tt2, 0x40011400" in asm          # GPIOD_CFGLR
    # PD5's nibble is at bit 20: 0xB << 20 = 0xB00000 = 11534336
    assert "11534336" in asm


def test_a_frame_other_than_8n1_is_refused(tmp_path):
    frontend, asm = compile_program(
        tmp_path, "from pymcu.hal.uart import UART\nu = UART(9600, 7, 1, 1)\n")

    assert asm is None
    assert "8 data bits, no parity and one stop bit" in frontend.stdout + frontend.stderr


def test_print_of_a_string_compiles_without_the_user_naming_a_uart(tmp_path):
    # The driver injects the UART; the bare compiler needs the console import the injection adds.
    _, asm = compile_program(
        tmp_path,
        "from pymcu.hal.uart import UART as _s\nfrom pymcu.hal.console import print_str\n"
        "_s(115200)\nprint(\"hola\")\n")

    assert asm is not None
    assert "0x40013804" in asm                 # the characters go out through DATAR


def compile_for(tmp_path: Path, source: str, chip: str):
    src = tmp_path / "main.py"
    src.write_text(source)
    return subprocess.run(
        [str(PYMCUC), str(src), "-o", str(tmp_path / "out.bin"), "--arch", "riscv",
         "--target", chip, "--freq", "48000000", "-I", str(STDLIB),
         "--emit-ir", str(tmp_path / "firmware.mir")],
        capture_output=True, text=True,
    )


def test_a_part_without_a_uart_hal_still_builds_a_program_that_never_asks_for_one(tmp_path):
    # pymcu.hal re-exports the UART, so `from pymcu.hal.gpio import Pin` alone drags the UART
    # dispatcher into the build; refusing at the import would take down a blink for the V203.
    out = compile_for(
        tmp_path,
        "from pymcu.hal.gpio import Pin\n"
        "led = Pin(\"PA5\", Pin.OUT)\n"
        "led.high()\n",
        "ch32v203",
    )

    assert "[BUILD_OK]" in out.stdout, out.stdout + out.stderr


def test_a_part_without_a_uart_hal_refuses_where_the_program_uses_the_uart(tmp_path):
    out = compile_for(
        tmp_path,
        "from pymcu.hal.uart import UART\n"
        "u = UART(115200)\n"
        "u.write(65)\n",
        "ch32v203",
    )

    assert "[BUILD_OK]" not in out.stdout
    assert "CH32V003" in out.stdout + out.stderr


def test_the_uart_facade_offers_print_float_like_the_other_architectures(tmp_path):
    out = compile_for(
        tmp_path,
        "from pymcu.hal.uart import UART\n"
        "u = UART(115200)\n"
        "u.print_float(1.5)\n",
        "ch32v003",
    )

    assert "[BUILD_OK]" in out.stdout, out.stdout + out.stderr

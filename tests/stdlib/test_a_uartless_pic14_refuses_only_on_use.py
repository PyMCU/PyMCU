"""A PIC14 part without a USART must still compile a program that never asks for one.

`pymcu/hal/__init__.py` re-exports all five peripherals, so `from pymcu.hal.gpio import Pin`
reaches `pymcu.hal.uart` and, on a PIC14 target, `pymcu.hal.pic14.pic14_uart`. That dispatcher
used to answer a chip without a USART with a module-level `raise`, which fired on the import
rather than on a UART operation. A plain LED blink for the PIC16F84A therefore stopped
compiling over a peripheral it never mentions.

The refusal itself is right and stays. What moves is when it fires: at the first UART
operation, where the message lands next to what the program actually asked for.

The sibling PIC14 facades already work this way. `pic14/adc.py`, `pic14/pwm.py` and
`pic14/timer.py` dispatch inside the class body, so importing them on a part that lacks the
peripheral costs nothing and only a real use fails. The UART was the one that did not.
"""

import os
import re
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")

ERROR = re.compile(r"^(?P<path>[^\s:]+):\d+:\d+: error: (?P<message>.*)$", re.MULTILINE)

# Module-level programs, the way a board program is written.
BLINK = 'from pymcu.hal.gpio import Pin\n\nled = Pin("RB0", Pin.OUT)\nled.high()\n'

ASKS_FOR_A_UART = 'from pymcu.hal.uart import UART\n\nu = UART(9600)\nu.write(65)\n'

# What `pymcu build` puts in front of a program that calls print(), copied from
# _inject_print_preamble in src/driver/commands/build.py. This suite drives pymcuc
# directly, and pymcuc does not inject, so a bare print() here would compile clean and
# say nothing about the gate under test.
PRINTS = ('from pymcu.hal.uart import UART as _pymcu_stdout\n'
          'from pymcu.hal.console import print_str\n'
          '_pymcu_stdout(115200)\n'
          '\n'
          'print("hello")\n')


def _compile(tmp_path: Path, source: str, target: str):
    """Run the frontend and IR generation only. Returns (exit code, combined output).

    `--emit-ir` stops before codegen, which is what this test wants: pymcuc has no PIC
    backend of its own, and the refusal under test fires during IR generation anyway.
    """
    src = tmp_path / "main.py"
    src.write_text(source)
    proc = subprocess.run(
        [str(PYMCUC), str(src), "--target", target,
         "-I", str(tmp_path), "-I", str(STDLIB),
         "--emit-ir", str(tmp_path / "out.mir"), "-o", str(tmp_path / "out.bin")],
        capture_output=True, text=True, env=dict(os.environ),
    )
    return proc.returncode, proc.stdout + proc.stderr


def test_a_gpio_program_compiles_on_a_pic14_without_a_usart(tmp_path):
    """The regression. Nothing in this program names a UART, so nothing may refuse one."""
    rc, out = _compile(tmp_path, BLINK, "pic16f84a")

    assert "UART" not in out, (
        "a blink that never mentions a UART was refused over one:\n" + out)
    assert rc == 0, out
    assert (tmp_path / "out.mir").exists(), "IR generation must reach the end"


def test_the_same_program_still_compiles_where_the_usart_exists(tmp_path):
    """The control. If the 84A answer came from breaking the dispatcher, this one goes too."""
    rc, out = _compile(tmp_path, BLINK, "pic16f877a")

    assert rc == 0, out
    assert (tmp_path / "out.mir").exists()


def test_asking_for_a_uart_on_a_pic14_without_a_usart_is_still_refused(tmp_path):
    """The honest refusal is kept. Moving it must not turn it into silence or wrong code.

    The message is asserted on, not just the exit code, because the failure mode that matters
    here is a program that builds clean and drives registers the die does not have.
    """
    rc, out = _compile(tmp_path, ASKS_FOR_A_UART, "pic16f84a")

    assert rc != 0, "a UART on a chip that has none must not compile:\n" + out
    m = ERROR.search(out)
    assert m, f"expected a located diagnostic, got:\n{out}"
    assert "CompileError" in m.group("message")
    assert "UART" in m.group("message")
    assert "16F84A" in m.group("message"), (
        "the refusal has to name the part the reader is building for")


def test_calling_the_hal_primitive_directly_is_still_refused(tmp_path):
    """The hole a use-time refusal can leave, and the reason uart_write itself must raise.

    Reaching past UART into the chip HAL -- `from pymcu.hal.pic14.pic14_uart import
    uart_write` -- skips uart_init, so no constructor gates it. A first cut of this fix left
    uart_write inert so that uart_write_str in hal/uart_text.py could still be lowered, and
    this program built clean and assembled to firmware that drove nothing. Silent wrong code
    on a part with no USART is the one outcome worse than the blink that would not compile.

    What makes both possible is that hal/uart_text.py now takes its primitive from a sink of
    its own rather than from the dispatcher, so the writers it emits unconditionally do not
    drag the refusal in with them.
    """
    source = 'from pymcu.hal.pic14.pic14_uart import uart_write\n\nuart_write(65)\n'
    rc, out = _compile(tmp_path, source, "pic16f84a")

    assert rc != 0, "a direct HAL write on a chip with no USART must not compile:\n" + out
    assert "no hardware UART" in out, out
    assert not (tmp_path / "out.mir").exists(), "nothing may be emitted for it"


def test_printing_on_a_pic14_without_a_usart_is_still_refused(tmp_path):
    """print() reaches the refusal through the driver's injected UART(baud), not by accident.

    The widest path to real output, and the one a user is most likely to take, so it is worth
    pinning on its own rather than trusting that it shares a route with UART(9600).
    """
    rc, out = _compile(tmp_path, PRINTS, "pic16f84a")

    assert rc != 0, "print() with nowhere to write must not compile:\n" + out
    assert "no hardware UART" in out, (
        "print() has to hit the USART refusal, not something vaguer:\n" + out)

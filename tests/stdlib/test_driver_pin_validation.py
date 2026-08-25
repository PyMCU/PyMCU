"""A driver handed a pin it cannot drive must stop the build and say so.

Each of these drivers dispatches on a compile-time pin name and each supports a
subset of the chip's pins. Falling off the end of that dispatch used to be silent:
the sensors returned their own "no device" sentinel, so the whole protocol folded
away and the program printed the value a broken sensor prints; the servo left
Timer1 reconfigured and never wrote a compare register; the LCD simply never drove
the pin, and every byte reached the panel as a command. All four built clean.

NeoPixel already closed its dispatch with a CompileError naming the pins it
supports. These are the same shape, so they are tested the same way: the bad pin
must fail the build with a message that names the driver and the supported set,
and the good pin must still build.
"""

import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")


DS18B20 = (
    "from pymcu.drivers.ds18b20 import DS18B20\n\n\n"
    "def main():\n"
    '    s = DS18B20("{pin}")\n'
    "    v: int16 = s.read()\n"
    "    while True:\n"
    "        pass\n"
)
DHT11 = (
    "from pymcu.drivers.dht11 import DHT11\n\n\n"
    "def main():\n"
    '    s = DHT11("{pin}")\n'
    "    v: uint16 = s.read()\n"
    "    while True:\n"
    "        pass\n"
)
SERVO = (
    "from pymcu.hal.servo import Servo\n\n\n"
    "def main():\n"
    '    s = Servo("{pin}")\n'
    "    s.write(90)\n"
    "    while True:\n"
    "        pass\n"
)
LCD = (
    "from pymcu.drivers.lcd import LCD\n\n\n"
    "def main():\n"
    '    lcd = LCD(rs="{pin}", en="PD5", d4="PD6", d5="PD7", d6="PB0", d7="PB1")\n'
    "    lcd.init()\n"
    '    lcd.print_str("Hi")\n'
    "    while True:\n"
    "        pass\n"
)
PWM = (
    "from pymcu.hal.pwm import PWM\n\n\n"
    "def main():\n"
    '    p = PWM("{pin}", 128)\n'
    "    p.set_duty(64)\n"
    "    while True:\n"
    "        pass\n"
)
NEOPIXEL = (
    "from pymcu.drivers.neopixel import NeoPixel\n\n\n"
    "def main():\n"
    '    np = NeoPixel("{pin}", 1)\n'
    "    np.set_pixel(1, 2, 3)\n"
    "    while True:\n"
    "        pass\n"
)

# (id, program, a pin the driver drives, a pin it does not, what the message must say)
DRIVERS = [
    ("ds18b20",  DS18B20,  "PD2", "PB4", "DS18B20"),
    ("dht11",    DHT11,    "PD4", "PB0", "DHT11"),
    ("servo",    SERVO,    "PB1", "PB3", "Servo"),
    ("lcd",      LCD,      "PD4", "PA0", "LCD"),
    ("pwm",      PWM,      "PD6", "PC0", "PWM"),
    ("neopixel", NEOPIXEL, "PB0", "PC0", "NeoPixel"),
]


def build(tmp_path: Path, program: str, pin: str):
    src = tmp_path / "main.py"
    src.write_text(program.format(pin=pin))
    proc = subprocess.run(
        [str(PYMCUC), str(src), "-o", "/dev/null", "--target", "atmega328p",
         "--freq", "16000000", "-I", str(tmp_path), "-I", str(STDLIB),
         "--emit-ir", str(tmp_path / "firmware.mir")],
        capture_output=True, text=True,
    )
    return proc.stdout + proc.stderr


@pytest.mark.parametrize("name,program,good,bad,label", DRIVERS,
                         ids=[d[0] for d in DRIVERS])
def test_a_pin_the_driver_cannot_drive_stops_the_build(tmp_path, name, program,
                                                       good, bad, label):
    out = build(tmp_path, program, bad)
    assert "[BUILD_OK]" not in out, \
        f"{label} accepted {bad} and built a program that cannot work:\n{out}"
    assert "CompileError" in out and label in out, \
        f"{label} rejected {bad} without naming itself:\n{out}"
    # A diagnostic with no file:line leaves the reader nowhere to go.
    assert "main.py:" in out, f"{label}'s refusal is not located:\n{out}"


@pytest.mark.parametrize("name,program,good,bad,label", DRIVERS,
                         ids=[d[0] for d in DRIVERS])
def test_a_pin_the_driver_does_drive_still_builds(tmp_path, name, program,
                                                 good, bad, label):
    out = build(tmp_path, program, good)
    assert "[BUILD_OK]" in out, f"{label} no longer builds on {good}:\n{out}"

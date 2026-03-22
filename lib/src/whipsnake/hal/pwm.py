from whipsnake.chips import __CHIP__
from whipsnake.types import uint8, inline
from whipsnake.hal.gpio import Pin


# PWM -- hardware PWM ZCA (all methods @inline, zero-cost).
# ATmega328P pins: PD6 (OC0A), PD5 (OC0B), PB1 (OC1A), PB2 (OC1B),
#                 PB3 (OC2A), PD3 (OC2B).
# self._ocr        -- ptr to the Output Compare Register for the pin.
# self._tccr_b     -- ptr to the TCCRxB register (controls prescaler / run).
# self._start_val  -- TCCRxB value that enables the timer (prescaler).
# These are resolved once at construction via match/case and stored so that
# set_duty / start / stop are each a single register write at runtime.
# noinspection PyProtectedMember
class PWM:

    @inline
    def __init__(self, pin: str, duty: uint8):
        match __CHIP__.name:
            case "atmega328p":
                from whipsnake.hal._pwm.atmega328p import pwm_init, pwm_select_ocr, pwm_select_tccr_b, pwm_select_start_val
                pwm_init(pin, duty)
                self._ocr       = pwm_select_ocr(pin)
                self._tccr_b    = pwm_select_tccr_b(pin)
                self._start_val = pwm_select_start_val(pin)
            case "pic16f877a":
                from whipsnake.hal._pwm.pic16f877a import pwm_init
                self.pin = pin
                pwm_init(pin, duty)
            case "pic16f18877":
                from whipsnake.hal._pwm.pic16f18877 import pwm_init
                self.pin = pin
                pwm_init(pin, duty)
            case "pic18f45k50":
                from whipsnake.hal._pwm.pic18f45k50 import pwm_init
                self.pin = pin
                pwm_init(pin, duty)

    @inline
    def __init__(self, pin: Pin, duty: uint8):
        match __CHIP__.name:
            case "atmega328p":
                from whipsnake.hal._pwm.atmega328p import pwm_init, pwm_select_ocr, pwm_select_tccr_b, pwm_select_start_val
                pwm_init(pin.name, duty)
                self._ocr       = pwm_select_ocr(pin.name)
                self._tccr_b    = pwm_select_tccr_b(pin.name)
                self._start_val = pwm_select_start_val(pin.name)
            case "pic16f877a":
                from whipsnake.hal._pwm.pic16f877a import pwm_init
                self.pin = pin.name
                pwm_init(pin.name, duty)
            case "pic16f18877":
                from whipsnake.hal._pwm.pic16f18877 import pwm_init
                self.pin = pin.name
                pwm_init(pin.name, duty)
            case "pic18f45k50":
                from whipsnake.hal._pwm.pic18f45k50 import pwm_init
                self.pin = pin.name
                pwm_init(pin.name, duty)

    @inline
    def set_duty(self, duty: uint8):
        match __CHIP__.name:
            case "atmega328p":
                # Direct OCR write -- single STS instruction.
                self._ocr.value = duty
            case "pic16f877a":
                from whipsnake.hal._pwm.pic16f877a import pwm_set_duty
                pwm_set_duty(self.pin, duty)
            case "pic16f18877":
                from whipsnake.hal._pwm.pic16f18877 import pwm_set_duty
                pwm_set_duty(self.pin, duty)
            case "pic18f45k50":
                from whipsnake.hal._pwm.pic18f45k50 import pwm_set_duty
                pwm_set_duty(self.pin, duty)

    @inline
    def start(self):
        match __CHIP__.name:
            case "atmega328p":
                # Restore prescaler value to re-enable the timer.
                self._tccr_b.value = self._start_val
            case "pic16f877a":
                from whipsnake.hal._pwm.pic16f877a import pwm_start
                pwm_start(self.pin)
            case "pic16f18877":
                from whipsnake.hal._pwm.pic16f18877 import pwm_start
                pwm_start(self.pin)
            case "pic18f45k50":
                from whipsnake.hal._pwm.pic18f45k50 import pwm_start
                pwm_start(self.pin)

    @inline
    def stop(self):
        match __CHIP__.name:
            case "atmega328p":
                # Clear TCCRxB to stop the timer clock.
                self._tccr_b.value = 0x00
            case "pic16f877a":
                from whipsnake.hal._pwm.pic16f877a import pwm_stop
                pwm_stop(self.pin)
            case "pic16f18877":
                from whipsnake.hal._pwm.pic16f18877 import pwm_stop
                pwm_stop(self.pin)
            case "pic18f45k50":
                from whipsnake.hal._pwm.pic18f45k50 import pwm_stop
                pwm_stop(self.pin)

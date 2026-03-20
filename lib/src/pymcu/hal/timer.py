from pymcu.chips import __CHIP__
from pymcu.types import uint8, uint16, const, inline

# ---- Unified Timer ZCA ----
# MicroPython-style: Timer(n, prescaler) — n is a compile-time constant.
# The compiler folds the `n` dispatch at compile time (dead-code elimination)
# so each call site emits only the instructions for the selected timer.
#
# ATmega328P:
#   Timer(0, prescaler)  -- 8-bit;  prescalers: 1/8/64/256
#                           OVF vector 0x0010; ~977 Hz OVF at 64x, 16 MHz
#   Timer(1, prescaler)  -- 16-bit; prescalers: 1/8/64/256/1024
#                           OVF vector 0x000d; ~0.48 Hz OVF at 1024x, 16 MHz
#   Timer(2, prescaler)  -- 8-bit async; prescalers: 1/8/32/64/128/256/1024
#                           OVF vector 0x0009; ~61 Hz OVF at 1024x, 16 MHz
#
# PIC chips only support n=0 (Timer0).

class Timer:

    @inline
    def __init__(self: uint8, n: const[uint8], prescaler: uint8):
        self._n = n
        match __CHIP__.name:
            case "atmega328p":
                if n == 0:
                    from pymcu.hal._timer.atmega328p import timer0_init
                    timer0_init(prescaler)
                elif n == 1:
                    from pymcu.hal._timer.atmega328p import timer1_init
                    timer1_init(prescaler)
                elif n == 2:
                    from pymcu.hal._timer.atmega328p import timer2_init
                    timer2_init(prescaler)
            case "pic16f877a":
                from pymcu.hal._timer.pic16f877a import timer0_init
                timer0_init(prescaler)
            case "pic16f18877":
                from pymcu.hal._timer.pic16f18877 import timer0_init
                timer0_init(prescaler)
            case "pic16f84a":
                from pymcu.hal._timer.pic16f84a import timer0_init
                timer0_init(prescaler)
            case "pic10f200":
                from pymcu.hal._timer.pic10f200 import timer0_init
                timer0_init(prescaler)
            case "pic18f45k50":
                from pymcu.hal._timer.pic18f45k50 import timer0_init
                timer0_init(prescaler)

    @inline
    def start(self: uint8):
        match __CHIP__.name:
            case "atmega328p":
                if self._n == 0:
                    from pymcu.hal._timer.atmega328p import timer0_start
                    timer0_start()
                elif self._n == 1:
                    from pymcu.hal._timer.atmega328p import timer1_start
                    timer1_start()
                elif self._n == 2:
                    from pymcu.hal._timer.atmega328p import timer2_start
                    timer2_start()
            case "pic16f877a":
                from pymcu.hal._timer.pic16f877a import timer0_start
                timer0_start()
            case "pic16f18877":
                from pymcu.hal._timer.pic16f18877 import timer0_start
                timer0_start()
            case "pic16f84a":
                from pymcu.hal._timer.pic16f84a import timer0_start
                timer0_start()
            case "pic10f200":
                from pymcu.hal._timer.pic10f200 import timer0_start
                timer0_start()
            case "pic18f45k50":
                from pymcu.hal._timer.pic18f45k50 import timer0_start
                timer0_start()

    @inline
    def stop(self: uint8):
        match __CHIP__.name:
            case "atmega328p":
                if self._n == 0:
                    from pymcu.hal._timer.atmega328p import timer0_stop
                    timer0_stop()
                elif self._n == 1:
                    from pymcu.hal._timer.atmega328p import timer1_stop
                    timer1_stop()
                elif self._n == 2:
                    from pymcu.hal._timer.atmega328p import timer2_stop
                    timer2_stop()
            case "pic16f877a":
                from pymcu.hal._timer.pic16f877a import timer0_stop
                timer0_stop()
            case "pic16f18877":
                from pymcu.hal._timer.pic16f18877 import timer0_stop
                timer0_stop()
            case "pic16f84a":
                from pymcu.hal._timer.pic16f84a import timer0_stop
                timer0_stop()
            case "pic10f200":
                from pymcu.hal._timer.pic10f200 import timer0_stop
                timer0_stop()
            case "pic18f45k50":
                from pymcu.hal._timer.pic18f45k50 import timer0_stop
                timer0_stop()

    @inline
    def clear(self: uint8):
        match __CHIP__.name:
            case "atmega328p":
                if self._n == 0:
                    from pymcu.hal._timer.atmega328p import timer0_clear
                    timer0_clear()
                elif self._n == 1:
                    from pymcu.hal._timer.atmega328p import timer1_clear
                    timer1_clear()
                elif self._n == 2:
                    from pymcu.hal._timer.atmega328p import timer2_clear
                    timer2_clear()
            case "pic16f877a":
                from pymcu.hal._timer.pic16f877a import timer0_clear
                timer0_clear()
            case "pic16f18877":
                from pymcu.hal._timer.pic16f18877 import timer0_clear
                timer0_clear()
            case "pic16f84a":
                from pymcu.hal._timer.pic16f84a import timer0_clear
                timer0_clear()
            case "pic10f200":
                from pymcu.hal._timer.pic10f200 import timer0_clear
                timer0_clear()
            case "pic18f45k50":
                from pymcu.hal._timer.pic18f45k50 import timer0_clear
                timer0_clear()

    # Sets the OCR (Output Compare Register) for timer n and enables CTC mode.
    # CTC vectors (ATmega328P): Timer0_COMPA=0x001C/word0x0E, Timer1_COMPA=0x0016/word0x0B,
    #   Timer2_COMPA=0x000E/word0x07.
    # Call start() first to configure the prescaler, then set_compare() to enable CTC.
    @inline
    def set_compare(self: uint8, value: uint16):
        match __CHIP__.name:
            case "atmega328p":
                if self._n == 0:
                    from pymcu.hal._timer.atmega328p import timer0_set_compare
                    timer0_set_compare(value)
                elif self._n == 1:
                    from pymcu.hal._timer.atmega328p import timer1_set_compare
                    timer1_set_compare(value)
                elif self._n == 2:
                    from pymcu.hal._timer.atmega328p import timer2_set_compare
                    timer2_set_compare(value)

    @inline
    def overflow(self: uint8) -> uint8:
        if __CHIP__.name == "atmega328p":
            if self._n == 0:
                from pymcu.hal._timer.atmega328p import timer0_overflow
                return timer0_overflow()
            elif self._n == 1:
                from pymcu.hal._timer.atmega328p import timer1_overflow
                return timer1_overflow()
            elif self._n == 2:
                from pymcu.hal._timer.atmega328p import timer2_overflow
                return timer2_overflow()
        return 0

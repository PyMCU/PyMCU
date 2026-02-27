# ATmega328P: UART command interpreter
# Tests: match/case on received byte, UART read/write, Pin HAL, delay_ms,
#        uint8 state variable, while loop with counter, write_str
#
# Hardware: Arduino Uno
#   LED on PB5 (built-in, Arduino pin 13)
#   Serial terminal at 9600 baud — send single-character commands:
#     'B' (66)  — blink LED 5 times
#     'H' (72)  — LED on
#     'L' (76)  — LED off
#     'T' (84)  — toggle LED
#     'S' (83)  — print LED status ('0' or '1')
#     '?' (63)  — print help
#     any other — echo back with '?' prefix
#
from pymcu.types import uint8
from pymcu.hal.gpio import Pin
from pymcu.hal.uart import UART
from pymcu.time import delay_ms


# Command byte constants — wrap in a class so match/case sees dotted names
# (dotted names = value patterns in Python, bare names = capture patterns)
class CMD:
    BLINK  = 66   # 'B'
    HIGH   = 72   # 'H'
    LOW    = 76   # 'L'
    TOGGLE = 84   # 'T'
    STATUS = 83   # 'S'
    HELP   = 63   # '?'


def main():
    led  = Pin("PB5", Pin.OUT)
    uart = UART(9600)

    uart.println("UART CMD READY")
    uart.println("B=Blink H=High L=Low T=Toggle S=Status ?=Help")

    led_on: uint8 = 0    # tracks LED state: 0=off, 1=on

    while True:
        cmd: uint8 = uart.read()

        match cmd:
            case CMD.BLINK:
                # Blink 5 times (10 toggles)
                uart.println("BLINK x5")
                i: uint8 = 0
                while i < 10:
                    led.toggle()
                    delay_ms(100)
                    i = i + 1
                led_on = 0
                led.low()

            case CMD.HIGH:
                led.high()
                led_on = 1
                uart.println("LED ON")

            case CMD.LOW:
                led.low()
                led_on = 0
                uart.println("LED OFF")

            case CMD.TOGGLE:
                led.toggle()
                if led_on == 1:
                    led_on = 0
                    uart.println("LED OFF")
                else:
                    led_on = 1
                    uart.println("LED ON")

            case CMD.STATUS:
                uart.write_str("LED=")
                uart.write(led_on + 48)    # 48='0', 49='1'
                uart.write('\n')

            case CMD.HELP:
                uart.println("B=Blink H=High L=Low T=Toggle S=Status ?=Help")

            case _:
                # Unknown command: echo it back with '?' prefix
                uart.write('?')
                uart.write(cmd)
                uart.write('\n')

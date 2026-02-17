from pymcu.chips import __CHIP__
from pymcu.types import uint8, const, inline

class Pin:
    IN  = 1
    OUT = 0
    OPEN_DRAIN = 2

    PULL_UP   = 1
    PULL_DOWN = 2

    DRIVE_0 = 0
    DRIVE_1 = 1

    IRQ_FALLING    = 1
    IRQ_RISING     = 2
    IRQ_LOW_LEVEL  = 4
    IRQ_HIGH_LEVEL = 8

    @inline
    def __init__(self: uint8, name: str, mode: uint8, pull: const[uint8] = -1, value: const = -1, drive: const = 0, alt: const = -1):
        self.name = name
        match __CHIP__.name:
            case "pic16f18877":
                from pymcu.hal._gpio.pic16f18877 import pin_set_mode
                pin_set_mode(name, mode)
                if pull != -1:
                    from pymcu.hal._gpio.pic16f18877 import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(name)
                    elif pull == 0:
                        pin_pull_off(name)
                if value != -1:
                    from pymcu.hal._gpio.pic16f18877 import pin_write
                    pin_write(name, value)
            case "pic16f877a":
                from pymcu.hal._gpio.pic16f877a import pin_set_mode
                pin_set_mode(name, mode)
                if pull != -1:
                    from pymcu.hal._gpio.pic16f877a import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(name)
                    elif pull == 0:
                        pin_pull_off(name)
                if value != -1:
                    from pymcu.hal._gpio.pic16f877a import pin_write
                    pin_write(name, value)
            case "pic16f84a":
                from pymcu.hal._gpio.pic16f84a import pin_set_mode
                pin_set_mode(name, mode)
                if pull != -1:
                    from pymcu.hal._gpio.pic16f84a import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(name)
                    elif pull == 0:
                        pin_pull_off(name)
                if value != -1:
                    from pymcu.hal._gpio.pic16f84a import pin_write
                    pin_write(name, value)
            case "pic10f200":
                from pymcu.hal._gpio.pic10f200 import pin_set_mode
                pin_set_mode(name, mode)
                if pull != -1:
                    from pymcu.hal._gpio.pic10f200 import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(name)
                    elif pull == 0:
                        pin_pull_off(name)
                if value != -1:
                    from pymcu.hal._gpio.pic10f200 import pin_write
                    pin_write(name, value)
            case "pic18f45k50":
                from pymcu.hal._gpio.pic18f45k50 import pin_set_mode
                pin_set_mode(name, mode)
                if pull != -1:
                    from pymcu.hal._gpio.pic18f45k50 import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(name)
                    elif pull == 0:
                        pin_pull_off(name)
                if value != -1:
                    from pymcu.hal._gpio.pic18f45k50 import pin_write
                    pin_write(name, value)
            case "atmega328p":
                if mode == 2:
                    raise NotImplementedError("Open-drain mode not supported on ATmega328P")
                if alt != -1:
                    raise NotImplementedError("Alternate functions not supported on ATmega328P")
                if drive != 0:
                    raise NotImplementedError("Drive strength control not supported on ATmega328P")
                from pymcu.hal._gpio.atmega328p import pin_set_mode
                pin_set_mode(name, mode)
                if pull != -1:
                    if pull == 2:
                        raise NotImplementedError("Pull-down resistor not supported on ATmega328P")
                    from pymcu.hal._gpio.atmega328p import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(name)
                    elif pull == 0:
                        pin_pull_off(name)
                if value != -1:
                    from pymcu.hal._gpio.atmega328p import pin_write
                    pin_write(name, value)

    @inline
    def high(self: uint8):
        match __CHIP__.name:
            case "pic16f18877":
                from pymcu.hal._gpio.pic16f18877 import pin_high
                pin_high(self.name)
            case "pic16f877a":
                from pymcu.hal._gpio.pic16f877a import pin_high
                pin_high(self.name)
            case "pic16f84a":
                from pymcu.hal._gpio.pic16f84a import pin_high
                pin_high(self.name)
            case "pic10f200":
                from pymcu.hal._gpio.pic10f200 import pin_high
                pin_high(self.name)
            case "pic18f45k50":
                from pymcu.hal._gpio.pic18f45k50 import pin_high
                pin_high(self.name)
            case "atmega328p":
                from pymcu.hal._gpio.atmega328p import pin_high
                pin_high(self.name)

    @inline
    def low(self: uint8):
        match __CHIP__.name:
            case "pic16f18877":
                from pymcu.hal._gpio.pic16f18877 import pin_low
                pin_low(self.name)
            case "pic16f877a":
                from pymcu.hal._gpio.pic16f877a import pin_low
                pin_low(self.name)
            case "pic16f84a":
                from pymcu.hal._gpio.pic16f84a import pin_low
                pin_low(self.name)
            case "pic10f200":
                from pymcu.hal._gpio.pic10f200 import pin_low
                pin_low(self.name)
            case "pic18f45k50":
                from pymcu.hal._gpio.pic18f45k50 import pin_low
                pin_low(self.name)
            case "atmega328p":
                from pymcu.hal._gpio.atmega328p import pin_low
                pin_low(self.name)

    @inline
    def on(self: uint8):
        self.high()

    @inline
    def off(self: uint8):
        self.low()

    @inline
    def toggle(self: uint8):
        match __CHIP__.name:
            case "pic16f18877":
                from pymcu.hal._gpio.pic16f18877 import pin_toggle
                pin_toggle(self.name)
            case "pic16f877a":
                from pymcu.hal._gpio.pic16f877a import pin_toggle
                pin_toggle(self.name)
            case "pic16f84a":
                from pymcu.hal._gpio.pic16f84a import pin_toggle
                pin_toggle(self.name)
            case "pic10f200":
                from pymcu.hal._gpio.pic10f200 import pin_toggle
                pin_toggle(self.name)
            case "pic18f45k50":
                from pymcu.hal._gpio.pic18f45k50 import pin_toggle
                pin_toggle(self.name)
            case "atmega328p":
                from pymcu.hal._gpio.atmega328p import pin_toggle
                pin_toggle(self.name)

    @inline
    def value(self: uint8, x: const = -1) -> uint8:
        if x == -1:
            match __CHIP__.name:
                case "atmega328p":
                    from pymcu.hal._gpio.atmega328p import pin_read
                    return pin_read(self.name)
                case "pic16f18877":
                    from pymcu.hal._gpio.pic16f18877 import pin_read
                    return pin_read(self.name)
                case "pic16f877a":
                    from pymcu.hal._gpio.pic16f877a import pin_read
                    return pin_read(self.name)
                case "pic16f84a":
                    from pymcu.hal._gpio.pic16f84a import pin_read
                    return pin_read(self.name)
                case "pic10f200":
                    from pymcu.hal._gpio.pic10f200 import pin_read
                    return pin_read(self.name)
                case "pic18f45k50":
                    from pymcu.hal._gpio.pic18f45k50 import pin_read
                    return pin_read(self.name)
        else:
            match __CHIP__.name:
                case "pic16f18877":
                    from pymcu.hal._gpio.pic16f18877 import pin_write
                    pin_write(self.name, x)
                case "pic16f877a":
                    from pymcu.hal._gpio.pic16f877a import pin_write
                    pin_write(self.name, x)
                case "pic16f84a":
                    from pymcu.hal._gpio.pic16f84a import pin_write
                    pin_write(self.name, x)
                case "pic10f200":
                    from pymcu.hal._gpio.pic10f200 import pin_write
                    pin_write(self.name, x)
                case "pic18f45k50":
                    from pymcu.hal._gpio.pic18f45k50 import pin_write
                    pin_write(self.name, x)
                case "atmega328p":
                    from pymcu.hal._gpio.atmega328p import pin_write
                    pin_write(self.name, x)

    @inline
    def init(self: uint8, mode: const = -1, pull: const = -1, value: const = -1, drive: const = 0, alt: const = -1):
        if mode != -1:
            match __CHIP__.name:
                case "pic16f18877":
                    from pymcu.hal._gpio.pic16f18877 import pin_set_mode
                    pin_set_mode(self.name, mode)
                case "pic16f877a":
                    from pymcu.hal._gpio.pic16f877a import pin_set_mode
                    pin_set_mode(self.name, mode)
                case "pic16f84a":
                    from pymcu.hal._gpio.pic16f84a import pin_set_mode
                    pin_set_mode(self.name, mode)
                case "pic10f200":
                    from pymcu.hal._gpio.pic10f200 import pin_set_mode
                    pin_set_mode(self.name, mode)
                case "pic18f45k50":
                    from pymcu.hal._gpio.pic18f45k50 import pin_set_mode
                    pin_set_mode(self.name, mode)
                case "atmega328p":
                    from pymcu.hal._gpio.atmega328p import pin_set_mode
                    pin_set_mode(self.name, mode)
        if pull != -1:
            match __CHIP__.name:
                case "atmega328p":
                    if pull == 2:
                        raise NotImplementedError("Pull-down resistor not supported on ATmega328P")
                    from pymcu.hal._gpio.atmega328p import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(self.name)
                    elif pull == 0:
                        pin_pull_off(self.name)
                case "pic16f18877":
                    from pymcu.hal._gpio.pic16f18877 import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(self.name)
                    elif pull == 0:
                        pin_pull_off(self.name)
                case "pic16f877a":
                    from pymcu.hal._gpio.pic16f877a import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(self.name)
                    elif pull == 0:
                        pin_pull_off(self.name)
                case "pic16f84a":
                    from pymcu.hal._gpio.pic16f84a import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(self.name)
                    elif pull == 0:
                        pin_pull_off(self.name)
                case "pic10f200":
                    from pymcu.hal._gpio.pic10f200 import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(self.name)
                    elif pull == 0:
                        pin_pull_off(self.name)
                case "pic18f45k50":
                    from pymcu.hal._gpio.pic18f45k50 import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(self.name)
                    elif pull == 0:
                        pin_pull_off(self.name)
        if value != -1:
            match __CHIP__.name:
                case "atmega328p":
                    from pymcu.hal._gpio.atmega328p import pin_write
                    pin_write(self.name, value)
                case "pic16f18877":
                    from pymcu.hal._gpio.pic16f18877 import pin_write
                    pin_write(self.name, value)
                case "pic16f877a":
                    from pymcu.hal._gpio.pic16f877a import pin_write
                    pin_write(self.name, value)
                case "pic16f84a":
                    from pymcu.hal._gpio.pic16f84a import pin_write
                    pin_write(self.name, value)
                case "pic10f200":
                    from pymcu.hal._gpio.pic10f200 import pin_write
                    pin_write(self.name, value)
                case "pic18f45k50":
                    from pymcu.hal._gpio.pic18f45k50 import pin_write
                    pin_write(self.name, value)
        if drive != 0:
            if __CHIP__.name == "atmega328p":
                raise NotImplementedError("Drive strength control not supported on ATmega328P")
        if alt != -1:
            if __CHIP__.name == "atmega328p":
                raise NotImplementedError("Alternate functions not supported on ATmega328P")

    @inline
    def pull(self: uint8, pull_mode: uint8):
        match __CHIP__.name:
            case "atmega328p":
                if pull_mode == 2:
                    raise NotImplementedError("Pull-down resistor not supported on ATmega328P")
                from pymcu.hal._gpio.atmega328p import pin_pull_up, pin_pull_off
                if pull_mode == 1:
                    pin_pull_up(self.name)
                elif pull_mode == 0:
                    pin_pull_off(self.name)
            case "pic16f18877":
                from pymcu.hal._gpio.pic16f18877 import pin_pull_up, pin_pull_off
                if pull_mode == 1:
                    pin_pull_up(self.name)
                elif pull_mode == 0:
                    pin_pull_off(self.name)
            case "pic16f877a":
                from pymcu.hal._gpio.pic16f877a import pin_pull_up, pin_pull_off
                if pull_mode == 1:
                    pin_pull_up(self.name)
                elif pull_mode == 0:
                    pin_pull_off(self.name)
            case "pic16f84a":
                from pymcu.hal._gpio.pic16f84a import pin_pull_up, pin_pull_off
                if pull_mode == 1:
                    pin_pull_up(self.name)
                elif pull_mode == 0:
                    pin_pull_off(self.name)
            case "pic10f200":
                from pymcu.hal._gpio.pic10f200 import pin_pull_up, pin_pull_off
                if pull_mode == 1:
                    pin_pull_up(self.name)
                elif pull_mode == 0:
                    pin_pull_off(self.name)
            case "pic18f45k50":
                from pymcu.hal._gpio.pic18f45k50 import pin_pull_up, pin_pull_off
                if pull_mode == 1:
                    pin_pull_up(self.name)
                elif pull_mode == 0:
                    pin_pull_off(self.name)

    @inline
    def drive(self: uint8, strength: uint8):
        if __CHIP__.name == "atmega328p":
            raise NotImplementedError("Drive strength control not supported on ATmega328P")

    @inline
    def irq(self: uint8, trigger: const = 3):
        match __CHIP__.name:
            case "atmega328p":
                from pymcu.hal._gpio.atmega328p import pin_irq_setup
                pin_irq_setup(self.name, trigger)
            case "pic16f877a":
                from pymcu.hal._gpio.pic16f877a import pin_irq_setup
                pin_irq_setup(self.name, trigger)
            case "pic16f84a":
                from pymcu.hal._gpio.pic16f84a import pin_irq_setup
                pin_irq_setup(self.name, trigger)
            case "pic16f18877":
                from pymcu.hal._gpio.pic16f18877 import pin_irq_setup
                pin_irq_setup(self.name, trigger)
            case "pic18f45k50":
                from pymcu.hal._gpio.pic18f45k50 import pin_irq_setup
                pin_irq_setup(self.name, trigger)
            case "pic10f200":
                raise NotImplementedError("IRQ not supported on PIC10F200")

    @inline
    def mode(self: uint8, m: const = -1) -> uint8:
        if m != -1:
            match __CHIP__.name:
                case "pic16f18877":
                    from pymcu.hal._gpio.pic16f18877 import pin_set_mode
                    pin_set_mode(self.name, m)
                case "pic16f877a":
                    from pymcu.hal._gpio.pic16f877a import pin_set_mode
                    pin_set_mode(self.name, m)
                case "pic16f84a":
                    from pymcu.hal._gpio.pic16f84a import pin_set_mode
                    pin_set_mode(self.name, m)
                case "pic10f200":
                    from pymcu.hal._gpio.pic10f200 import pin_set_mode
                    pin_set_mode(self.name, m)
                case "pic18f45k50":
                    from pymcu.hal._gpio.pic18f45k50 import pin_set_mode
                    pin_set_mode(self.name, m)
                case "atmega328p":
                    from pymcu.hal._gpio.atmega328p import pin_set_mode
                    pin_set_mode(self.name, m)

from pymcu.chips import __CHIP__
from pymcu.types import uint8, uint16, const, inline
from pymcu.exceptions import CompileError

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

    def __init__(self, name: str, mode: const[uint8], pull: const[uint8] = -1, value: const = -1, drive: const = 0, alt: const = -1):
        self.name = name
        match __CHIP__.name:
            case "pic16f18877":
                from pymcu.hal.pic14.pic16f18877_gpio import pin_set_mode, pin_pull_up, pin_pull_off, pin_write
                pin_set_mode(name, mode)
                if pull != -1:
                    if pull == 1:
                        pin_pull_up(name)
                    elif pull == 0:
                        pin_pull_off(name)
                if value != -1:
                    pin_write(name, value)
            case "pic16f628a":
                from pymcu.hal.pic14.pic16f628a_gpio import pin_set_mode, pin_pull_up, pin_pull_off, pin_write
                pin_set_mode(name, mode)
                if pull != -1:
                    if pull == 1:
                        pin_pull_up(name)
                    elif pull == 0:
                        pin_pull_off(name)
                if value != -1:
                    pin_write(name, value)
            case "pic16f877a":
                from pymcu.hal.pic14.pic16f877a_gpio import pin_set_mode, pin_pull_up, pin_pull_off, pin_write
                pin_set_mode(name, mode)
                if pull != -1:
                    if pull == 1:
                        pin_pull_up(name)
                    elif pull == 0:
                        pin_pull_off(name)
                if value != -1:
                    pin_write(name, value)
            case "pic16f84a":
                from pymcu.hal.pic14.pic16f84a_gpio import pin_set_mode, pin_pull_up, pin_pull_off, pin_write
                pin_set_mode(name, mode)
                if pull != -1:
                    if pull == 1:
                        pin_pull_up(name)
                    elif pull == 0:
                        pin_pull_off(name)
                if value != -1:
                    pin_write(name, value)
            case "pic10f200":
                from pymcu.hal.pic12.pic10f200_gpio import pin_set_mode, pin_pull_up, pin_pull_off, pin_write
                pin_set_mode(name, mode)
                if pull != -1:
                    if pull == 1:
                        pin_pull_up(name)
                    elif pull == 0:
                        pin_pull_off(name)
                if value != -1:
                    pin_write(name, value)
            case "pic18f45k50":
                from pymcu.hal.pic18.pic18f45k50_gpio import pin_set_mode, pin_pull_up, pin_pull_off, pin_write
                pin_set_mode(name, mode)
                if pull != -1:
                    if pull == 1:
                        pin_pull_up(name)
                    elif pull == 0:
                        pin_pull_off(name)
                if value != -1:
                    pin_write(name, value)

    @inline
    def high(self):
        match __CHIP__.name:
            case "pic16f18877":
                from pymcu.hal.pic14.pic16f18877_gpio import pin_high
                pin_high(self.name)
            case "pic16f628a":
                from pymcu.hal.pic14.pic16f628a_gpio import pin_high
                pin_high(self.name)
            case "pic16f877a":
                from pymcu.hal.pic14.pic16f877a_gpio import pin_high
                pin_high(self.name)
            case "pic16f84a":
                from pymcu.hal.pic14.pic16f84a_gpio import pin_high
                pin_high(self.name)
            case "pic10f200":
                from pymcu.hal.pic12.pic10f200_gpio import pin_high
                pin_high(self.name)
            case "pic18f45k50":
                from pymcu.hal.pic18.pic18f45k50_gpio import pin_high
                pin_high(self.name)

    @inline
    def low(self):
        match __CHIP__.name:
            case "pic16f18877":
                from pymcu.hal.pic14.pic16f18877_gpio import pin_low
                pin_low(self.name)
            case "pic16f628a":
                from pymcu.hal.pic14.pic16f628a_gpio import pin_low
                pin_low(self.name)
            case "pic16f877a":
                from pymcu.hal.pic14.pic16f877a_gpio import pin_low
                pin_low(self.name)
            case "pic16f84a":
                from pymcu.hal.pic14.pic16f84a_gpio import pin_low
                pin_low(self.name)
            case "pic10f200":
                from pymcu.hal.pic12.pic10f200_gpio import pin_low
                pin_low(self.name)
            case "pic18f45k50":
                from pymcu.hal.pic18.pic18f45k50_gpio import pin_low
                pin_low(self.name)

    @inline
    def on(self):
        self.high()

    @inline
    def off(self):
        self.low()

    @inline
    def toggle(self):
        match __CHIP__.name:
            case "pic16f18877":
                from pymcu.hal.pic14.pic16f18877_gpio import pin_toggle
                pin_toggle(self.name)
            case "pic16f628a":
                from pymcu.hal.pic14.pic16f628a_gpio import pin_toggle
                pin_toggle(self.name)
            case "pic16f877a":
                from pymcu.hal.pic14.pic16f877a_gpio import pin_toggle
                pin_toggle(self.name)
            case "pic16f84a":
                from pymcu.hal.pic14.pic16f84a_gpio import pin_toggle
                pin_toggle(self.name)
            case "pic10f200":
                from pymcu.hal.pic12.pic10f200_gpio import pin_toggle
                pin_toggle(self.name)
            case "pic18f45k50":
                from pymcu.hal.pic18.pic18f45k50_gpio import pin_toggle
                pin_toggle(self.name)

    @inline
    def value(self, x: const = -1) -> uint8:
        if x == -1:
            match __CHIP__.name:
                case "pic16f18877":
                    from pymcu.hal.pic14.pic16f18877_gpio import pin_read
                    return pin_read(self.name)
                case "pic16f628a":
                    from pymcu.hal.pic14.pic16f628a_gpio import pin_read
                    return pin_read(self.name)
                case "pic16f877a":
                    from pymcu.hal.pic14.pic16f877a_gpio import pin_read
                    return pin_read(self.name)
                case "pic16f84a":
                    from pymcu.hal.pic14.pic16f84a_gpio import pin_read
                    return pin_read(self.name)
                case "pic10f200":
                    from pymcu.hal.pic12.pic10f200_gpio import pin_read
                    return pin_read(self.name)
                case "pic18f45k50":
                    from pymcu.hal.pic18.pic18f45k50_gpio import pin_read
                    return pin_read(self.name)
        else:
            match __CHIP__.name:
                case "pic16f18877":
                    from pymcu.hal.pic14.pic16f18877_gpio import pin_write
                    pin_write(self.name, x)
                case "pic16f628a":
                    from pymcu.hal.pic14.pic16f628a_gpio import pin_write
                    pin_write(self.name, x)
                case "pic16f877a":
                    from pymcu.hal.pic14.pic16f877a_gpio import pin_write
                    pin_write(self.name, x)
                case "pic16f84a":
                    from pymcu.hal.pic14.pic16f84a_gpio import pin_write
                    pin_write(self.name, x)
                case "pic10f200":
                    from pymcu.hal.pic12.pic10f200_gpio import pin_write
                    pin_write(self.name, x)
                case "pic18f45k50":
                    from pymcu.hal.pic18.pic18f45k50_gpio import pin_write
                    pin_write(self.name, x)

    @inline
    def init(self, mode: const = -1, pull: const = -1, value: const = -1, drive: const = 0, alt: const = -1):
        if mode != -1:
            match __CHIP__.name:
                case "pic16f18877":
                    from pymcu.hal.pic14.pic16f18877_gpio import pin_set_mode
                    pin_set_mode(self.name, mode)
                case "pic16f628a":
                    from pymcu.hal.pic14.pic16f628a_gpio import pin_set_mode
                    pin_set_mode(self.name, mode)
                case "pic16f877a":
                    from pymcu.hal.pic14.pic16f877a_gpio import pin_set_mode
                    pin_set_mode(self.name, mode)
                case "pic16f84a":
                    from pymcu.hal.pic14.pic16f84a_gpio import pin_set_mode
                    pin_set_mode(self.name, mode)
                case "pic10f200":
                    from pymcu.hal.pic12.pic10f200_gpio import pin_set_mode
                    pin_set_mode(self.name, mode)
                case "pic18f45k50":
                    from pymcu.hal.pic18.pic18f45k50_gpio import pin_set_mode
                    pin_set_mode(self.name, mode)
        if pull != -1:
            match __CHIP__.name:
                case "pic16f18877":
                    from pymcu.hal.pic14.pic16f18877_gpio import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(self.name)
                    elif pull == 0:
                        pin_pull_off(self.name)
                case "pic16f628a":
                    from pymcu.hal.pic14.pic16f628a_gpio import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(self.name)
                    elif pull == 0:
                        pin_pull_off(self.name)
                case "pic16f877a":
                    from pymcu.hal.pic14.pic16f877a_gpio import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(self.name)
                    elif pull == 0:
                        pin_pull_off(self.name)
                case "pic16f84a":
                    from pymcu.hal.pic14.pic16f84a_gpio import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(self.name)
                    elif pull == 0:
                        pin_pull_off(self.name)
                case "pic10f200":
                    from pymcu.hal.pic12.pic10f200_gpio import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(self.name)
                    elif pull == 0:
                        pin_pull_off(self.name)
                case "pic18f45k50":
                    from pymcu.hal.pic18.pic18f45k50_gpio import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(self.name)
                    elif pull == 0:
                        pin_pull_off(self.name)
        if value != -1:
            match __CHIP__.name:
                case "pic16f18877":
                    from pymcu.hal.pic14.pic16f18877_gpio import pin_write
                    pin_write(self.name, value)
                case "pic16f628a":
                    from pymcu.hal.pic14.pic16f628a_gpio import pin_write
                    pin_write(self.name, value)
                case "pic16f877a":
                    from pymcu.hal.pic14.pic16f877a_gpio import pin_write
                    pin_write(self.name, value)
                case "pic16f84a":
                    from pymcu.hal.pic14.pic16f84a_gpio import pin_write
                    pin_write(self.name, value)
                case "pic10f200":
                    from pymcu.hal.pic12.pic10f200_gpio import pin_write
                    pin_write(self.name, value)
                case "pic18f45k50":
                    from pymcu.hal.pic18.pic18f45k50_gpio import pin_write
                    pin_write(self.name, value)

    @inline
    def pull(self, pull_mode: const):
        match __CHIP__.name:
            case "pic16f18877":
                from pymcu.hal.pic14.pic16f18877_gpio import pin_pull_up, pin_pull_off
                if pull_mode == 1:
                    pin_pull_up(self.name)
                elif pull_mode == 0:
                    pin_pull_off(self.name)
            case "pic16f628a":
                from pymcu.hal.pic14.pic16f628a_gpio import pin_pull_up, pin_pull_off
                if pull_mode == 1:
                    pin_pull_up(self.name)
                elif pull_mode == 0:
                    pin_pull_off(self.name)
            case "pic16f877a":
                from pymcu.hal.pic14.pic16f877a_gpio import pin_pull_up, pin_pull_off
                if pull_mode == 1:
                    pin_pull_up(self.name)
                elif pull_mode == 0:
                    pin_pull_off(self.name)
            case "pic16f84a":
                from pymcu.hal.pic14.pic16f84a_gpio import pin_pull_up, pin_pull_off
                if pull_mode == 1:
                    pin_pull_up(self.name)
                elif pull_mode == 0:
                    pin_pull_off(self.name)
            case "pic10f200":
                from pymcu.hal.pic12.pic10f200_gpio import pin_pull_up, pin_pull_off
                if pull_mode == 1:
                    pin_pull_up(self.name)
                elif pull_mode == 0:
                    pin_pull_off(self.name)
            case "pic18f45k50":
                from pymcu.hal.pic18.pic18f45k50_gpio import pin_pull_up, pin_pull_off
                if pull_mode == 1:
                    pin_pull_up(self.name)
                elif pull_mode == 0:
                    pin_pull_off(self.name)

    @inline
    def drive(self, strength: uint8):
        pass # Support depends on target chip

    @inline
    def irq(self, trigger: const = 3, handler: const = 0):
        match __CHIP__.name:
            case "pic16f628a":
                from pymcu.hal.pic14.pic16f628a_gpio import pin_irq_setup
                pin_irq_setup(self.name, trigger)
            case "pic16f877a":
                from pymcu.hal.pic14.pic16f877a_gpio import pin_irq_setup
                pin_irq_setup(self.name, trigger)
            case "pic16f84a":
                from pymcu.hal.pic14.pic16f84a_gpio import pin_irq_setup
                pin_irq_setup(self.name, trigger)
            case "pic16f18877":
                from pymcu.hal.pic14.pic16f18877_gpio import pin_irq_setup
                pin_irq_setup(self.name, trigger)
            case "pic18f45k50":
                from pymcu.hal.pic18.pic18f45k50_gpio import pin_irq_setup
                pin_irq_setup(self.name, trigger)
            case "pic10f200":
                raise CompileError("IRQ not supported on PIC10F200")

    @inline
    def pulse_in(self, state: uint8, timeout_us: uint16 = 1000) -> uint16:
        return 0

    @inline
    def mode(self, m: const = -1) -> uint8:
        if m != -1:
            match __CHIP__.name:
                case "pic16f18877":
                    from pymcu.hal.pic14.pic16f18877_gpio import pin_set_mode
                    pin_set_mode(self.name, m)
                case "pic16f628a":
                    from pymcu.hal.pic14.pic16f628a_gpio import pin_set_mode
                    pin_set_mode(self.name, m)
                case "pic16f877a":
                    from pymcu.hal.pic14.pic16f877a_gpio import pin_set_mode
                    pin_set_mode(self.name, m)
                case "pic16f84a":
                    from pymcu.hal.pic14.pic16f84a_gpio import pin_set_mode
                    pin_set_mode(self.name, m)
                case "pic10f200":
                    from pymcu.hal.pic12.pic10f200_gpio import pin_set_mode
                    pin_set_mode(self.name, m)
                case "pic18f45k50":
                    from pymcu.hal.pic18.pic18f45k50_gpio import pin_set_mode
                    pin_set_mode(self.name, m)

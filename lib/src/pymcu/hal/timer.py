# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------

from pymcu.chips import __CHIP__
from pymcu.types import uint8, uint16, const, inline

# ---- Unified Timer ZCA ----
# IRQ mode constants (Timer.IRQ_OVF, Timer.IRQ_COMPA) select the interrupt
# source for Timer.irq(). IRQ_OVF fires on counter overflow; IRQ_COMPA fires
# on a compare-match with OCRnA (requires set_compare() to have been called).
# Timer(n, prescaler) -- n is a compile-time constant; all methods @inline.
# The compiler folds both the chip dispatch and the timer-number dispatch at
# compile time, emitting only the instructions for the selected timer.
#
# AVR supports multiple numbered hardware timers (n=0, 1, 2).
# PIC chips only support n=0 (Timer0).
# Available prescaler values, resolution, and overflow rates depend on the
# target chip and the selected timer.
# CTC mode (compare-match interrupt) is configured via set_compare().

# noinspection PyProtectedMember
class Timer:  # noqa
    """Hardware timer, zero-cost abstraction (all methods @inline).

    ``n`` is a compile-time constant that selects the hardware timer.
    The compiler folds both the chip dispatch and the timer-number
    dispatch at compile time, emitting only the instructions for the
    selected timer.

    AVR supports multiple numbered timers (n=0, 1, 2); PIC supports
    only n=0. Available prescaler values, resolution, and overflow
    frequencies depend on the target chip and selected timer.
    """

    IRQ_OVF   = 1
    IRQ_COMPA = 2

    def __init__(self, n: const[uint8], prescaler: uint8):
        """Initialize a hardware timer.

        n:         compile-time timer number (e.g. 0, 1, 2).
        prescaler: clock prescaler value; valid values depend on the chip.
        """
        self._n = n
        # Store timer identity as a string so irq() can CT-fold on it
        # (string members are compile-time constants; numeric SRAM members are not).
        if n == 0:
            self._id = "t0"
        elif n == 1:
            self._id = "t1"
        elif n == 2:
            self._id = "t2"
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
                match n:
                    case 0:
                        from pymcu.hal._timer.atmega328p import timer0_init
                        timer0_init(prescaler)
                    case 1:
                        from pymcu.hal._timer.atmega328p import timer1_init
                        timer1_init(prescaler)
                    case 2:
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
    def start(self):
        """Start the timer by connecting its clock source."""
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
                match self._n:
                    case 0:
                        from pymcu.hal._timer.atmega328p import timer0_start
                        timer0_start()
                    case 1:
                        from pymcu.hal._timer.atmega328p import timer1_start
                        timer1_start()
                    case 2:
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
    def stop(self):
        """Stop the timer by disconnecting its clock source."""
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
                match self._n:
                    case 0:
                        from pymcu.hal._timer.atmega328p import timer0_stop
                        timer0_stop()
                    case 1:
                        from pymcu.hal._timer.atmega328p import timer1_stop
                        timer1_stop()
                    case 2:
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
    def clear(self):
        """Reset the timer counter register to zero."""
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
                match self._n:
                    case 0:
                        from pymcu.hal._timer.atmega328p import timer0_clear
                        timer0_clear()
                    case 1:
                        from pymcu.hal._timer.atmega328p import timer1_clear
                        timer1_clear()
                    case 2:
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

    @inline
    def set_compare(self, value: uint16):
        """Set the compare-match value and enable CTC mode.

        The timer resets to zero when its counter reaches this value,
        generating a compare-match interrupt at that instant.
        Call start() first, then set_compare() to arm the interrupt.
        """
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
                match self._n:
                    case 0:
                        from pymcu.hal._timer.atmega328p import timer0_set_compare
                        timer0_set_compare(value)
                    case 1:
                        from pymcu.hal._timer.atmega328p import timer1_set_compare
                        timer1_set_compare(value)
                    case 2:
                        from pymcu.hal._timer.atmega328p import timer2_set_compare
                        timer2_set_compare(value)

    @inline
    def overflow(self) -> uint8:
        """Return 1 if the timer overflow flag is set, 0 otherwise.

        The overflow flag is set when the counter wraps from its maximum
        value back to zero. Reading this flag does not clear it.
        """
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
                match self._n:
                    case 0:
                        from pymcu.hal._timer.atmega328p import timer0_overflow
                        return timer0_overflow()
                    case 1:
                        from pymcu.hal._timer.atmega328p import timer1_overflow
                        return timer1_overflow()
                    case 2:
                        from pymcu.hal._timer.atmega328p import timer2_overflow
                        return timer2_overflow()
        return 0

    @inline
    def irq(self, handler: const, mode: const = 1):
        """Register an interrupt handler for this timer.

        handler: compile-time function reference; automatically registered
                 at the timer overflow vector -- no @interrupt decorator needed.
        mode:    ``Timer.IRQ_OVF`` (default) for overflow interrupt.

        Enables the relevant interrupt mask bit and global interrupts (SEI).
        """
        # Use self._id (string member = CT constant) for compile_isr dispatch,
        # not self._n (numeric SRAM) which cannot CT-fold the vector placement.
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
                match self._id:
                    case "t0":
                        from pymcu.hal._timer.atmega328p import timer0_irq_setup
                        timer0_irq_setup(handler)
                    case "t1":
                        from pymcu.hal._timer.atmega328p import timer1_irq_setup
                        timer1_irq_setup(handler)
                    case "t2":
                        from pymcu.hal._timer.atmega328p import timer2_irq_setup
                        timer2_irq_setup(handler)

# -----------------------------------------------------------------------------
# PyMCU asyncio -- cooperative coroutines compiled to native state machines.
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------
# `import asyncio` is REQUIRED to use `async def` / `await` (mirroring how CPython
# pairs the keywords with the asyncio runtime). The PyMCU frontend rewrites each
# `async def` into a zero-cost state-machine class with a poll() method; `await
# asyncio.sleep(...)` / `asyncio.sleep_ms(...)` become non-blocking waits against
# the monotonic `ticks()` counter below. sleep()/sleep_ms() have marker bodies --
# they are only ever awaited, and the transform supplies the real wait code.
from pymcu.types import uint32, ptr, inline
from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError


@inline
def ticks() -> uint32:
    """Free-running monotonic microsecond counter backing every await.

    rp2040/rp2350: hardware TIMER (1 MHz), exact 1 us resolution.
    avr (ATmega): Timer0 overflow counter combined with TCNT0 -- the same
      counter pymcu.time.micros() reads.  Resolution is 4 us at 16 MHz /
      prescaler 64 (each Timer0 tick is 4 us, each overflow 1024 us); the
      counter wraps every ~71 minutes, which uint32 subtraction handles
      correctly.  It only advances once millis_init() has armed the overflow
      ISR -- `pymcu build` injects that call when the sources contain an
      `async def`.  Timer0 is therefore reserved: do not also drive PWM from
      Timer0 in an async program.
    Everything else (ATtiny, PIC, RISC-V) has no time base at all.  A frozen
      counter would leave the wait condition `ticks() - start < duration`
      permanently unmet, so every await would block forever -- those targets
      raise CompileError here instead of producing firmware that hangs.
    """
    if __CHIP__.name == "rp2040":
        t: ptr[uint32] = ptr(0x40054028)        # TIMER.TIMERAWL
        return t.value
    elif __CHIP__.name == "rp2350":
        t2: ptr[uint32] = ptr(0x400B0028)       # TIMER0.TIMERAWL
        return t2.value
    elif __CHIP__.arch == "avr":
        if __CHIP__.name.startswith("attiny"):
            raise CompileError("async needs a timebase; not available on attiny yet: millis/micros are ATmega-only, so every await would block forever. Use pymcu.time.delay_ms() instead.")
        else:
            # Via hal.timer, the same counter pymcu.time.micros() reads.
            from pymcu.hal.timer import micros as _micros_avr
            return _micros_avr()
    else:
        raise CompileError("async needs a timebase; not available on this architecture yet: only ATmega AVR (Timer0) and RP2040/RP2350 (hardware TIMER) have one, so every await would block forever. Use pymcu.time.delay_ms() instead.")


def sleep(seconds: uint32):
    """`await asyncio.sleep(seconds)` -- suspend the coroutine for `seconds`.
    Marker only; the async transform emits the non-blocking wait."""
    pass


def sleep_ms(ms: uint32):
    """`await asyncio.sleep_ms(ms)` -- suspend the coroutine for `ms` milliseconds.
    Marker only; the async transform emits the non-blocking wait."""
    pass


@inline
def run(coro):
    """Drive one coroutine to completion (blocking poll loop)."""
    while coro.poll() == 1:
        pass


@inline
def gather(a, b):
    """Drive two coroutines concurrently until both complete.

    The arity is fixed at compile time (coroutine state machines have no runtime
    representation to put in an array); nest gathers or add explicit poll loops
    for more tasks.
    """
    ra: uint32 = 1
    rb: uint32 = 1
    while (ra == 1) or (rb == 1):
        if ra == 1:
            ra = a.poll()
        if rb == 1:
            rb = b.poll()

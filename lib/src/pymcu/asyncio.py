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


@inline
def ticks() -> uint32:
    """Free-running monotonic microsecond counter (hardware TIMER, 1 MHz)."""
    if __CHIP__.name == "rp2040":
        t: ptr[uint32] = ptr(0x40054028)        # TIMER.TIMERAWL
        return t.value
    elif __CHIP__.name == "rp2350":
        t2: ptr[uint32] = ptr(0x400B0028)       # TIMER0.TIMERAWL
        return t2.value
    return 0


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

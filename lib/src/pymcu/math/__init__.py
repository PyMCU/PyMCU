# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# math -- integer utilities for embedded use
#
#   map_range(x, in_lo, in_hi, out_lo, out_hi) -- linear mapping between ranges
#   constrain(x, lo, hi)                        -- clamp a value to [lo, hi]
#   floor(x) / ceil(x) / trunc(x)               -- a float to the integer below,
#                                                  above, or toward zero
#   pow(x, y)                                   -- x raised to y, software float
#   sqrt / log / exp / pow / radians            -- float32, from the runtime helpers
#   sin cos tan atan atan2 asin acos            -- float32 (Cephes); ValueError outside the domain
#   sinh cosh tanh asinh acosh atanh            -- float32, from exp and log
#   log2 log10 exp2 cbrt expm1 log1p            -- float32
#   fabs copysign fmod hypot degrees            -- float32
#   gcd lcm isqrt factorial                     -- 32-bit integers (factorial up to 12)
#   pi e tau inf nan                            -- const[float]: folded, no storage
#   isnan isinf isfinite                        -- bit tests
#   gamma erf comb prod fsum ...                -- not provided; each says why when called

from pymcu.types import uint8, uint16, uint32, int16, int32, inline
from pymcu.exceptions import CompileError


@inline
def map_range(x: uint16, in_lo: uint16, in_hi: uint16, out_lo: uint16, out_hi: uint16) -> uint16:
    """Map x from [in_lo, in_hi] to [out_lo, out_hi].

    Equivalent to Arduino's map() function.  Uses integer arithmetic; the
    result is truncated (not rounded).  Safe when in_hi != in_lo.
    """
    return (x - in_lo) * (out_hi - out_lo) // (in_hi - in_lo) + out_lo


@inline
def constrain(x: uint16, lo: uint16, hi: uint16) -> uint16:
    """Clamp x to the closed interval [lo, hi].

    Equivalent to Arduino's constrain() macro.
    """
    if x < lo:
        return lo
    if x > hi:
        return hi
    return x


# floor / ceil / trunc.
#
# Each has an integer overload returning its argument. An integer is already whole, so
# `math.floor(count)` costs nothing instead of pulling the software float routines in behind
# the caller's back -- on this target the float library is the largest single thing the stdlib
# can add to an image, so widening an integer to get an integer back is not a free convenience.
#
# The float overloads build on int32(x), which truncates toward zero, and adjust by one only
# when x actually had a fractional part. `_as_float(t) != x` is that test: t is x with the
# fraction removed, so the two differ exactly when there was one.
#
# _as_float exists because writing `float(t)` directly inside an OVERLOADED @inline resolves
# to the integer overload of the enclosing function instead of to the cast, and the build
# fails with "function 'floor' is recursive". A non-overloaded helper is not affected. See
# PyMCU#182; when that is fixed the helper can be inlined back into its two call sites and
# nothing else changes.
#
# PyMCU#182 also means the integer overloads cannot currently be SELECTED: an integer
# argument picks the float form and pays for the software float routines, 476 bytes against
# the 142 the integer path costs when it is the only candidate. They are written here anyway,
# because they are what the code should say and the day #182 is fixed they start working with
# no change to this file.


@inline
def _as_float(v: int32) -> float:
    return float(v)


@inline
def trunc(x: int32) -> int32:
    """The integral part of x. An integer is already integral."""
    return x


@inline
def trunc(x: float) -> int32:
    """The integral part of x, toward zero. trunc(2.7) is 2, trunc(-2.7) is -2."""
    return int32(x)


@inline
def floor(x: int32) -> int32:
    """The largest integer not greater than x. An integer is its own floor."""
    return x


@inline
def floor(x: float) -> int32:
    """The largest integer not greater than x. floor(2.7) is 2, floor(-2.7) is -3."""
    t: int32 = int32(x)
    if x < 0.0:
        if _as_float(t) != x:
            t = t - 1
    return t


@inline
def ceil(x: int32) -> int32:
    """The smallest integer not less than x. An integer is its own ceiling."""
    return x


@inline
def ceil(x: float) -> int32:
    """The smallest integer not less than x. ceil(2.1) is 3, ceil(-2.1) is -2."""
    t: int32 = int32(x)
    if x > 0.0:
        if _as_float(t) != x:
            t = t + 1
    return t


# pow(x, y) -- a real exponent on the software float.
#
# There is no powf in the runtime, so this is the textbook decomposition
# x ** y = 2 ** (y * log2(x)) built from the primitives the float library already
# provides. log2 pulls the exponent out by halving/doubling x into [1, 2), where the
# atanh series ln(m) = 2*(t + t^3/3 + t^5/5 + ...) with t = (m-1)/(m+1) converges
# fast because |t| <= 1/3; the fractional power of two is exp(f * ln2) Taylor, which
# converges fast because |z| <= ln2. The integer part of the exponent is exact
# doubling/halving, so precision is decided by the two series, not by the scale.
#
# Domain follows CPython's math.pow: y == 0 is 1.0 (0**0 included), x == 0 with a
# positive y is 0.0, x == 0 with negative y and x < 0 raise ValueError.
#
# Cost note: only programs that CALL it pay for it. The series keep the software
# float routines in the image, which a float program has already paid for.
#
# The implementation itself lives in the compiler's embedded runtime helpers
# (__pymcu_powf) so the bare builtin `pow(x, y)` -- which a program can call
# without importing math -- lowers to the same code. One algorithm, one place.
@inline
def pow(x: float, y: float) -> float:
    """x raised to y. Software-float power: 2 ** (y * log2(x))."""
    return __pymcu_powf(x, y)


# sqrt / log / exp / radians -- and ONLY these four.
#
# Each one is here because a library measured in this tree stops without it, named by
# the diagnostic the compiler gave on main:
#
#   sqrt     adafruit_max31865   "call to undefined function 'math_sqrt'"
#   log      adafruit_thermistor "call to undefined function 'math_log'"
#   exp      adafruit_sgp30      "cannot import 'exp' from 'math'"
#   radians  adafruit_mpu6050    "cannot import 'radians' from 'math'"
#            adafruit_lsm6ds     same
#
# log10 and degrees are NOT here. A sweep of 66 upstream CircuitPython and MicroPython
# libraries found neither name in any of them, and a shim exists because something needs
# it, not because the module has it upstream. They go in the day a library asks.
#
# Same arrangement as pow(): the algorithm lives in the compiler's embedded runtime
# helpers (__pymcu_sqrtf, __pymcu_logf, __pymcu_expf) and these are thin @inline
# wrappers, so there is one implementation and it lowers as a REAL subroutine, once,
# and only in a program that calls it. `import math` with no call costs 0 bytes, and
# each function's own body (measured on top of the 910 bytes of software float that any
# float arithmetic already pays) is: radians 8, exp 918, log 1062, sqrt 1452.
#
# The values are float32, not CPython's float64: that is the width this target has, and
# it is the width every other float in a PyMCU program already carries.
@inline
def sqrt(x: float) -> float:
    """The square root of x. Raises ValueError for a negative argument, as CPython does."""
    return __pymcu_sqrtf(x)


@inline
def log(x: float) -> float:
    """The natural logarithm of x. Raises ValueError for x <= 0, as CPython does."""
    return __pymcu_logf(x)


@inline
def exp(x: float) -> float:
    """e raised to x."""
    return __pymcu_expf(x)


# radians. Pure scaling, so no runtime helper: the multiply is smaller than a call would
# be, and it folds outright for a constant angle -- 8 bytes on top of the float runtime.
#
# math.pi and math.e are NOT defined here, and that is deliberate. A module-level FLOAT
# constant in an imported module becomes storage that nothing initialises: `math.pi` read
# back 0.0 instead of refusing, which is worse than not having it. Leaving the names out
# keeps the honest "Unknown module member" until the module-init gap is closed.


@inline
def radians(x: float) -> float:
    """x degrees in radians."""
    return x * 0.017453292519943295


# isnan/isinf/isfinite (P2 AVR gaps bundle, item 5): a float32's exponent field is all
# ones (255) for both infinity and NaN, distinguished by the mantissa (zero for
# infinity, nonzero for NaN) -- the same bit-pattern read _f32_repr (uart_text.py) has
# used since it was written to print "inf"/"nan" correctly. No runtime helper: three
# bit operations apiece, folds for a constant argument like any other @inline body.
@inline
def isnan(x: float) -> bool:
    """True if x is a NaN, as CPython's math.isnan does."""
    bits: uint32 = bitcast(uint32, x)
    return ((bits >> 23) & 255) == 255 and (bits & 8388607) != 0


@inline
def isinf(x: float) -> bool:
    """True if x is positive or negative infinity, as CPython's math.isinf does."""
    bits: uint32 = bitcast(uint32, x)
    return ((bits >> 23) & 255) == 255 and (bits & 8388607) == 0


@inline
def isfinite(x: float) -> bool:
    """True if x is neither an infinity nor a NaN, as CPython's math.isfinite does."""
    bits: uint32 = bitcast(uint32, x)
    return ((bits >> 23) & 255) != 255


# -----------------------------------------------------------------------------
# The rest of CPython's math that a float32 target can honestly offer.
#
# Everything below is float32, like sqrt/log/exp above, and is plain Python: a function
# nothing calls costs nothing, and the constants are `const[float]`, which fold into the
# reading expression and own no storage.
#
# ACCURACY. The trigonometric functions are Cephes' single-precision kernels (the ones libm's
# sinf/cosf/tanf/atanf descend from): about 1 ulp of float32 (6e-8 relative) after argument
# reduction, which is done with a three-part pi/4 so that it stays accurate while |x| is small.
# Past |x| of a few thousand a float32 argument has no bits left for the reduction to work
# with, and that is true of every float32 sin; math.sin(1e6) is not attempted.
#
# Domain errors raise ValueError with CPython's text ("math domain error"). A function nothing
# calls does not make the program carry the unhandled-exception report: the compiler counts a
# raise with a message only in functions the program can reach.

pi: const[float] = 3.141592653589793
e: const[float] = 2.718281828459045
tau: const[float] = 6.283185307179586
inf: const[float] = float("inf")
nan: const[float] = float("nan")


def fabs(x: float) -> float:
    """The absolute value of x, as a float."""
    if x < 0.0:
        return 0.0 - x
    return x


def copysign(x: float, y: float) -> float:
    """x with the sign of y (the sign bit is read, so -0.0 counts as negative)."""
    bx: uint32 = bitcast(uint32, x)
    by: uint32 = bitcast(uint32, y)
    return bitcast(float, (bx & 2147483647) | (by & 2147483648))


@inline
def degrees(x: float) -> float:
    """x radians in degrees."""
    return x * 57.29577951308232


def hypot(x: float, y: float) -> float:
    """sqrt(x*x + y*y), scaled so that the squares cannot overflow."""
    a: float = fabs(x)
    b: float = fabs(y)
    if a < b:
        t: float = a
        a = b
        b = t
    if a == 0.0:
        return 0.0
    if isinf(a):
        return a
    r: float = b / a
    return a * sqrt(1.0 + r * r)


def fmod(x: float, y: float) -> float:
    """The remainder of x / y with the sign of x, as C's fmod gives it."""
    if y == 0.0:
        raise ValueError("math domain error")
    if isinf(x):
        raise ValueError("math domain error")
    if isinf(y):
        return x
    q: float = x / y
    if q > 2000000000.0 or q < -2000000000.0:
        raise ValueError("math.fmod: quotient too large for float32")
    return x - float(int32(q)) * y


# sin / cos / tan -- Cephes sinf/cosf/tanf. The octant j picks the polynomial and the sign;
# pi/4 is split in three so that x - j*pi/4 loses nothing it needs.
def _octant_reduce(x: float, j: int32) -> float:
    y: float = float(j)
    return ((x - y * 0.78515625) - y * 2.4187564849853515625e-4) - y * 3.77489497744594108e-8


def sin(x: float) -> float:
    """The sine of x, in radians."""
    sign: float = 1.0
    if x < 0.0:
        x = 0.0 - x
        sign = -1.0
    j: int32 = int32(x * 1.27323954473516)
    if (j & 1) != 0:
        j = j + 1
    z: float = _octant_reduce(x, j)
    j = j & 7
    if j > 3:
        sign = 0.0 - sign
        j = j - 4
    zz: float = z * z
    r: float
    if j == 1 or j == 2:
        r = (((2.443315711809948e-5 * zz - 1.388731625493765e-3) * zz + 4.166664568298827e-2) * zz * zz) - 0.5 * zz + 1.0
    else:
        r = (((-1.9515295891e-4 * zz + 8.3321608736e-3) * zz - 1.6666654611e-1) * zz * z) + z
    return sign * r


def cos(x: float) -> float:
    """The cosine of x, in radians."""
    sign: float = 1.0
    if x < 0.0:
        x = 0.0 - x
    j: int32 = int32(x * 1.27323954473516)
    if (j & 1) != 0:
        j = j + 1
    z: float = _octant_reduce(x, j)
    j = j & 7
    if j > 3:
        j = j - 4
        sign = -1.0
    if j > 1:
        sign = 0.0 - sign
    zz: float = z * z
    r: float
    if j == 1 or j == 2:
        r = (((-1.9515295891e-4 * zz + 8.3321608736e-3) * zz - 1.6666654611e-1) * zz * z) + z
    else:
        r = (((2.443315711809948e-5 * zz - 1.388731625493765e-3) * zz + 4.166664568298827e-2) * zz * zz) - 0.5 * zz + 1.0
    return sign * r


def tan(x: float) -> float:
    """The tangent of x, in radians."""
    sign: float = 1.0
    if x < 0.0:
        x = 0.0 - x
        sign = -1.0
    j: int32 = int32(x * 1.27323954473516)
    if (j & 1) != 0:
        j = j + 1
    z: float = _octant_reduce(x, j)
    zz: float = z * z
    r: float = z
    if x > 1.0e-4:
        r = (((((9.38540185543e-3 * zz + 3.11992232697e-3) * zz + 2.44301354525e-2) * zz
               + 5.34112807005e-2) * zz + 1.33387994085e-1) * zz + 3.33331568548e-1) * zz * z + z
    if (j & 2) != 0:
        r = -1.0 / r
    return sign * r


# atan / atan2 / asin / acos -- Cephes atanf, reduced to [0, tan(pi/8)].
def atan(x: float) -> float:
    """The arc tangent of x, in radians."""
    sign: float = 1.0
    if x < 0.0:
        x = 0.0 - x
        sign = -1.0
    base: float = 0.0
    if x > 2.414213562373095:
        base = 1.5707963267948966
        x = -1.0 / x
    elif x > 0.4142135623730950:
        base = 0.7853981633974483
        x = (x - 1.0) / (x + 1.0)
    z: float = x * x
    base = base + (((((8.05374449538e-2 * z - 1.38776856032e-1) * z + 1.99777106478e-1) * z
                     - 3.33329491539e-1) * z * x) + x)
    return sign * base


def atan2(y: float, x: float) -> float:
    """The arc tangent of y / x, in radians, in the quadrant of (x, y)."""
    if x == 0.0:
        if y > 0.0:
            return 1.5707963267948966
        if y < 0.0:
            return -1.5707963267948966
        return 0.0
    a: float = atan(y / x)
    if x > 0.0:
        return a
    if y >= 0.0:
        return a + 3.141592653589793
    return a - 3.141592653589793


def asin(x: float) -> float:
    """The arc sine of x, in radians. x must be in [-1, 1]."""
    if x > 1.0 or x < -1.0:
        raise ValueError("math domain error")
    return atan2(x, sqrt(1.0 - x * x))


def acos(x: float) -> float:
    """The arc cosine of x, in radians. x must be in [-1, 1]."""
    if x > 1.0 or x < -1.0:
        raise ValueError("math domain error")
    return atan2(sqrt(1.0 - x * x), x)


# Hyperbolic functions, from exp. sinh and tanh switch to a series near zero, where
# exp(x) - exp(-x) is the difference of two nearly equal numbers.
def sinh(x: float) -> float:
    """The hyperbolic sine of x."""
    if x < 0.5 and x > -0.5:
        z: float = x * x
        return x + x * z * (0.16666666666666666 + z * (0.008333333333333333 + z * 0.0001984126984126984))
    return (exp(x) - exp(0.0 - x)) * 0.5


def cosh(x: float) -> float:
    """The hyperbolic cosine of x."""
    return (exp(x) + exp(0.0 - x)) * 0.5


def tanh(x: float) -> float:
    """The hyperbolic tangent of x."""
    if x > 9.0:
        return 1.0
    if x < -9.0:
        return -1.0
    return sinh(x) / cosh(x)


def asinh(x: float) -> float:
    """The inverse hyperbolic sine of x."""
    a: float = fabs(x)
    r: float = log(a + sqrt(a * a + 1.0))
    if x < 0.0:
        return 0.0 - r
    return r


def acosh(x: float) -> float:
    """The inverse hyperbolic cosine of x. x must be at least 1."""
    if x < 1.0:
        raise ValueError("math domain error")
    return log(x + sqrt(x * x - 1.0))


def atanh(x: float) -> float:
    """The inverse hyperbolic tangent of x. x must be in (-1, 1)."""
    if x >= 1.0 or x <= -1.0:
        raise ValueError("math domain error")
    return 0.5 * log((1.0 + x) / (1.0 - x))


@inline
def log2(x: float) -> float:
    """The base-2 logarithm of x. Raises ValueError for x <= 0."""
    return log(x) * 1.4426950408889634


@inline
def log10(x: float) -> float:
    """The base-10 logarithm of x. Raises ValueError for x <= 0."""
    return log(x) * 0.4342944819032518


@inline
def exp2(x: float) -> float:
    """2 raised to x."""
    return exp(x * 0.6931471805599453)


def cbrt(x: float) -> float:
    """The cube root of x, real for a negative x as well."""
    if x == 0.0:
        return 0.0
    a: float = fabs(x)
    r: float = exp(log(a) * 0.3333333333333333)
    r = r - (r * r * r - a) / (3.0 * r * r)
    if x < 0.0:
        return 0.0 - r
    return r


def expm1(x: float) -> float:
    """exp(x) - 1, kept accurate for small x where the subtraction would lose it."""
    if x < 0.1 and x > -0.1:
        return x * (1.0 + x * (0.5 + x * (0.16666666666666666 + x * (0.041666666666666664
                    + x * 0.008333333333333333))))
    return exp(x) - 1.0


def log1p(x: float) -> float:
    """log(1 + x), kept accurate for small x. Raises ValueError for x <= -1."""
    if x <= -1.0:
        raise ValueError("math domain error")
    if x < 0.1 and x > -0.1:
        t: float = x / (2.0 + x)
        z: float = t * t
        return 2.0 * t * (1.0 + z * (0.3333333333333333 + z * (0.2 + z * 0.14285714285714285)))
    return log(1.0 + x)


def gcd(a: int32, b: int32) -> int32:
    """The greatest common divisor of a and b (two arguments; 32-bit integers)."""
    if a < 0:
        a = 0 - a
    if b < 0:
        b = 0 - b
    while b != 0:
        t: int32 = a % b
        a = b
        b = t
    return a


def lcm(a: int32, b: int32) -> int32:
    """The least common multiple of a and b (two arguments; 32-bit integers)."""
    if a == 0 or b == 0:
        return 0
    g: int32 = gcd(a, b)
    r: int32 = (a // g) * b
    if r < 0:
        return 0 - r
    return r


def isqrt(n: int32) -> int32:
    """The integer square root of n: the largest r with r * r <= n."""
    if n < 0:
        raise ValueError("isqrt() argument must be nonnegative")
    r: int32 = int32(sqrt(float(n)))
    while r * r > n:
        r = r - 1
    while (r + 1) * (r + 1) <= n and (r + 1) * (r + 1) > 0:
        r = r + 1
    return r


def factorial(n: int32) -> int32:
    """n!, for n up to 12: 13! does not fit in 32 bits, and a result that wrapped would be
    a wrong answer, so it raises instead."""
    if n < 0:
        raise ValueError("factorial() not defined for negative values")
    if n > 12:
        raise ValueError("factorial() result does not fit in 32 bits")
    r: int32 = 1
    i: int32 = 2
    while i <= n:
        r = r * i
        i = i + 1
    return r


# What CPython's math has and this one deliberately does not. Each name is defined so that
# using it says WHY at the call, rather than "Unknown module member"; an unused one costs nothing.
@inline
def gamma(*args):
    raise CompileError("math.gamma is not provided: it needs a Lanczos table and a reflection "
                       "branch that float32 does not carry accurately. Compute it on the host.")


@inline
def lgamma(*args):
    raise CompileError("math.lgamma is not provided (see math.gamma).")


@inline
def erf(*args):
    raise CompileError("math.erf is not provided: a float32 rational approximation is only "
                       "good to 1e-7 absolute, which is not what math.erf promises.")


@inline
def erfc(*args):
    raise CompileError("math.erfc is not provided (see math.erf).")


@inline
def comb(*args):
    raise CompileError("math.comb is not provided: its result does not fit in 32 bits for "
                       "any n worth asking about.")


@inline
def perm(*args):
    raise CompileError("math.perm is not provided (see math.comb).")


@inline
def prod(*args):
    raise CompileError("math.prod takes an iterable; multiply in a loop.")


@inline
def fsum(*args):
    raise CompileError("math.fsum takes an iterable and exists to be exact in float64; "
                       "add in a loop.")


@inline
def dist(*args):
    raise CompileError("math.dist takes sequences; use math.hypot for two coordinates.")


@inline
def sumprod(*args):
    raise CompileError("math.sumprod takes iterables; accumulate in a loop.")


@inline
def fma(*args):
    raise CompileError("math.fma is not provided: it promises a single rounding, which "
                       "float32 software arithmetic cannot give.")


@inline
def nextafter(*args):
    raise CompileError("math.nextafter is not provided.")


@inline
def ulp(*args):
    raise CompileError("math.ulp is not provided.")


@inline
def remainder(*args):
    raise CompileError("math.remainder is not provided; math.fmod is.")


@inline
def ldexp(*args):
    raise CompileError("math.ldexp is not provided; multiply by 2.0 ** n instead.")


@inline
def frexp(*args):
    raise CompileError("math.frexp is not provided.")


@inline
def modf(*args):
    raise CompileError("math.modf is not provided; use x - math.trunc(x) and math.trunc(x).")


@inline
def isclose(*args):
    raise CompileError("math.isclose is not provided; compare abs(a - b) with a tolerance.")

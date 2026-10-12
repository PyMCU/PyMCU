/*
 * -----------------------------------------------------------------------------
 * PyMCU RISC-V backend: IEEE 754 binary32 arithmetic for cores with no FPU
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
 * -----------------------------------------------------------------------------
 *
 * A float is its 32 bits in an integer register. Round to nearest even, subnormals,
 * infinities and NaN (any NaN comes out as the canonical quiet NaN). Only 32-bit
 * integer operations are used, so the compiled code needs no libgcc: the backend
 * emits tools/gen_softfloat.py's translation of this file when a program uses floats.
 *
 * A working mantissa carries 3 extra low bits (guard, round, sticky) under a hidden
 * bit at position 26; "e" is the biased exponent of that hidden bit.
 */
typedef unsigned int u32;
typedef int i32;

#define QNAN 0x7FC00000u
#define SIGN 0x80000000u

static u32 __pymcu_sf_jam(u32 m, u32 n)
{
    if (n == 0) return m;
    if (n >= 32) return m != 0;
    return (m >> n) | ((m << (32 - n)) != 0);
}

/* sign | exp | frac from a normalised working mantissa (hidden bit at 26, 3 extra bits). */
static u32 __pymcu_sf_pack(u32 sign, i32 e, u32 m)
{
    u32 inc;
    if (m == 0) return sign;
    if (m >= (1u << 27)) { m = __pymcu_sf_jam(m, 1); e++; }
    while (m < (1u << 26) && e > 1) { m <<= 1; e--; }
    if (e <= 0) { m = __pymcu_sf_jam(m, (u32)(1 - e)); e = 1; }
    inc = ((m & 7) > 4) || ((m & 15) == 12);
    m = (m >> 3) + inc;
    if (m >= (1u << 24)) { m >>= 1; e++; }
    if (e >= 255) return sign | 0x7F800000u;
    return sign | ((m >= (1u << 23) ? (u32)e : 0u) << 23) | (m & 0x7FFFFFu);
}

static int __pymcu_sf_is_nan(u32 a) { return (a & 0x7FFFFFFFu) > 0x7F800000u; }
static int __pymcu_sf_is_inf(u32 a) { return (a & 0x7FFFFFFFu) == 0x7F800000u; }

/* Working mantissa and exponent of a finite value. */
static void __pymcu_sf_unpack(u32 a, i32 *e, u32 *m)
{
    u32 ex = (a >> 23) & 0xFF, fr = a & 0x7FFFFF;
    if (ex == 0) { *e = 1; *m = fr << 3; }
    else { *e = (i32)ex; *m = (fr | 0x800000u) << 3; }
}

static u32 __pymcu_sf_addsub(u32 a, u32 b)           /* a + b, with b's sign already applied */
{
    i32 ea, eb;
    u32 ma, mb, sa = a & SIGN, sb = b & SIGN, m, t;
    if (__pymcu_sf_is_nan(a) || __pymcu_sf_is_nan(b)) return QNAN;
    if (__pymcu_sf_is_inf(a)) return (__pymcu_sf_is_inf(b) && sa != sb) ? QNAN : a;
    if (__pymcu_sf_is_inf(b)) return b;
    __pymcu_sf_unpack(a, &ea, &ma);
    __pymcu_sf_unpack(b, &eb, &mb);
    if (ea < eb || (ea == eb && ma < mb)) {
        t = a; a = b; b = t; t = sa; sa = sb; sb = t;
        ea ^= eb; eb ^= ea; ea ^= eb;
        t = ma; ma = mb; mb = t;
    }
    mb = __pymcu_sf_jam(mb, (u32)(ea - eb));
    if (sa == sb) m = ma + mb;
    else m = ma - mb;
    if (m == 0) return (sa == sb) ? sa : 0;       /* x + (-x) is +0 */
    return __pymcu_sf_pack(sa, ea, m);
}

u32 __pymcu_fadd(u32 a, u32 b) { return __pymcu_sf_addsub(a, b); }
u32 __pymcu_fsub(u32 a, u32 b) { return __pymcu_sf_addsub(a, b ^ SIGN); }

/* 24x24 -> 48 bit product as hi:lo, out of 16-bit pieces. */
static void __pymcu_sf_mul24(u32 a, u32 b, u32 *hi, u32 *lo)
{
    u32 a0 = a & 0xFFFF, a1 = a >> 16, b0 = b & 0xFFFF, b1 = b >> 16;
    u32 p00 = a0 * b0, p01 = a0 * b1, p10 = a1 * b0, p11 = a1 * b1;
    u32 mid = (p00 >> 16) + (p01 & 0xFFFF) + (p10 & 0xFFFF);
    *lo = (p00 & 0xFFFF) | (mid << 16);
    *hi = p11 + (p01 >> 16) + (p10 >> 16) + (mid >> 16);
}

/* Normalised 24-bit mantissa (hidden bit at 23) and exponent of a finite nonzero value. */
static void __pymcu_sf_unpack_norm(u32 a, i32 *e, u32 *m)
{
    u32 ex = (a >> 23) & 0xFF, fr = a & 0x7FFFFF;
    if (ex == 0) {
        i32 s = 1;
        while (!(fr & 0x800000u)) { fr <<= 1; s--; }
        *e = s; *m = fr;
    } else { *e = (i32)ex; *m = fr | 0x800000u; }
}

u32 __pymcu_fmul(u32 a, u32 b)
{
    u32 sign = (a ^ b) & SIGN, ma, mb, hi, lo, m;
    i32 ea, eb;
    if (__pymcu_sf_is_nan(a) || __pymcu_sf_is_nan(b)) return QNAN;
    if (__pymcu_sf_is_inf(a) || __pymcu_sf_is_inf(b)) {
        if ((a & 0x7FFFFFFFu) == 0 || (b & 0x7FFFFFFFu) == 0) return QNAN;
        return sign | 0x7F800000u;
    }
    if ((a & 0x7FFFFFFFu) == 0 || (b & 0x7FFFFFFFu) == 0) return sign;
    __pymcu_sf_unpack_norm(a, &ea, &ma);
    __pymcu_sf_unpack_norm(b, &eb, &mb);
    __pymcu_sf_mul24(ma, mb, &hi, &lo);                      /* product in [2^46, 2^48) */
    m = (hi << 11) | (lo >> 21);                  /* bits 47..21: hidden bit at 26 or 27 */
    if (lo & 0x1FFFFFu) m |= 1;
    return __pymcu_sf_pack(sign, ea + eb - 127 + 1, m);      /* +1: hidden bit of the product is at 46/47 */
}

u32 __pymcu_fdiv(u32 a, u32 b)
{
    u32 sign = (a ^ b) & SIGN, ma, mb, q = 0, r;
    i32 ea, eb, i;
    if (__pymcu_sf_is_nan(a) || __pymcu_sf_is_nan(b)) return QNAN;
    if (__pymcu_sf_is_inf(a)) return __pymcu_sf_is_inf(b) ? QNAN : (sign | 0x7F800000u);
    if (__pymcu_sf_is_inf(b)) return sign;
    if ((b & 0x7FFFFFFFu) == 0) return ((a & 0x7FFFFFFFu) == 0) ? QNAN : (sign | 0x7F800000u);
    if ((a & 0x7FFFFFFFu) == 0) return sign;
    __pymcu_sf_unpack_norm(a, &ea, &ma);
    __pymcu_sf_unpack_norm(b, &eb, &mb);
    if (ma < mb) { ma <<= 1; ea--; }              /* quotient in [1, 2) */
    r = ma;
    for (i = 0; i < 27; i++) {
        q <<= 1;
        if (r >= mb) { r -= mb; q |= 1; }
        r <<= 1;
    }
    if (r) q |= 1;
    return __pymcu_sf_pack(sign, ea - eb + 127, q);
}

/* -1 less, 0 equal, 1 greater, 2 unordered. */
int __pymcu_fcmp(u32 a, u32 b)
{
    u32 ma = a & 0x7FFFFFFFu, mb = b & 0x7FFFFFFFu;
    if (__pymcu_sf_is_nan(a) || __pymcu_sf_is_nan(b)) return 2;
    if (ma == 0 && mb == 0) return 0;
    if ((a ^ b) & SIGN) return (a & SIGN) ? -1 : 1;
    if (ma == mb) return 0;
    if (a & SIGN) return ma > mb ? -1 : 1;
    return ma > mb ? 1 : -1;
}

u32 __pymcu_i2f(i32 v)
{
    u32 sign = 0, u, m;
    i32 n;
    if (v == 0) return 0;
    if (v < 0) { sign = SIGN; u = 0u - (u32)v; } else u = (u32)v;
    n = 31;
    while (!(u >> n)) n--;
    if (n > 26) m = __pymcu_sf_jam(u, (u32)(n - 26)); else m = u << (26 - n);
    return __pymcu_sf_pack(sign, 127 + n, m);
}

u32 __pymcu_u2f(u32 u)
{
    u32 m;
    i32 n;
    if (u == 0) return 0;
    n = 31;
    while (!(u >> n)) n--;
    if (n > 26) m = __pymcu_sf_jam(u, (u32)(n - 26)); else m = u << (26 - n);
    return __pymcu_sf_pack(0, 127 + n, m);
}

/* Truncate toward zero; out of range saturates, NaN is 0. */
i32 __pymcu_f2i(u32 a)
{
    i32 e;
    u32 m, v;
    if (__pymcu_sf_is_nan(a)) return 0;
    e = (i32)((a >> 23) & 0xFF) - 127;
    if (e < 0) return 0;
    if (e >= 31) return (a & SIGN) ? (i32)0x80000000u : 0x7FFFFFFF;
    m = (a & 0x7FFFFF) | 0x800000u;
    v = (e <= 23) ? (m >> (23 - e)) : (m << (e - 23));
    return (a & SIGN) ? -(i32)v : (i32)v;
}

/* A negative value wraps the way the signed conversion does; an overflow saturates. */
u32 __pymcu_f2u(u32 a)
{
    i32 e;
    u32 m;
    if (__pymcu_sf_is_nan(a)) return 0;
    if (a & SIGN) return (u32)__pymcu_f2i(a);
    e = (i32)((a >> 23) & 0xFF) - 127;
    if (e < 0) return 0;
    if (e >= 32) return 0xFFFFFFFFu;
    m = (a & 0x7FFFFF) | 0x800000u;
    return (e <= 23) ? (m >> (23 - e)) : (m << (e - 23));
}

/* Host check: every routine against the hardware's float, bit for bit (NaNs compared as NaNs). */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <stdint.h>
#include "softfloat.c"

static float f(u32 x) { float r; memcpy(&r, &x, 4); return r; }
static u32 u(float x) { u32 r; memcpy(&r, &x, 4); return r; }
static u32 rng_state = 12345;
static u32 rnd(void) { rng_state ^= rng_state << 13; rng_state ^= rng_state >> 17; rng_state ^= rng_state << 5; return rng_state; }

static u32 pick(void)
{
    static const u32 special[] = {0, 0x80000000u, 0x3F800000u, 0xBF800000u, 0x7F800000u, 0xFF800000u,
        0x7FC00000u, 0x00000001u, 0x007FFFFFu, 0x00800000u, 0x7F7FFFFFu, 0x80000001u, 0xFF7FFFFFu,
        0x3F7FFFFFu, 0x40000000u, 0x4B000000u, 0x4B7FFFFFu, 0x4EFFFFFFu, 0x7F000000u, 0x00FFFFFFu};
    switch (rnd() % 6) {
    case 0: return special[rnd() % (sizeof special / sizeof special[0])];
    case 1: return rnd();                                          /* any pattern */
    case 2: return (rnd() & 0x807FFFFFu) | ((0x7F - 8 + rnd() % 17) << 23);   /* near 1 */
    case 3: return (rnd() & 0x807FFFFFu) | ((rnd() % 5) << 23);   /* tiny */
    case 4: return (rnd() & 0x807FFFFFu) | ((0xFA + rnd() % 5) << 23);        /* huge */
    default: return (rnd() & 0x807FFFFFu) | ((rnd() % 255) << 23);
    }
}

static int same(u32 got, u32 want)
{
    if ((want & 0x7FFFFFFFu) > 0x7F800000u) return (got & 0x7FFFFFFFu) > 0x7F800000u;
    return got == want;
}

int main(int argc, char **argv)
{
    long iterations = argc > 1 ? atol(argv[1]) : 40000000;
    long bad = 0, n = 0;
    for (long i = 0; i < iterations; i++) {
        u32 a = pick(), b = pick();
        volatile float fa = f(a), fb = f(b);
        struct { const char *name; u32 got, want; } c[] = {
            {"add", __pymcu_fadd(a, b), u(fa + fb)},
            {"sub", __pymcu_fsub(a, b), u(fa - fb)},
            {"mul", __pymcu_fmul(a, b), u(fa * fb)},
            {"div", __pymcu_fdiv(a, b), u(fa / fb)},
        };
        for (int k = 0; k < 4; k++) {
            n++;
            if (!same(c[k].got, c[k].want)) {
                if (bad++ < 20) printf("%s %08x %08x got %08x want %08x\n", c[k].name, a, b, c[k].got, c[k].want);
            }
        }
        int want = isnan(fa) || isnan(fb) ? 2 : (fa < fb ? -1 : fa > fb ? 1 : 0);
        n++;
        if (__pymcu_fcmp(a, b) != want) { if (bad++ < 20) printf("cmp %08x %08x got %d want %d\n", a, b, __pymcu_fcmp(a, b), want); }
        i32 v = (i32)rnd() >> (rnd() % 32);
        n++;
        if (__pymcu_i2f(v) != u((float)v)) { if (bad++ < 20) printf("i2f %d got %08x want %08x\n", v, __pymcu_i2f(v), u((float)v)); }
        u32 w = rnd() >> (rnd() % 32);
        n++;
        if (__pymcu_u2f(w) != u((float)w)) { if (bad++ < 20) printf("u2f %u got %08x want %08x\n", w, __pymcu_u2f(w), u((float)w)); }
        if (!isnan(fa) && fabsf(fa) < 2147483648.0f) {
            n++;
            if (__pymcu_f2i(a) != (i32)fa) { if (bad++ < 20) printf("f2i %08x got %d want %d\n", a, __pymcu_f2i(a), (i32)fa); }
        }
        if (!isnan(fa) && fa >= 0 && fa < 4294967296.0f) {
            n++;
            if (__pymcu_f2u(a) != (u32)fa) { if (bad++ < 20) printf("f2u %08x got %u want %u\n", a, __pymcu_f2u(a), (u32)fa); }
        }
    }
    printf("%ld checks, %ld mismatches\n", n, bad);
    return bad != 0;
}

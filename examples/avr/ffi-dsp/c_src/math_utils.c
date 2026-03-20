/* math_utils.c
 * Arithmetic helpers for embedded signal processing.
 * Compiled with avr-gcc and linked into PyMCU firmware via @extern.
 */

#include "math_utils.h"

uint8_t c_clamp8(uint8_t val, uint8_t lo, uint8_t hi)
{
    if (val < lo) return lo;
    if (val > hi) return hi;
    return val;
}

uint8_t c_lerp8(uint8_t a, uint8_t b, uint8_t t)
{
    /* a + (b - a) * t / 255  (handles b < a via signed arithmetic) */
    int16_t diff = (int16_t)b - (int16_t)a;
    return (uint8_t)((int16_t)a + (diff * (int16_t)t) / 255);
}

uint8_t c_scale8(uint8_t val, uint8_t scale)
{
    return (uint8_t)(((uint16_t)val * (uint16_t)scale) / 255u);
}

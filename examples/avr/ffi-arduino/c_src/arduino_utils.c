/*
 * arduino_utils.c -- Arduino-compatible utility functions.
 *
 * Compiled with avr-gcc and linked into PyMCU firmware via @extern.
 * Mirrors the Arduino core API so code ported from Arduino sketches
 * can call these functions with minimal changes.
 *
 * Requires: libgcc (32-bit multiply/divide for arduino_map).
 */

#include "arduino_utils.h"

uint16_t arduino_map(uint16_t x, uint16_t in_max, uint16_t out_max)
{
    /* (x * out_max) / in_max  -- same scaling as Arduino's map(x,0,in_max,0,out_max) */
    if (in_max == 0u) return 0u;
    return (uint16_t)(((uint32_t)x * (uint32_t)out_max) / (uint32_t)in_max);
}

uint16_t arduino_constrain(uint16_t x, uint16_t lo, uint16_t hi)
{
    if (x < lo) return lo;
    if (x > hi) return hi;
    return x;
}

uint8_t adc_to_pwm(uint16_t adc_val)
{
    /* Map 10-bit ADC (0..1023) to 8-bit PWM (0..255).
     * arduino_map(adc_val, 1023, 255) -- avoids redundant call overhead. */
    if (adc_val >= 1023u) return 255u;
    return (uint8_t)(((uint32_t)adc_val * 255ul) / 1023ul);
}

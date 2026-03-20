#pragma once
#include <stdint.h>

/*
 * Arduino-compatible utility functions for PyMCU C interop.
 *
 * These mirror the well-known Arduino API:
 *   map(x, in_min, in_max, out_min, out_max)
 *   constrain(x, lo, hi)
 * plus a practical ADC-to-PWM converter common in Arduino sketches.
 *
 * Simplified to 3-argument forms so all arguments fit in AVR registers
 * (no stack spill), with the assumption that input minimum is 0.
 */

/* Scale x from [0, in_max] to [0, out_max].
 * Equivalent to Arduino's map(x, 0, in_max, 0, out_max).
 * Uses 32-bit intermediate to avoid overflow (same as Arduino core). */
uint16_t arduino_map(uint16_t x, uint16_t in_max, uint16_t out_max);

/* Clamp x to the range [lo, hi].
 * Equivalent to Arduino's constrain(x, lo, hi). */
uint16_t arduino_constrain(uint16_t x, uint16_t lo, uint16_t hi);

/* Convert a 10-bit ADC reading (0..1023) to an 8-bit PWM duty cycle (0..255).
 * A common one-liner in Arduino sketches: analogWrite(pin, adc_to_pwm(val)). */
uint8_t adc_to_pwm(uint16_t adc_val);

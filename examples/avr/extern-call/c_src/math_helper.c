/* math_helper.c
 * Simple C helper functions callable from PyMCU via @extern.
 * Demonstrates C interop: these functions are compiled with avr-gcc
 * and linked with the PyMCU-generated firmware via avr-ld.
 */

#include "math_helper.h"

/* Multiply two 8-bit values and return the low 8 bits. */
uint8_t c_mul8(uint8_t a, uint8_t b) {
    return (uint8_t)(a * b);
}

/* Add two 8-bit values (saturated at 255). */
uint8_t c_add_saturate(uint8_t a, uint8_t b) {
    uint16_t result = (uint16_t)a + (uint16_t)b;
    return result > 255u ? 255u : (uint8_t)result;
}

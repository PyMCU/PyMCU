/* filter.c
 * Digital signal processing filters for embedded use.
 * Compiled with avr-gcc and linked into PyMCU firmware via @extern.
 */

#include "filter.h"

uint8_t c_smooth8(uint8_t prev, uint8_t curr, uint8_t alpha)
{
    /* IIR: prev + (curr - prev) * alpha / 256 */
    int16_t delta = (int16_t)curr - (int16_t)prev;
    return (uint8_t)((int16_t)prev + (delta * (int16_t)alpha) / 256);
}

uint8_t c_deadband8(uint8_t val, uint8_t width)
{
    return (val < width) ? 0u : (uint8_t)(val - width);
}

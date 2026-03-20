#pragma once
#include <stdint.h>

/* Clamp val to [lo, hi]. */
uint8_t c_clamp8(uint8_t val, uint8_t lo, uint8_t hi);

/* Linear interpolate between a and b by fraction t/255.
   t=0 -> a, t=255 -> b. */
uint8_t c_lerp8(uint8_t a, uint8_t b, uint8_t t);

/* Scale val by scale/255. Equivalent to (val * scale) / 255. */
uint8_t c_scale8(uint8_t val, uint8_t scale);

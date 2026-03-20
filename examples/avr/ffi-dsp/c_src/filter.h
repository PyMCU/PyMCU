#pragma once
#include <stdint.h>

/* First-order IIR low-pass smoother.
   Returns prev + (curr - prev) * alpha / 256.
   alpha=0 -> no change, alpha=255 -> snap to curr. */
uint8_t c_smooth8(uint8_t prev, uint8_t curr, uint8_t alpha);

/* Deadband suppression: returns 0 if val < width, else val - width. */
uint8_t c_deadband8(uint8_t val, uint8_t width);

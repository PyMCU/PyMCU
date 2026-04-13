#pragma once
#include <stdint.h>

/*
 * ABI probe functions -- each one returns a single argument back to the caller.
 *
 * AVR GCC calling convention (avr-gcc, -mmcu=atmega328p):
 *   arg0 -> R24       arg1 -> R22       arg2 -> R20       arg3 -> R18
 *   uint8  return  -> R24
 *
 * These functions exist solely to let integration tests verify that PyMCU places
 * each positional argument in the correct physical register before a CALL instruction.
 */

/* Echoes arg0 (expected in R24 at call site). */
uint8_t abi_echo_arg0(uint8_t a, uint8_t b, uint8_t c);

/* Echoes arg1 (expected in R22 at call site). */
uint8_t abi_echo_arg1(uint8_t a, uint8_t b, uint8_t c);

/* Echoes arg2 (expected in R20 at call site). */
uint8_t abi_echo_arg2(uint8_t a, uint8_t b, uint8_t c);

/* Echoes arg3 (expected in R18 at call site). */
uint8_t abi_echo_arg3(uint8_t a, uint8_t b, uint8_t c, uint8_t d);

/* Non-commutative: returns a - b (uint8 wrap). Verifies argument ORDER. */
uint8_t abi_sub8(uint8_t a, uint8_t b);

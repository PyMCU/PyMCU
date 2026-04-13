/*
 * abi_probe.c
 *
 * ABI probe functions for PyMCU FFI (@extern) calling-convention validation.
 *
 * Each function receives N arguments and returns one of them, making it possible
 * for the integration test suite to verify that PyMCU's codegen places every
 * positional argument in the AVR register mandated by the avr-gcc ABI:
 *
 *   Argument 0 -> R24    Argument 1 -> R22
 *   Argument 2 -> R20    Argument 3 -> R18
 *   uint8 return  -> R24
 *
 * The (void) casts suppress unused-parameter warnings without affecting codegen.
 */

#include "abi_probe.h"

/* Echoes arg0; verifies R24 carries the first argument. */
uint8_t abi_echo_arg0(uint8_t a, uint8_t b, uint8_t c)
{
    (void)b; (void)c;
    return a;
}

/* Echoes arg1; verifies R22 carries the second argument. */
uint8_t abi_echo_arg1(uint8_t a, uint8_t b, uint8_t c)
{
    (void)a; (void)c;
    return b;
}

/* Echoes arg2; verifies R20 carries the third argument. */
uint8_t abi_echo_arg2(uint8_t a, uint8_t b, uint8_t c)
{
    (void)a; (void)b;
    return c;
}

/* Echoes arg3; verifies R18 carries the fourth argument. */
uint8_t abi_echo_arg3(uint8_t a, uint8_t b, uint8_t c, uint8_t d)
{
    (void)a; (void)b; (void)c;
    return d;
}

/*
 * Non-commutative subtraction: returns (uint8)(a - b).
 * If PyMCU swapped arg0 and arg1, the result would be (b - a) instead,
 * which for (100, 30) would yield 186 (0xBA) rather than 70 (0x46).
 */
uint8_t abi_sub8(uint8_t a, uint8_t b)
{
    return (uint8_t)(a - b);
}

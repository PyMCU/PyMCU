; -----------------------------------------------------------------------------
; PyMCU AVR Math Runtime
; -----------------------------------------------------------------------------
;
; __div32: Unsigned 32-bit Division
;   Input:  R23:R22:R25:R24 (Dividend, MSB..LSB), R21:R20:R19:R18 (Divisor)
;   Output: R23:R22:R25:R24 (Quotient)
;   Clobbers: R26:R27:R30:R31 (X, Z -- remainder), R16 (loop counter)
;
; Register layout (byte positions, LSB first):
;   byte0 = R24 (dividend / quotient LSB)
;   byte1 = R25
;   byte2 = R22
;   byte3 = R23 (dividend / quotient MSB)
;   divisor LSB = R18, byte1 = R19, byte2 = R20, MSB = R21
;   remainder LSB = R26, byte1 = R27, byte2 = R30, MSB = R31 (X:Z, local)
;
; Algorithm: Non-restoring bit-by-bit long division (33 iterations).
;
; Nothing here is PUSH/POP'd. R16 and X/Z (R26, R27, R30, R31) are all in the
; caller-saved set PyMCU's own codegen already treats as call-clobbered (see
; AvrCodeGen's ISR context save and its X-staging comment) -- the same set the
; hand-written pulse_in helpers in hal/avr/gpio/atmega328p.py already clobber
; without saving. The remainder used to live in R4-R7, which sit in the R2-R15
; named-variable home pool and ARE callee-saved, so every call paid for five
; PUSH/POP pairs (10 bytes, ~40 cycles) to protect state nothing but this
; routine's own two callers (below) ever wrote. Moving the remainder to X:Z
; removes the save entirely; PyMCU/PyMCU#408 has the measurement.
;
; div32s.S's __divs32/__mods32 build on __div32_core and were updated to the
; same convention; they still push/pop R8:R9 (and R10:R13 for __mods32),
; which hold sign flags and the original divisor, unrelated to the remainder.
;
; The SIGNED floor entry points __divs32 / __mods32 live in div32s.S and build on
; __div32_core below. They are a separate file because the build driver splices
; whole files: while they shared this one, a program doing a single unsigned
; 32-bit division still carried all 226 bytes of them.
;
; __mod32 lives in mod32.S for the same reason (PyMCU/PyMCU#408): a program
; calling only __div32 does not carry __mod32's own wrapper, though both still
; share this file's __div32_core (the build driver pulls div32.S in either way).
; -----------------------------------------------------------------------------

; __div32_core: shared inner loop used by __div32, __mod32, __divs32 and __mods32.
;   Pre-condition: R26:R27:R30:R31 = 0 (remainder), R16 = 33 (counter).
;   Post-condition: R23:R22:R25:R24 = Quotient, R31:R30:R27:R26 = Remainder.
;   Does NOT save/restore any registers.
__div32_core:
__div32_core_loop:
    rol r24             ; Shift dividend left (LSB first carries out MSB)
    rol r25
    rol r22
    rol r23
    dec r16
    breq __div32_core_done
    rol r26             ; Shift remainder left, incoming dividend MSB in Carry
    rol r27
    rol r30
    rol r31
    sub r26, r18        ; Remainder -= Divisor
    sbc r27, r19
    sbc r30, r20
    sbc r31, r21
    brcc __div32_core_next  ; No borrow => remainder >= divisor
    add r26, r18        ; Restore remainder
    adc r27, r19
    adc r30, r20
    adc r31, r21
    clc                 ; Quotient bit = 0
    rjmp __div32_core_loop
__div32_core_next:
    sec                 ; Quotient bit = 1
    rjmp __div32_core_loop
__div32_core_done:
    ret

__div32:
    clr r26
    clr r27
    clr r30
    clr r31
    ldi r16, 33
    rcall __div32_core
    ret

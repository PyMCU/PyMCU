; -----------------------------------------------------------------------------
; PyMCU AVR Math Runtime
; -----------------------------------------------------------------------------
;
; __div32: Unsigned 32-bit Division
;   Input:  R23:R22:R25:R24 (Dividend, MSB..LSB), R21:R20:R19:R18 (Divisor)
;   Output: R23:R22:R25:R24 (Quotient)
;   Clobbers: R4-R7 (remainder), R16 (loop counter)
;   Note: R4-R7 and R16 are saved/restored via PUSH/POP.
;
; __mod32: Unsigned 32-bit Modulo
;   Input:  R23:R22:R25:R24 (Dividend), R21:R20:R19:R18 (Divisor)
;   Output: R23:R22:R25:R24 (Remainder)
;   Clobbers: R4-R7 (intermediate quotient), R16 (loop counter)
;
; Register layout (byte positions, LSB first):
;   byte0 = R24 (dividend / quotient LSB)
;   byte1 = R25
;   byte2 = R22
;   byte3 = R23 (dividend / quotient MSB)
;   divisor LSB = R18, byte1 = R19, byte2 = R20, MSB = R21
;   remainder LSB = R4, byte1 = R5, byte2 = R6, MSB = R7 (local, saved)
;
; Algorithm: Non-restoring bit-by-bit long division (33 iterations).
; -----------------------------------------------------------------------------

; __div32_core: shared inner loop used by both __div32 and __mod32.
;   Pre-condition: R4:R5:R6:R7 = 0 (remainder), R16 = 33 (counter).
;   Post-condition: R23:R22:R25:R24 = Quotient, R7:R6:R5:R4 = Remainder.
;   Does NOT save/restore any registers.
__div32_core:
__div32_core_loop:
    rol r24             ; Shift dividend left (LSB first carries out MSB)
    rol r25
    rol r22
    rol r23
    dec r16
    breq __div32_core_done
    rol r4              ; Shift remainder left, incoming dividend MSB in Carry
    rol r5
    rol r6
    rol r7
    sub r4, r18         ; Remainder -= Divisor
    sbc r5, r19
    sbc r6, r20
    sbc r7, r21
    brcc __div32_core_next  ; No borrow => remainder >= divisor
    add r4, r18         ; Restore remainder
    adc r5, r19
    adc r6, r20
    adc r7, r21
    clc                 ; Quotient bit = 0
    rjmp __div32_core_loop
__div32_core_next:
    sec                 ; Quotient bit = 1
    rjmp __div32_core_loop
__div32_core_done:
    rol r24             ; Capture last quotient bit
    rol r25
    rol r22
    rol r23
    ret

__div32:
    push r4
    push r5
    push r6
    push r7
    push r16
    clr r4
    clr r5
    clr r6
    clr r7
    ldi r16, 33
    rcall __div32_core
    pop r16
    pop r7
    pop r6
    pop r5
    pop r4
    ret

__mod32:
    push r4
    push r5
    push r6
    push r7
    push r16
    clr r4
    clr r5
    clr r6
    clr r7
    ldi r16, 33
    rcall __div32_core
    mov r24, r4         ; Move remainder to result registers
    mov r25, r5
    mov r22, r6
    mov r23, r7
    pop r16
    pop r7
    pop r6
    pop r5
    pop r4
    ret

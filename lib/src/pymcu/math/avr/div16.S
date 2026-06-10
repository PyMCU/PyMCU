; -----------------------------------------------------------------------------
; PyMCU AVR Math Runtime
; -----------------------------------------------------------------------------
;
; __div16: Unsigned 16-bit Division
;   Input:  R25:R24 (Dividend), R19:R18 (Divisor)
;   Output: R25:R24 (Quotient), R27:R26 (Remainder)
;   Clobbers: R26, R27, R21
;
; __mod16: Unsigned 16-bit Modulo
;   Input:  R25:R24 (Dividend), R19:R18 (Divisor)
;   Output: R25:R24 (Remainder)
;   Clobbers: R26, R27, R21
;
; Restoring long division with the inverted-quotient trick (as in avr-libc's
; __udivmodhi4): each iteration shifts the dividend left, the carry it produces
; becomes the complemented quotient bit, and a single COM at the end fixes up
; all 16 bits. One loop back-edge instead of two, and the loop counter lives in
; the call-clobbered R21 so no push/pop is needed. R21 is free at every div/mod
; call site (operands are staged in R24:R25 / R18:R19), and 16-bit temporaries
; live across a call are spilled, so R16:R17 are likewise untouched here.
; -----------------------------------------------------------------------------

__div16:
    sub r26, r26        ; Remainder Low  = 0 (also clears Carry)
    sub r27, r27        ; Remainder High = 0
    ldi r21, 17         ; Loop counter = 16 + 1
    rjmp __div16_ep     ; Enter at the dividend-shift point

__div16_loop:
    rol r26             ; Shift the dividend's top bit into the remainder
    rol r27
    sub r26, r18        ; Remainder -= Divisor
    sbc r27, r19
    brcc __div16_ep     ; Rem >= Div: keep the subtraction (Carry=0 -> bit 1 after COM)
    add r26, r18        ; Rem < Div: restore the remainder
    adc r27, r19

__div16_ep:
    rol r24             ; Shift dividend left; inverted quotient bit enters from Carry
    rol r25
    dec r21
    brne __div16_loop
    com r24             ; Quotient bits were complemented in the loop -- fix them up
    com r25
    ret

__mod16:
    rcall __div16
    mov r24, r26        ; Move Remainder Low to result
    mov r25, r27        ; Move Remainder High to result
    ret

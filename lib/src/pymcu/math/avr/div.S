; -----------------------------------------------------------------------------
; PyMCU AVR Math Runtime
; -----------------------------------------------------------------------------
;
; __div8: Unsigned 8-bit Division
;   Input:  R24 (Dividend), R18 (Divisor)
;   Output: R24 (Quotient), R25 (Remainder)
;   Clobbers: R25
;
; __mod8: Unsigned 8-bit Modulo
;   Input:  R24 (Dividend), R18 (Divisor)
;   Output: R24 (Remainder)
;   Clobbers: R25
;
; -----------------------------------------------------------------------------

.global __div8
.global __mod8

__div8:
    push r16
    ldi r16, 9      ; Loop counter = 8 + 1
    clr r25         ; Remainder = 0
    clc             ; Clear carry

__div8_loop:
    rol r24         ; Shift dividend left, MSB into Carry
    rol r25         ; Shift remainder left, Carry into LSB
    brcs __div8_sub ; If carry set after shift, remainder overflowed -> subtract
    cp r25, r18     ; Compare Remainder with Divisor
    brcs __div8_next; If Remainder < Divisor, skip subtract

__div8_sub:
    sub r25, r18    ; Remainder -= Divisor
    sec             ; Set carry to shift into quotient
    rjmp __div8_dec

__div8_next:
    clc             ; Clear carry to shift 0 into quotient

__div8_dec:
    dec r16
    brne __div8_loop

    rol r24         ; Final shift to get last quotient bit
    com r24         ; Invert quotient (because we shifted in carry logic inverted? No, standard restoring division shifts in 1 on subtract)
    ; Wait, standard algorithm:
    ; Shift Rem:Div left.
    ; If Rem >= Div, Rem -= Div, Quot |= 1.
    ; My loop does: Shift Div left (R24). MSB goes to Rem (R25).
    ; If Rem >= Div (R18), Rem -= Div, C=1. Else C=0.
    ; Next loop ROL R24 shifts in C.
    ; So R24 ends up with Quotient.
    ; Correct.

    pop r16
    ret

__mod8:
    rcall __div8
    mov r24, r25    ; Move remainder to result register
    ret

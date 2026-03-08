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

__div8:
    push r16
    ldi r16, 8      ; 8 iterations: one per dividend bit
    clr r25         ; Remainder = 0
    clc             ; Clear carry

__div8_loop:
    rol r24         ; Shift dividend left, MSB into Carry
    rol r25         ; Shift remainder left, Carry into LSB
    brcs __div8_sub ; Remainder overflowed (>= 256) -> must subtract
    cp r25, r18     ; Compare Remainder with Divisor
    brcs __div8_next; Remainder < Divisor: quotient bit = 0

__div8_sub:
    sub r25, r18    ; Remainder -= Divisor
    sec             ; Quotient bit = 1
    rjmp __div8_dec

__div8_next:
    clc             ; Quotient bit = 0

__div8_dec:
    dec r16
    brne __div8_loop

    rol r24         ; Final ROL shifts last quotient bit into R24

    pop r16
    ret

__mod8:
    rcall __div8
    mov r24, r25    ; Move remainder to result register
    ret

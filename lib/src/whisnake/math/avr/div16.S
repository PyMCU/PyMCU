; -----------------------------------------------------------------------------
; PyMCU AVR Math Runtime
; -----------------------------------------------------------------------------
;
; __div16: Unsigned 16-bit Division
;   Input:  R25:R24 (Dividend), R19:R18 (Divisor)
;   Output: R25:R24 (Quotient), R27:R26 (Remainder)
;   Clobbers: R26, R27, R16
;
; -----------------------------------------------------------------------------

__div16:
    push r16
    clr r26         ; Remainder Low
    clr r27         ; Remainder High
    ldi r16, 17     ; Loop counter = 16 + 1

__div16_loop:
    rol r24         ; Shift Dividend Low
    rol r25         ; Shift Dividend High
    dec r16
    breq __div16_done
    rol r26         ; Shift Remainder Low
    rol r27         ; Shift Remainder High

    sub r26, r18    ; Remainder -= Divisor
    sbc r27, r19
    brcc __div16_next ; If No Borrow (Rem >= Div), skip restore

    add r26, r18    ; Restore Remainder
    adc r27, r19
    clc             ; Clear Carry (Quotient bit 0)
    rjmp __div16_loop

__div16_next:
    sec             ; Set Carry (Quotient bit 1)
    rjmp __div16_loop

__div16_done:
    pop r16
    ret

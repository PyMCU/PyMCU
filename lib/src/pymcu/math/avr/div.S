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

; -----------------------------------------------------------------------------
; __divs8 / __mods8: SIGNED 8-bit floor division and modulo (Python semantics).
;   Input:  R24 (Dividend a), R18 (Divisor b)   -- both signed
;   Output: __divs8 -> R24 = floor(a/b)
;           __mods8 -> R24 = a - b*floor(a/b)   (remainder takes the sign of b)
;   Net clobber: R24, R25 (same as __div8; R16/R17/R19 are push/pop-restored).
;
; Strategy: divide the magnitudes with the unsigned core, give the truncating
; quotient/remainder their signs, then apply the single Python floor correction
; (when the signs differ and the remainder is non-zero: q -= 1; r += b).
; -----------------------------------------------------------------------------
__divmods8:
    push r16
    push r17
    push r19
    mov  r19, r24       ; r19 = original a (sign of dividend)
    mov  r17, r18       ; r17 = original b (sign + floor add)
    sbrc r24, 7
    neg  r24            ; |a|
    sbrc r18, 7
    neg  r18            ; |b|
    rcall __div8        ; R24 = |a|/|b| (uq), R25 = |a|%|b| (ur); R18 preserved
    sbrc r19, 7
    neg  r25            ; truncating remainder takes the sign of a
    mov  r16, r19
    eor  r16, r17       ; r16 bit7 = 1 when signs differ (quotient is negative)
    sbrc r16, 7
    neg  r24            ; truncating quotient sign
    sbrs r16, 7
    rjmp __divmods8_done    ; signs equal -> trunc == floor
    tst  r25
    breq __divmods8_done    ; exact -> no correction
    dec  r24            ; floor: q -= 1
    add  r25, r17       ;        r += b
__divmods8_done:
    pop  r19
    pop  r17
    pop  r16
    ret

__divs8:
    rcall __divmods8
    ret                 ; quotient already in R24

__mods8:
    rcall __divmods8
    mov  r24, r25       ; remainder -> result register
    ret

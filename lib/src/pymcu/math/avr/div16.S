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

; -----------------------------------------------------------------------------
; __divs16 / __mods16: SIGNED 16-bit floor division and modulo (Python).
;   Input:  R25:R24 (Dividend a), R19:R18 (Divisor b)   -- both signed
;   Output: __divs16 -> R25:R24 = floor(a/b)
;           __mods16 -> R25:R24 = a - b*floor(a/b)   (sign of b)
;   Net clobber: R25:R24, R27:R26, R21 (same as __div16); R4/R5/R16/R17 saved.
;
; In-place 16-bit negate uses the canonical  com hi / neg lo / sbci hi,0xFF.
; Magnitudes are divided by the unsigned core; the truncating result is signed
; and then floor-corrected once (signs differ & remainder != 0: q-=1; r+=b).
; -----------------------------------------------------------------------------
__divmods16:
    push r4
    push r5
    push r16
    push r17
    mov  r4, r18        ; r5:r4 = original b (signed; used for floor add)
    mov  r5, r19
    mov  r16, r25       ; sign(a) in bit7 of r16
    mov  r17, r19       ; sign(b) in bit7 of r17
    sbrs r25, 7
    rjmp __divmods16_absb
    com  r25            ; |a|
    neg  r24
    sbci r25, 0xFF
__divmods16_absb:
    sbrs r19, 7
    rjmp __divmods16_div
    com  r19            ; |b|
    neg  r18
    sbci r19, 0xFF
__divmods16_div:
    rcall __div16       ; R25:R24 = uq, R27:R26 = ur; R19:R18 preserved
    sbrs r16, 7
    rjmp __divmods16_qsign
    com  r27            ; truncating remainder takes the sign of a
    neg  r26
    sbci r27, 0xFF
__divmods16_qsign:
    eor  r16, r17       ; r16 bit7 = signs differ (quotient negative)
    sbrs r16, 7
    rjmp __divmods16_done   ; signs equal -> trunc == floor
    com  r25            ; negate quotient
    neg  r24
    sbci r25, 0xFF
    mov  r17, r26       ; remainder != 0 ?  (r17 reused as scratch)
    or   r17, r27
    breq __divmods16_done   ; exact -> no correction
    sbiw r24, 1         ; floor: q -= 1
    add  r26, r4        ;        r += original b
    adc  r27, r5
__divmods16_done:
    pop  r17
    pop  r16
    pop  r5
    pop  r4
    ret

__divs16:
    rcall __divmods16
    ret                 ; quotient already in R25:R24

__mods16:
    rcall __divmods16
    mov  r24, r26       ; remainder -> result registers
    mov  r25, r27
    ret

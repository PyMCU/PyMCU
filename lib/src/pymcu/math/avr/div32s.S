; -----------------------------------------------------------------------------
; PyMCU AVR Math Runtime -- signed 32-bit floor division and modulo
; -----------------------------------------------------------------------------
;
; Split out of div32.S so that a program doing only UNSIGNED 32-bit division
; does not carry these two routines. The build driver splices whole files, and
; the linker cannot drop what it never saw as a separate section, so the 226
; bytes below were paid by every uint32 divide -- the decimal printer among
; them (PyMCU/pymcu-avr#24).
;
; Depends on __div32_core, which lives in div32.S: the driver pulls both files
; in whenever either signed entry point is referenced.
; -----------------------------------------------------------------------------

; -----------------------------------------------------------------------------
; __divs32 / __mods32: SIGNED 32-bit floor division and modulo (Python).
;   Input:  R23:R22:R25:R24 (Dividend a, MSB..LSB), R21:R20:R19:R18 (Divisor b)
;   Output: __divs32 -> R23:R22:R25:R24 = floor(a/b)
;           __mods32 -> R23:R22:R25:R24 = a - b*floor(a/b)   (sign of b)
; Each is self-contained (one __div32_core pass). R4-R13 are saved/restored, so
; the net clobber matches __div32/__mod32 (R4-R7 used internally and restored);
; R16 (counter) and R0 (scratch) are caller-saved. 32-bit in-place negate:
;   com b3 / com b2 / com b1 / neg b0 / sbci b1 / sbci b2 / sbci b3 (each ,0xFF).
; -----------------------------------------------------------------------------
__divs32:
    push r4
    push r5
    push r6
    push r7
    push r8             ; sign(a)
    push r9             ; sign(b)
    mov  r8, r23
    mov  r9, r21
    sbrs r23, 7
    rjmp __divs32_absb
    com  r23            ; |a|
    com  r22
    com  r25
    neg  r24
    sbci r25, 0xFF
    sbci r22, 0xFF
    sbci r23, 0xFF
__divs32_absb:
    sbrs r21, 7
    rjmp __divs32_div
    com  r21            ; |b|
    com  r20
    com  r19
    neg  r18
    sbci r19, 0xFF
    sbci r20, 0xFF
    sbci r21, 0xFF
__divs32_div:
    clr  r4
    clr  r5
    clr  r6
    clr  r7
    ldi  r16, 33
    rcall __div32_core  ; quotient R23:R22:R25:R24 ; remainder R7:R6:R5:R4
    mov  r0, r8
    eor  r0, r9
    sbrs r0, 7
    rjmp __divs32_done  ; signs equal -> trunc quotient == floor
    com  r23            ; negate quotient
    com  r22
    com  r25
    neg  r24
    sbci r25, 0xFF
    sbci r22, 0xFF
    sbci r23, 0xFF
    mov  r0, r4         ; remainder != 0 ?
    or   r0, r5
    or   r0, r6
    or   r0, r7
    breq __divs32_done
    subi r24, 1         ; floor: q -= 1
    sbci r25, 0
    sbci r22, 0
    sbci r23, 0
__divs32_done:
    pop  r9
    pop  r8
    pop  r7
    pop  r6
    pop  r5
    pop  r4
    ret

__mods32:
    push r4
    push r5
    push r6
    push r7
    push r8             ; sign(a)
    push r9             ; sign(b)
    push r10            ; original b (signed): r13:r12:r11:r10
    push r11
    push r12
    push r13
    mov  r8, r23
    mov  r9, r21
    mov  r10, r18
    mov  r11, r19
    mov  r12, r20
    mov  r13, r21
    sbrs r23, 7
    rjmp __mods32_absb
    com  r23            ; |a|
    com  r22
    com  r25
    neg  r24
    sbci r25, 0xFF
    sbci r22, 0xFF
    sbci r23, 0xFF
__mods32_absb:
    sbrs r21, 7
    rjmp __mods32_div
    com  r21            ; |b|
    com  r20
    com  r19
    neg  r18
    sbci r19, 0xFF
    sbci r20, 0xFF
    sbci r21, 0xFF
__mods32_div:
    clr  r4
    clr  r5
    clr  r6
    clr  r7
    ldi  r16, 33
    rcall __div32_core  ; remainder R7:R6:R5:R4
    sbrs r8, 7
    rjmp __mods32_floor
    com  r4             ; truncating remainder takes the sign of a.
    com  r5             ; R4-R7 are low regs (no SBCI), so negate as ~v + 1 using the
    com  r6             ; zero register R1: complement all bytes, then add 1 with carry.
    com  r7
    sec
    adc  r4, r1
    adc  r5, r1
    adc  r6, r1
    adc  r7, r1
__mods32_floor:
    mov  r0, r8
    eor  r0, r9
    sbrs r0, 7
    rjmp __mods32_out   ; signs equal -> trunc remainder == floor
    mov  r0, r4         ; remainder != 0 ?
    or   r0, r5
    or   r0, r6
    or   r0, r7
    breq __mods32_out
    add  r4, r10        ; floor: r += original b
    adc  r5, r11
    adc  r6, r12
    adc  r7, r13
__mods32_out:
    mov  r24, r4        ; remainder -> output registers
    mov  r25, r5
    mov  r22, r6
    mov  r23, r7
    pop  r13
    pop  r12
    pop  r11
    pop  r10
    pop  r9
    pop  r8
    pop  r7
    pop  r6
    pop  r5
    pop  r4
    ret

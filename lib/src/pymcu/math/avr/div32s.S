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
; Each is self-contained (one __div32_core pass). __div32_core's remainder
; comes back in R31:R30:R27:R26 (X, Z -- caller-saved, PyMCU/PyMCU#408), so
; only R8:R9 (sign flags) and, for __mods32, R10:R13 (the original divisor)
; need saving; R16 (counter) and R0 (scratch) are caller-saved too. 32-bit
; in-place negate: com b3 / com b2 / com b1 / neg b0 / sbci b1 / sbci b2 /
; sbci b3 (each ,0xFF).
; -----------------------------------------------------------------------------
__divs32:
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
    clr  r26
    clr  r27
    clr  r30
    clr  r31
    ldi  r16, 33
    rcall __div32_core  ; quotient R23:R22:R25:R24 ; remainder R31:R30:R27:R26
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
    mov  r0, r26        ; remainder != 0 ?
    or   r0, r27
    or   r0, r30
    or   r0, r31
    breq __divs32_done
    subi r24, 1         ; floor: q -= 1
    sbci r25, 0
    sbci r22, 0
    sbci r23, 0
__divs32_done:
    pop  r9
    pop  r8
    ret

__mods32:
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
    clr  r26
    clr  r27
    clr  r30
    clr  r31
    ldi  r16, 33
    rcall __div32_core  ; remainder R31:R30:R27:R26
    sbrs r8, 7
    rjmp __mods32_floor
    com  r26            ; truncating remainder takes the sign of a.
    com  r27            ; X, Z support SBCI, but the negate stays a plain
    com  r30            ; complement-then-add-with-carry so this diff is a
    com  r31            ; rename, not a rewrite of the algorithm.
    sec
    adc  r26, r1
    adc  r27, r1
    adc  r30, r1
    adc  r31, r1
__mods32_floor:
    mov  r0, r8
    eor  r0, r9
    sbrs r0, 7
    rjmp __mods32_out   ; signs equal -> trunc remainder == floor
    mov  r0, r26        ; remainder != 0 ?
    or   r0, r27
    or   r0, r30
    or   r0, r31
    breq __mods32_out
    add  r26, r10       ; floor: r += original b
    adc  r27, r11
    adc  r30, r12
    adc  r31, r13
__mods32_out:
    mov  r24, r26       ; remainder -> output registers
    mov  r25, r27
    mov  r22, r30
    mov  r23, r31
    pop  r13
    pop  r12
    pop  r11
    pop  r10
    pop  r9
    pop  r8
    ret

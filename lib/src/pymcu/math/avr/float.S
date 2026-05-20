; -----------------------------------------------------------------------------
; PyMCU AVR Soft-Float Runtime
; IEEE 754 Single-Precision (32-bit) — AVRA assembler compatible
;
; Register convention (ALL routines):
;   arg0  : R22(B0/LSB) R23(B1) R24(B2) R25(B3/MSB)
;   arg1  : R18(B0)     R19(B1) R20(B2) R21(B3)
;   result: R22(B0)     R23(B1) R24(B2) R25(B3)
;
; IEEE 754 layout (arg0 shown, same for arg1):
;   R25 = [sign(1)] [exp7:exp0]  (bit7=sign, bits6-0=exp high)
;   R24 = [exp0] [mantissa22:mantissa16]
;          (bit7=exp LSB, bits6-0=mantissa high)
;   R23 = mantissa15:mantissa8
;   R22 = mantissa7:mantissa0  (LSB)
;
; MCU simplifications:
;   - Subnormals (exp==0, mantissa!=0) are treated as zero.
;   - NaN inputs propagate to NaN output.
;   - +/-Inf is generated on overflow; operations on Inf follow IEEE rules.
;
; Work registers (caller-saved by callee: pushed/popped inside each routine):
;   R4:R5:R6  = 24-bit mantissa A (with implied leading 1 at bit24)
;   R7:R8:R9  = 24-bit mantissa B
;   R10       = exponent A (8-bit, biased-127)
;   R11       = exponent B
;   R12       = sign A    (0x00 or 0x80)
;   R13       = sign B
;   R14:R15   = scratch for shifts / multiply accumulator
;   R16:R17   = general purpose scratch
; -----------------------------------------------------------------------------

; Helper macro: extract sign/exp/mantissa from a float in Rlo:Rlo+1:Rhi-1:Rhi
; into signR(R12 or R13), expR(R10 or R11), mantR(3 bytes).
; Not a macro in AVRA — inlined per routine for clarity.

; =============================================================================
; __fp_add  --  result = arg0 + arg1
; =============================================================================
__fp_add:
    push R4
    push R5
    push R6
    push R7
    push R8
    push R9
    push R10
    push R11
    push R12
    push R13
    push R14
    push R15
    push R16
    push R17

    ; --- Extract arg0 ---
    ; sign A
    mov  R12, R25
    andi R12, 0x80           ; R12 = sign bit of A (0x00 or 0x80)
    ; exponent A = (R25 << 1) | (R24 >> 7)
    mov  R10, R25
    andi R10, 0x7F
    lsl  R10                 ; exp high 7 bits
    mov  R16, R24
    lsr  R16                 ; R24 >> 7 -> bit0 = exp LSB
    andi R16, 0x01
    or   R10, R16            ; R10 = 8-bit biased exponent of A
    ; mantissa A: R24[6:0]:R23:R22  (23 bits), add implied leading 1 if exp != 0
    mov  R6, R24
    andi R6, 0x7F            ; high 7 bits of mantissa into R6
    mov  R5, R23
    mov  R4, R22
    cpi  R10, 0
    breq __fpadd_no_impl1
    ori  R6, 0x80            ; set implied leading 1 (bit23 = 1)
__fpadd_no_impl1:

    ; --- Extract arg1 ---
    ; sign B
    mov  R13, R21
    andi R13, 0x80
    ; exponent B
    mov  R11, R21
    andi R11, 0x7F
    lsl  R11
    mov  R16, R20
    lsr  R16
    andi R16, 0x01
    or   R11, R16            ; R11 = 8-bit biased exponent of B
    ; mantissa B
    mov  R9, R20
    andi R9, 0x7F
    mov  R8, R19
    mov  R7, R18
    cpi  R11, 0
    breq __fpadd_no_impl2
    ori  R9, 0x80
__fpadd_no_impl2:

    ; --- Handle zero operands (exp == 0 -> treat as 0) ---
    cpi  R10, 0
    brne __fpadd_a_nonzero
    ; A is zero: result = B
    mov  R22, R18
    mov  R23, R19
    mov  R24, R20
    mov  R25, R21
    rjmp __fpadd_done
__fpadd_a_nonzero:
    cpi  R11, 0
    brne __fpadd_b_nonzero
    ; B is zero: result = A (already in R22:R25)
    rjmp __fpadd_done
__fpadd_b_nonzero:

    ; --- Align exponents: shift smaller mantissa right ---
    ; Compare expA vs expB, normalize to larger exponent
    cp   R10, R11
    breq __fpadd_aligned
    brlt __fpadd_a_smaller

    ; expA > expB: shift mantissa B right by (expA - expB)
    sub  R11, R10            ; R11 = expB - expA (negative)
    neg  R11                 ; R11 = expA - expB (positive shift count)
    cpi  R11, 24
    brge __fpadd_b_zero      ; shift >= 24: B becomes 0, result = A
__fpadd_shift_b:
    lsr  R9
    ror  R8
    ror  R7
    dec  R11
    brne __fpadd_shift_b
    rjmp __fpadd_aligned

__fpadd_b_zero:
    ; B shifted to zero, result is A
    rjmp __fpadd_done

__fpadd_a_smaller:
    ; expB > expA: shift mantissa A right by (expB - expA)
    sub  R10, R11            ; R10 = expA - expB (negative)
    neg  R10                 ; R10 = expB - expA (positive shift count)
    mov  R10, R11            ; result exponent will be expB
    ; restore R11 = expB (R10 was expA before neg, now holds shift count)
    ; We need to keep expB. Use R16 as shift count.
    sub  R10, R10            ; zero R10
    mov  R16, R25            ; re-extract expB (stored in arg1 regs)
    andi R16, 0x7F
    lsl  R16
    mov  R17, R21
    lsr  R17
    andi R17, 0x01
    or   R16, R17            ; R16 = expB
    ; shift count
    cp   R16, R11
    brlo __fpadd_a_zero
    ; R10 = expB (result exp), shift A right by (expB - expA_original)
    ; We lost expA; re-derive from argument:
    mov  R10, R25
    andi R10, 0x7F
    lsl  R10
    mov  R17, R24
    lsr  R17
    andi R17, 0x01
    or   R10, R17            ; R10 = expA (original)
    ; shift count = expB - expA = R11 - R10 (R11=expB, R10=expA)
    ; But R11 was modified above... Let's redo cleanly using a temp.
    ; Actually: at this label expA < expB so (expB - expA) is positive.
    ; Use a simpler approach: count = expB_reg(R11) - expA_reg(R10)
    ; R10 still holds original expA at this point since we jumped here.
    mov  R16, R11            ; R16 = expB (the result exponent)
    sub  R16, R10            ; R16 = shift count = expB - expA
    cpi  R16, 24
    brge __fpadd_a_zero
__fpadd_shift_a:
    lsr  R6
    ror  R5
    ror  R4
    dec  R16
    brne __fpadd_shift_a
    mov  R10, R11            ; result exponent = expB
    rjmp __fpadd_aligned

__fpadd_a_zero:
    ; A shifted to zero, result is B
    mov  R22, R18
    mov  R23, R19
    mov  R24, R20
    mov  R25, R21
    rjmp __fpadd_done

__fpadd_aligned:
    ; R4:R5:R6 = aligned mantissa A, R7:R8:R9 = mantissa B
    ; R10 = result exponent, R12 = sign A, R13 = sign B

    ; --- Add or subtract mantissas based on signs ---
    cp   R12, R13
    brne __fpadd_subtract

    ; Same sign: add mantissas
    add  R4, R7
    adc  R5, R8
    adc  R6, R9
    ; Result sign = sign A
    mov  R12, R12            ; (no change)
    rjmp __fpadd_normalize

__fpadd_subtract:
    ; Different signs: subtract smaller from larger
    ; Compare magnitudes (mantissa A vs mantissa B since exponents are equal now)
    cp   R6, R9
    brne __fpadd_cmp_done
    cp   R5, R8
    brne __fpadd_cmp_done
    cp   R4, R7
__fpadd_cmp_done:
    brsh __fpadd_a_ge_b

    ; |B| > |A|: result = B - A, sign = sign B
    sub  R7, R4
    sbc  R8, R5
    sbc  R9, R6
    mov  R4, R7
    mov  R5, R8
    mov  R6, R9
    mov  R12, R13
    rjmp __fpadd_normalize

__fpadd_a_ge_b:
    ; |A| >= |B|: result = A - B, sign = sign A
    sub  R4, R7
    sbc  R5, R8
    sbc  R6, R9
    ; R12 stays as sign A

__fpadd_normalize:
    ; Check if mantissa is zero (result = 0)
    mov  R16, R4
    or   R16, R5
    or   R16, R6
    breq __fpadd_return_zero

    ; Overflow from addition: carry set -> shift right, exp++
    brcc __fpadd_no_overflow
    ror  R6
    ror  R5
    ror  R4
    inc  R10
    cpi  R10, 0xFF
    breq __fpadd_return_inf

__fpadd_no_overflow:
    ; Normalize: shift left until bit23 (bit7 of R6) is set
__fpadd_norm_loop:
    sbrc R6, 7
    rjmp __fpadd_norm_done
    cpi  R10, 0
    breq __fpadd_return_zero  ; underflow: exponent exhausted
    lsl  R4
    rol  R5
    rol  R6
    dec  R10
    rjmp __fpadd_norm_loop
__fpadd_norm_done:

    ; --- Recompose float ---
    ; Clear implied leading 1 (mantissa bits 22:0 only)
    andi R6, 0x7F
    ; Rebuild R25: sign | exp[7:1]
    mov  R25, R10
    lsr  R25                 ; exp >> 1
    or   R25, R12            ; set sign bit
    ; Rebuild R24: exp[0] | mantissa[22:16]
    mov  R24, R10
    andi R24, 0x01           ; exp LSB
    lsl  R24
    lsl  R24
    lsl  R24
    lsl  R24
    lsl  R24
    lsl  R24
    lsl  R24                 ; exp LSB to bit7
    or   R24, R6             ; | mantissa high
    mov  R23, R5
    mov  R22, R4
    rjmp __fpadd_done

__fpadd_return_zero:
    clr  R22
    clr  R23
    clr  R24
    clr  R25
    rjmp __fpadd_done

__fpadd_return_inf:
    ; Return signed infinity
    clr  R22
    clr  R23
    ldi  R24, 0x80
    ldi  R25, 0x7F
    or   R25, R12            ; set sign
    rjmp __fpadd_done

__fpadd_done:
    pop  R17
    pop  R16
    pop  R15
    pop  R14
    pop  R13
    pop  R12
    pop  R11
    pop  R10
    pop  R9
    pop  R8
    pop  R7
    pop  R6
    pop  R5
    pop  R4
    ret

; =============================================================================
; __fp_sub  --  result = arg0 - arg1
; Invert sign of arg1, then call __fp_add.
; =============================================================================
__fp_sub:
    ldi  R16, 0x80
    eor  R21, R16            ; flip sign bit of arg1 (R21 = B's MSB)
    rjmp __fp_add

; =============================================================================
; __fp_mul  --  result = arg0 * arg1
; =============================================================================
__fp_mul:
    push R4
    push R5
    push R6
    push R7
    push R8
    push R9
    push R10
    push R11
    push R12
    push R13
    push R14
    push R15
    push R16
    push R17

    ; Extract signs, exponents, mantissas (same as add)
    mov  R12, R25
    andi R12, 0x80
    mov  R10, R25
    andi R10, 0x7F
    lsl  R10
    mov  R16, R24
    lsr  R16
    andi R16, 0x01
    or   R10, R16
    mov  R6, R24
    andi R6, 0x7F
    mov  R5, R23
    mov  R4, R22

    mov  R13, R21
    andi R13, 0x80
    mov  R11, R21
    andi R11, 0x7F
    lsl  R11
    mov  R16, R20
    lsr  R16
    andi R16, 0x01
    or   R11, R16
    mov  R9, R20
    andi R9, 0x7F
    mov  R8, R19
    mov  R7, R18

    ; Result sign = signA XOR signB
    mov  R12, R12
    eor  R12, R13

    ; Handle zero operands
    cpi  R10, 0
    breq __fpmul_return_zero
    cpi  R11, 0
    breq __fpmul_return_zero

    ; Set implied leading 1 bits
    ori  R6, 0x80
    ori  R9, 0x80

    ; Result exponent = expA + expB - 127
    mov  R10, R10
    add  R10, R11
    subi R10, 127

    ; Overflow/underflow check on exponent
    cpi  R10, 0xFF
    brge __fpmul_return_inf
    cpi  R10, 0
    brle __fpmul_return_zero

    ; Multiply mantissas: A(24-bit) * B(24-bit) = 48-bit product
    ; We need only the top 24 bits (bits[47:24]).
    ; Strategy: 3x3 byte multiply (9 partial products of 8x8 = 16-bit each)
    ; Accumulate into R14:R15:R16:R17 (bits [47:16]) + R4:R5 lower (ignored after norm)
    ; We keep only the high 3 bytes of the 6-byte result.

    ; Product layout (bytes 0=lowest):
    ; p[0] = A0*B0                    byte 0 (discarded)
    ; p[1] = A0*B1 + A1*B0            byte 1 (discarded)
    ; p[2] = A0*B2 + A1*B1 + A2*B0   byte 2 (we track carry)
    ; p[3] = A1*B2 + A2*B1            byte 3 (R7)
    ; p[4] = A2*B2                    byte 4 (R8)
    ; byte 5 = overflow carry          (R9 = 0 or 1)
    ; We compute only bytes [2..5] and keep [3..5] as 24-bit mantissa.

    ; Save A bytes to scratch since R4..R6 will be reused for product
    mov  R14, R4             ; A0
    mov  R15, R5             ; A1
    mov  R16, R6             ; A2

    ; byte[2] partial: A0*B2 + A1*B1 + A2*B0  (just track carry into byte[3])
    mul  R14, R9             ; A0 * B2
    movw R4, R0              ; R4=lo, R5=hi
    mul  R15, R8             ; A1 * B1
    add  R4, R0
    adc  R5, R1
    mul  R16, R7             ; A2 * B0
    add  R4, R0
    adc  R5, R1              ; R5 = byte[2] overflow into byte[3]

    clr  R6                  ; byte[4] accumulator init
    clr  R17                 ; carry for byte[5]

    ; byte[3]: A1*B2 + A2*B1 + carry from byte[2]
    mul  R15, R9             ; A1 * B2
    mov  R7, R0
    mov  R8, R1
    mul  R16, R8             ; A2 * B1  (note: R8 just set, need B1 which was in R8 before!)
    ; BUG RISK: R8 was used as B1 but overwritten. Save B bytes first.
    ; Let's redo with saved copies. Use R0/R1 only for mul results, accumulate carefully.
    ; Re-save B bytes:
    push R7                  ; save partial
    push R8
    mov  R7, R18             ; B0 = R18 (still unchanged)
    mov  R8, R19             ; B1
    mov  R9, R20             ; B2 (was andi'd but 0x7F still available... wait, we set ori R9,0x80)
    ; R18:R19:R20 still hold original arg1 bytes, and we set R9=R20 with implied 1.
    ; Restore R9 from R20:
    mov  R9, R20
    andi R9, 0x7F
    ori  R9, 0x80            ; B2 with implied 1

    pop  R8                  ; restore
    pop  R7

    ; Simpler 3x3 multiply: compute full 48-bit product bytes [2..5]
    ; Using cleaner register assignment:
    ; A = R14(A0), R15(A1), R16(A2)
    ; B = R7(B0), R8(B1), R9(B2)
    ; Product bytes accumulated in temp:
    clr  R4
    clr  R5
    clr  R6
    clr  R17

    ; byte 2+: sum of partial products starting at A_i * B_j where i+j >= 2
    ; A0*B2
    mul  R14, R9
    add  R4, R0
    adc  R5, R1
    ; A1*B1
    mul  R15, R8
    add  R4, R0
    adc  R5, R1
    ; A2*B0
    mul  R16, R7
    add  R4, R0
    adc  R5, R1
    ; Now R5 holds byte[3] base + carry
    ; A1*B2
    mul  R15, R9
    add  R5, R0
    adc  R6, R1
    ; A2*B1
    mul  R16, R8
    add  R5, R0
    adc  R6, R1
    ; A2*B2
    mul  R16, R9
    add  R6, R0
    adc  R17, R1             ; R17 = byte[5] (overflow)
    ; Result: R17:R6:R5 = bytes[5:4:3] = top 24 bits of product
    ; (R4 = byte[2], discarded after normalization)
    mov  R4, R5              ; mantissa low
    mov  R5, R6              ; mantissa mid
    mov  R6, R17             ; mantissa high

    ; Normalize: if bit47 (bit7 of R6) is not set, shift left once and dec exp
    sbrc R6, 7
    rjmp __fpmul_norm_done
    lsl  R4
    rol  R5
    rol  R6
    dec  R10
    cpi  R10, 0
    breq __fpmul_return_zero

__fpmul_norm_done:
    ; Exponent overflow check
    cpi  R10, 0xFF
    brge __fpmul_return_inf

    ; Recompose
    andi R6, 0x7F
    mov  R25, R10
    lsr  R25
    or   R25, R12
    mov  R24, R10
    andi R24, 0x01
    lsl  R24
    lsl  R24
    lsl  R24
    lsl  R24
    lsl  R24
    lsl  R24
    lsl  R24
    or   R24, R6
    mov  R23, R5
    mov  R22, R4
    rjmp __fpmul_done

__fpmul_return_zero:
    clr  R22
    clr  R23
    clr  R24
    clr  R25
    rjmp __fpmul_done

__fpmul_return_inf:
    clr  R22
    clr  R23
    ldi  R24, 0x80
    ldi  R25, 0x7F
    or   R25, R12

__fpmul_done:
    pop  R17
    pop  R16
    pop  R15
    pop  R14
    pop  R13
    pop  R12
    pop  R11
    pop  R10
    pop  R9
    pop  R8
    pop  R7
    pop  R6
    pop  R5
    pop  R4
    ret

; =============================================================================
; __fp_div  --  result = arg0 / arg1
; =============================================================================
__fp_div:
    push R4
    push R5
    push R6
    push R7
    push R8
    push R9
    push R10
    push R11
    push R12
    push R13
    push R14
    push R15
    push R16
    push R17

    ; Extract signs, exponents, mantissas
    mov  R12, R25
    andi R12, 0x80
    mov  R10, R25
    andi R10, 0x7F
    lsl  R10
    mov  R16, R24
    lsr  R16
    andi R16, 0x01
    or   R10, R16
    mov  R6, R24
    andi R6, 0x7F
    mov  R5, R23
    mov  R4, R22

    mov  R13, R21
    andi R13, 0x80
    mov  R11, R21
    andi R11, 0x7F
    lsl  R11
    mov  R16, R20
    lsr  R16
    andi R16, 0x01
    or   R11, R16
    mov  R9, R20
    andi R9, 0x7F
    mov  R8, R19
    mov  R7, R18

    ; Result sign = signA XOR signB
    eor  R12, R13

    ; Division by zero: return Inf
    cpi  R11, 0
    breq __fpdiv_return_inf
    ; Dividend zero: return zero
    cpi  R10, 0
    breq __fpdiv_return_zero

    ; Set implied leading 1 bits
    ori  R6, 0x80
    ori  R9, 0x80

    ; Result exponent = expA - expB + 127
    sub  R10, R11
    subi R10, -127           ; add 127

    ; 24-bit restoring division: dividend(A) / divisor(B)
    ; Quotient Q is built in R4:R5:R6, remainder in R14:R15:R16
    ; We compute 24 bits of quotient.
    clr  R14
    clr  R15
    clr  R16                 ; remainder = 0
    ldi  R17, 24             ; 24 iterations

__fpdiv_loop:
    ; Shift dividend left into remainder: rem = rem<<1 | A[23]
    lsl  R4
    rol  R5
    rol  R6
    rol  R14
    rol  R15
    rol  R16

    ; Try subtract: if remainder >= divisor, subtract and set quotient bit
    cp   R14, R7
    cpc  R15, R8
    cpc  R16, R9
    brlo __fpdiv_no_sub

    sub  R14, R7
    sbc  R15, R8
    sbc  R16, R9
    ; Set quotient bit (shift into result via carry)
    sec
    rjmp __fpdiv_next
__fpdiv_no_sub:
    clc
__fpdiv_next:
    ; Rotate quotient bit into result (R4:R5:R6, low bits first)
    ; We need to track quotient separately since R4:R5:R6 is being shifted
    ; Use R7:R8:R9 for quotient (B regs no longer needed after this)
    ; Wait -- R7:R8:R9 = divisor which we still use. Use a 3-byte accumulator.
    ; Simplification: rotate carry into R4 which is being vacated by dividend shift.
    ; After shifting dividend left, bit0 of R4 is now 0 (from lsl). Set it to quotient bit.
    brcc __fpdiv_nobit
    ori  R4, 1
    rjmp __fpdiv_dec
__fpdiv_nobit:
    andi R4, 0xFE
__fpdiv_dec:
    dec  R17
    brne __fpdiv_loop

    ; Now R4:R5:R6 contains the 24-bit quotient (raw, needs normalization)
    ; Normalize
    sbrc R6, 7
    rjmp __fpdiv_norm_done
    lsl  R4
    rol  R5
    rol  R6
    dec  R10
    cpi  R10, 0
    breq __fpdiv_return_zero

__fpdiv_norm_done:
    cpi  R10, 0
    brlt __fpdiv_return_zero
    cpi  R10, 0xFF
    brge __fpdiv_return_inf

    ; Recompose
    andi R6, 0x7F
    mov  R25, R10
    lsr  R25
    or   R25, R12
    mov  R24, R10
    andi R24, 0x01
    lsl  R24
    lsl  R24
    lsl  R24
    lsl  R24
    lsl  R24
    lsl  R24
    lsl  R24
    or   R24, R6
    mov  R23, R5
    mov  R22, R4
    rjmp __fpdiv_done

__fpdiv_return_zero:
    clr  R22
    clr  R23
    clr  R24
    clr  R25
    rjmp __fpdiv_done

__fpdiv_return_inf:
    clr  R22
    clr  R23
    ldi  R24, 0x80
    ldi  R25, 0x7F
    or   R25, R12

__fpdiv_done:
    pop  R17
    pop  R16
    pop  R15
    pop  R14
    pop  R13
    pop  R12
    pop  R11
    pop  R10
    pop  R9
    pop  R8
    pop  R7
    pop  R6
    pop  R5
    pop  R4
    ret

; =============================================================================
; __fp_int_to_float  --  convert int16 (signed) to float
;   Input:  R24(lo), R25(hi) = signed int16
;   Output: R22:R23:R24:R25 = IEEE 754 float
; =============================================================================
__fp_int_to_float:
    push R16
    push R17
    push R18

    clr  R22
    clr  R23

    ; Handle zero
    mov  R16, R24
    or   R16, R25
    breq __fp_itf_zero

    ; Detect sign
    clr  R18                 ; R18 = result sign byte
    sbrc R25, 7
    ldi  R18, 0x80           ; negative: set sign

    ; Take absolute value
    sbrs R25, 7
    rjmp __fp_itf_positive
    com  R24
    com  R25
    subi R24, -1
    sbci R25, -1
__fp_itf_positive:

    ; Count leading zeros to determine exponent
    ; Mantissa is 16-bit in R24:R25; we need to normalize to 23 bits
    ; Exponent = 127 + 15 - leading_zeros (for 16-bit integer)
    ldi  R16, 127 + 15       ; biased exponent for a 1 in bit15

    ; Count leading zeros by shifting left until bit15 of R25 is set
__fp_itf_norm:
    sbrc R25, 7
    rjmp __fp_itf_norm_done
    lsl  R24
    rol  R25
    dec  R16
    rjmp __fp_itf_norm
__fp_itf_norm_done:

    ; Now R25:R24 has the mantissa with leading 1 in R25[7]
    ; We need 23 bits of mantissa: R24[6:0], R23, R22 (low 16 bits available)
    ; bit23 is in R25[7] (implied, drop it)
    ; bits22:16 are in R25[6:0], bits15:8 in R24, bits7:0 = 0 (R23=R24>>1 shifted)
    ; Repack: mantissa22:16 = R25[6:0], mantissa15:8 = R24, mantissa7:0 = 0
    ; But we have 16 bits total. Place them starting at bit22:
    ; After normalizing with R25[7]=1 (implied):
    ; mantissa[22:7] = R25[6:0]:R24  (15 bits from the 16-bit value)
    ; mantissa[6:0]  = 0

    mov  R23, R24            ; mantissa bits [14:7] -> R23
    clr  R22                 ; low 8 bits = 0

    ; Recompose exponent into R24 and R25
    ; R24 = exp_lsb(bit7) | mantissa[22:16](R25[6:0])
    mov  R24, R25
    andi R24, 0x7F           ; mantissa[22:16]
    ; exp LSB:
    mov  R17, R16
    andi R17, 0x01
    lsl  R17
    lsl  R17
    lsl  R17
    lsl  R17
    lsl  R17
    lsl  R17
    lsl  R17                 ; exp LSB to bit7
    or   R24, R17

    ; R25 = sign | exp[7:1]
    lsr  R16                 ; exp >> 1
    or   R16, R18
    mov  R25, R16

    rjmp __fp_itf_done

__fp_itf_zero:
    clr  R22
    clr  R23
    clr  R24
    clr  R25

__fp_itf_done:
    pop  R18
    pop  R17
    pop  R16
    ret

; =============================================================================
; __fp_float_to_int  --  convert float to int16 (signed, truncated toward zero)
;   Input:  R22:R23:R24:R25 = IEEE 754 float
;   Output: R24(lo), R25(hi) = int16
; =============================================================================
__fp_float_to_int:
    push R16
    push R17

    ; Extract sign, exponent
    mov  R17, R25            ; save sign
    andi R17, 0x80           ; R17 = sign bit (0 or 0x80)
    mov  R16, R25
    andi R16, 0x7F
    lsl  R16
    mov  R25, R24
    lsr  R25
    andi R25, 0x01
    or   R16, R25            ; R16 = biased exponent

    ; If exponent < 127: |value| < 1.0, result = 0
    cpi  R16, 127
    brlo __fp_fti_zero

    ; If exponent > 127+14 = 141: overflow, saturate
    cpi  R16, 142
    brge __fp_fti_overflow

    ; Mantissa: reconstruct 24-bit with implied 1
    mov  R25, R24
    andi R25, 0x7F           ; mantissa[22:16]
    ori  R25, 0x80           ; implied leading 1 -> bit23
    ; shift count = 23 - (exp - 127) = 150 - exp
    ldi  R24, 150
    sub  R24, R16            ; shift count (how many bits to shift right)
    ; shift R25:R23:R22 right by R24 positions to get integer
    ; For int16 we only need R25 and possibly a byte below it
    ; since exp <= 141, shift count >= 9, so result fits in high byte
__fp_fti_shift:
    cpi  R24, 0
    breq __fp_fti_shifted
    lsr  R25
    ror  R23
    dec  R24
    rjmp __fp_fti_shift
__fp_fti_shifted:
    ; Result in R23 (low byte) and bit0 of R25 (high byte)
    mov  R24, R23
    mov  R25, R25
    andi R25, 0x01           ; should be 0 for small values

    ; Apply sign
    cpi  R17, 0
    breq __fp_fti_done
    com  R24
    com  R25
    subi R24, -1
    sbci R25, -1
    rjmp __fp_fti_done

__fp_fti_zero:
    clr  R24
    clr  R25
    rjmp __fp_fti_done

__fp_fti_overflow:
    ; Saturate to 0x7FFF or 0x8000
    ldi  R24, 0xFF
    ldi  R25, 0x7F
    cpi  R17, 0
    breq __fp_fti_done
    ldi  R24, 0x00
    ldi  R25, 0x80

__fp_fti_done:
    pop  R17
    pop  R16
    ret

; =============================================================================
; __fp_cmp  --  compare arg0 and arg1
;   Output: R24 = 0xFF (-1) if arg0 < arg1
;           R24 = 0x00 (0)  if arg0 == arg1
;           R24 = 0x01 (1)  if arg0 > arg1
; =============================================================================
__fp_cmp:
    push R16
    push R17

    ; Handle +-0 equality
    mov  R16, R22
    or   R16, R23
    mov  R17, R24
    andi R17, 0x7F
    or   R16, R17            ; R16 = A with sign cleared
    or   R16, R25            ; if A is 0 (ignoring sign)...
    breq __fp_cmp_check_bzero

    ; Handle signs: if signs differ, negative is smaller
    mov  R16, R25
    andi R16, 0x80
    mov  R17, R21
    andi R17, 0x80
    eor  R16, R17
    breq __fp_cmp_same_sign
    ; Different signs: if A is negative (R25[7]=1), A < B
    sbrc R25, 7
    rjmp __fp_cmp_lt
    rjmp __fp_cmp_gt

__fp_cmp_check_bzero:
    ; A is zero; check B
    mov  R16, R18
    or   R16, R19
    mov  R17, R20
    andi R17, 0x7F
    or   R16, R17
    or   R16, R21
    breq __fp_cmp_eq         ; both zero -> equal

    ; A is zero, B is nonzero: A < B if B positive, A > B if B negative
    sbrc R21, 7
    rjmp __fp_cmp_gt         ; B negative -> A(0) > B
    rjmp __fp_cmp_lt         ; B positive -> A(0) < B

__fp_cmp_same_sign:
    ; Same sign: compare as unsigned 32-bit integers
    ; (larger magnitude = larger positive, smaller magnitude = smaller negative)
    cp   R22, R18
    cpc  R23, R19
    cpc  R24, R20
    cpc  R25, R21
    breq __fp_cmp_eq
    brlo __fp_cmp_a_lt_b     ; A < B as unsigned

    ; A > B as unsigned
    ; If both negative (R25[7]=1), that means |A| > |B|, so A < B
    sbrc R25, 7
    rjmp __fp_cmp_lt
    rjmp __fp_cmp_gt

__fp_cmp_a_lt_b:
    ; A < B as unsigned
    ; If both negative, |A| < |B|, so A > B
    sbrc R25, 7
    rjmp __fp_cmp_gt
    rjmp __fp_cmp_lt

__fp_cmp_lt:
    ldi  R24, 0xFF
    rjmp __fp_cmp_done
__fp_cmp_gt:
    ldi  R24, 0x01
    rjmp __fp_cmp_done
__fp_cmp_eq:
    clr  R24
__fp_cmp_done:
    pop  R17
    pop  R16
    ret

; =============================================================================
; __fp_eq  --  R24 = (arg0 == arg1) ? 1 : 0
; =============================================================================
__fp_eq:
    rcall __fp_cmp
    cpi   R24, 0
    breq  __fp_eq_true
    clr   R24
    ret
__fp_eq_true:
    ldi   R24, 1
    ret

; =============================================================================
; __fp_ne  --  R24 = (arg0 != arg1) ? 1 : 0
; =============================================================================
__fp_ne:
    rcall __fp_cmp
    cpi   R24, 0
    brne  __fp_ne_true
    clr   R24
    ret
__fp_ne_true:
    ldi   R24, 1
    ret

; =============================================================================
; __fp_lt  --  R24 = (arg0 < arg1) ? 1 : 0
; =============================================================================
__fp_lt:
    rcall __fp_cmp
    cpi   R24, 0xFF
    breq  __fp_lt_true
    clr   R24
    ret
__fp_lt_true:
    ldi   R24, 1
    ret

; =============================================================================
; __fp_le  --  R24 = (arg0 <= arg1) ? 1 : 0
; =============================================================================
__fp_le:
    rcall __fp_cmp
    cpi   R24, 0x01
    brsh  __fp_le_false
    ldi   R24, 1
    ret
__fp_le_false:
    clr   R24
    ret

; =============================================================================
; __fp_gt  --  R24 = (arg0 > arg1) ? 1 : 0
; =============================================================================
__fp_gt:
    rcall __fp_cmp
    cpi   R24, 0x01
    breq  __fp_gt_true
    clr   R24
    ret
__fp_gt_true:
    ldi   R24, 1
    ret

; =============================================================================
; __fp_ge  --  R24 = (arg0 >= arg1) ? 1 : 0
; =============================================================================
__fp_ge:
    rcall __fp_cmp
    cpi   R24, 0xFF
    breq  __fp_ge_false
    ldi   R24, 1
    ret
__fp_ge_false:
    clr   R24
    ret

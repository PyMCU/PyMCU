; -----------------------------------------------------------------------------
; PyMCU soft-float for PIC18 -- IEEE-754 binary32
; Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
;
; SPDX-License-Identifier: MIT
; -----------------------------------------------------------------------------
;
; Calling convention
;   __fp_a  4 bytes, little-endian: operand A, and the result of every
;           arithmetic routine.
;   __fp_b  4 bytes, little-endian: operand B.
;   __fp_s  8 bytes of scratch, owned by these routines.
;   The cells are allocated in access RAM by PIC18CodeGen, so an interrupt
;   handler that reaches float code saves and restores them like any other
;   backend scratch.
;
; NaN and infinities are handled but never signalled: no flags, no traps.
; The algorithms are transliterated from a Python reference verified bit for
; bit against CPython (0 mismatches over ~1.17M cases, subnormals included).
; -----------------------------------------------------------------------------

; --- __fp_cmp: W = 0x00 if A == B, 0x01 if A > B, 0xFF if A < B --------------
; Unordered (either operand NaN) reports 0x01, the libgcc __cmpsf2 convention.
__fp_cmp:
	MOVF	__fp_a+3, W
	ANDLW	0x7F
	XORLW	0x7F
	BNZ	__fp_cmp_a_ok
	BTFSS	__fp_a+2, 7, ACCESS
	BRA	__fp_cmp_a_ok
	MOVF	__fp_a+2, W
	ANDLW	0x7F
	IORWF	__fp_a+1, W
	IORWF	__fp_a+0, W
	BNZ	__fp_cmp_unordered
__fp_cmp_a_ok:
	MOVF	__fp_b+3, W
	ANDLW	0x7F
	XORLW	0x7F
	BNZ	__fp_cmp_b_ok
	BTFSS	__fp_b+2, 7, ACCESS
	BRA	__fp_cmp_b_ok
	MOVF	__fp_b+2, W
	ANDLW	0x7F
	IORWF	__fp_b+1, W
	IORWF	__fp_b+0, W
	BNZ	__fp_cmp_unordered

; +0.0 and -0.0 compare equal, but the total-order key below separates them.
__fp_cmp_b_ok:
	MOVF	__fp_a+0, W
	IORWF	__fp_a+1, W
	IORWF	__fp_a+2, W
	MOVWF	__fp_s
	MOVF	__fp_a+3, W
	ANDLW	0x7F
	IORWF	__fp_s, W
	BNZ	__fp_cmp_keys
	MOVF	__fp_b+0, W
	IORWF	__fp_b+1, W
	IORWF	__fp_b+2, W
	MOVWF	__fp_s
	MOVF	__fp_b+3, W
	ANDLW	0x7F
	IORWF	__fp_s, W
	BZ	__fp_cmp_equal

; Total order: a negative float maps to its one's complement, a positive one
; to itself with the top bit set. Comparing the keys as unsigned 32-bit
; integers then yields the IEEE ordering.
__fp_cmp_keys:
	BTFSS	__fp_a+3, 7, ACCESS
	BRA	__fp_cmp_a_pos
	COMF	__fp_a+0, F
	COMF	__fp_a+1, F
	COMF	__fp_a+2, F
	COMF	__fp_a+3, F
	BRA	__fp_cmp_key_b
__fp_cmp_a_pos:
	BSF	__fp_a+3, 7, ACCESS
__fp_cmp_key_b:
	BTFSS	__fp_b+3, 7, ACCESS
	BRA	__fp_cmp_b_pos
	COMF	__fp_b+0, F
	COMF	__fp_b+1, F
	COMF	__fp_b+2, F
	COMF	__fp_b+3, F
	BRA	__fp_cmp_sub
__fp_cmp_b_pos:
	BSF	__fp_b+3, 7, ACCESS

; A - B across four bytes. IORWF keeps the carry chain intact (it writes Z and
; N only), so the final carry is the full-width borrow and __fp_s accumulates
; the difference bytes for the equality test.
__fp_cmp_sub:
	MOVF	__fp_b+0, W
	SUBWF	__fp_a+0, W
	MOVWF	__fp_s
	MOVF	__fp_b+1, W
	SUBWFB	__fp_a+1, W
	IORWF	__fp_s, F
	MOVF	__fp_b+2, W
	SUBWFB	__fp_a+2, W
	IORWF	__fp_s, F
	MOVF	__fp_b+3, W
	SUBWFB	__fp_a+3, W
	IORWF	__fp_s, F
	BNC	__fp_cmp_less
	MOVF	__fp_s, F
	BZ	__fp_cmp_equal
	RETLW	0x01
__fp_cmp_less:
	RETLW	0xFF
__fp_cmp_equal:
	RETLW	0x00
__fp_cmp_unordered:
	RETLW	0x01

; --- scratch layout ----------------------------------------------------------
; __fp_s is 16 bytes owned by these routines.
__fp_ma	EQU	__fp_s+0	; 4 bytes: working mantissa (28 significant bits max)
__fp_ex	EQU	__fp_s+4	; 2 bytes: working exponent, signed
__fp_sg	EQU	__fp_s+6	; result sign in bit 0
__fp_t	EQU	__fp_s+7	; byte temp
__fp_mb	EQU	__fp_s+8	; 4 bytes: second mantissa
__fp_eb	EQU	__fp_s+12	; 2 bytes: second exponent, signed
__fp_c	EQU	__fp_s+14	; loop counter
__fp_st	EQU	__fp_s+15	; sticky accumulator
__fp_p	EQU	__fp_s+16	; 6 bytes: 48-bit product

; --- __fp_round: pack (__fp_sg, __fp_ex, __fp_ma) into __fp_a ----------------
; __fp_ma carries the significand with 3 guard bits below it; the caller must
; have brought it under 2^32. Round to nearest, ties to even; overflow gives a
; signed infinity, underflow a subnormal or a signed zero.
__fp_round:
__fp_rnd_down:
	MOVF	__fp_ma+3, W
	ANDLW	0xF8
	BZ	__fp_rnd_up
	MOVF	__fp_ma+0, W
	ANDLW	0x01
	MOVWF	__fp_t
	BCF	STATUS, C, ACCESS
	RRCF	__fp_ma+3, F
	RRCF	__fp_ma+2, F
	RRCF	__fp_ma+1, F
	RRCF	__fp_ma+0, F
	MOVF	__fp_t, W
	IORWF	__fp_ma+0, F
	INCF	__fp_ex+0, F
	BNC	__fp_rnd_down
	INCF	__fp_ex+1, F
	BRA	__fp_rnd_down

__fp_rnd_up:
	MOVF	__fp_ma+3, W
	ANDLW	0xFC
	BNZ	__fp_rnd_limits		; already >= 2^26
	BTFSC	__fp_ex+1, 7, ACCESS
	BRA	__fp_rnd_limits		; exponent already negative
	MOVF	__fp_ex+1, F
	BNZ	__fp_rnd_shift_up
	MOVF	__fp_ex+0, W
	SUBLW	0x01
	BC	__fp_rnd_limits		; ex <= 1: cannot borrow from the exponent
__fp_rnd_shift_up:
	BCF	STATUS, C, ACCESS
	RLCF	__fp_ma+0, F
	RLCF	__fp_ma+1, F
	RLCF	__fp_ma+2, F
	RLCF	__fp_ma+3, F
	MOVLW	0x01
	SUBWF	__fp_ex+0, F
	BC	__fp_rnd_up
	DECF	__fp_ex+1, F
	BRA	__fp_rnd_up

; Both limits are checked here: an already-normalized mantissa must still be
; able to reach the subnormal path, which an overflow-only test skipped.
__fp_rnd_limits:
	BTFSC	__fp_ex+1, 7, ACCESS
	BRA	__fp_rnd_under		; exponent negative
	MOVF	__fp_ex+1, F
	BNZ	__fp_rnd_inf		; exponent >= 256
	MOVF	__fp_ex+0, W
	SUBLW	0xFE
	BNC	__fp_rnd_inf		; exponent > 254
	MOVF	__fp_ex+0, F
	BZ	__fp_rnd_under		; exponent 0
	BRA	__fp_rnd_pack

; ex <= 0: shift the mantissa down into the subnormal range, keeping a sticky.
__fp_rnd_under:
	COMF	__fp_ex+0, W
	ADDLW	0x02
	MOVWF	__fp_c			; shift = 1 - ex, low byte
	COMF	__fp_ex+1, W
	BTFSC	STATUS, C, ACCESS
	ADDLW	0x01
	MOVWF	__fp_t			; shift, high byte
	BNZ	__fp_rnd_zero		; shift >= 256
	MOVLW	0x1C
	SUBWF	__fp_c, W
	BC	__fp_rnd_zero		; shift > 27: nothing survives
	CLRF	__fp_ex+0
	CLRF	__fp_ex+1
__fp_rnd_under_loop:
	MOVF	__fp_c, F
	BZ	__fp_rnd_pack
	MOVF	__fp_ma+0, W
	ANDLW	0x01
	MOVWF	__fp_t
	BCF	STATUS, C, ACCESS
	RRCF	__fp_ma+3, F
	RRCF	__fp_ma+2, F
	RRCF	__fp_ma+1, F
	RRCF	__fp_ma+0, F
	MOVF	__fp_t, W
	IORWF	__fp_ma+0, F
	DECF	__fp_c, F
	BRA	__fp_rnd_under_loop

__fp_rnd_inf:
	CLRF	__fp_a+0
	CLRF	__fp_a+1
	MOVLW	0x80
	MOVWF	__fp_a+2
	MOVLW	0x7F
	MOVWF	__fp_a+3
	BRA	__fp_rnd_sign
__fp_rnd_zero:
	CLRF	__fp_a+0
	CLRF	__fp_a+1
	CLRF	__fp_a+2
	CLRF	__fp_a+3
	BRA	__fp_rnd_sign

; round to nearest, ties to even, then drop the 3 guard bits
__fp_rnd_pack:
	MOVF	__fp_ma+0, W
	ANDLW	0x07
	MOVWF	__fp_t
	BCF	STATUS, C, ACCESS
	RRCF	__fp_ma+3, F
	RRCF	__fp_ma+2, F
	RRCF	__fp_ma+1, F
	RRCF	__fp_ma+0, F
	BCF	STATUS, C, ACCESS
	RRCF	__fp_ma+3, F
	RRCF	__fp_ma+2, F
	RRCF	__fp_ma+1, F
	RRCF	__fp_ma+0, F
	BCF	STATUS, C, ACCESS
	RRCF	__fp_ma+3, F
	RRCF	__fp_ma+2, F
	RRCF	__fp_ma+1, F
	RRCF	__fp_ma+0, F
	MOVLW	0x05
	SUBWF	__fp_t, W
	BC	__fp_rnd_inc		; guard > 4
	MOVF	__fp_t, W
	XORLW	0x04
	BNZ	__fp_rnd_store		; guard < 4
	BTFSS	__fp_ma+0, 0, ACCESS
	BRA	__fp_rnd_store		; tie and even: stay
__fp_rnd_inc:
	INCF	__fp_ma+0, F
	BNC	__fp_rnd_carry_done
	INCF	__fp_ma+1, F
	BNC	__fp_rnd_carry_done
	INCF	__fp_ma+2, F
	BNC	__fp_rnd_carry_done
	INCF	__fp_ma+3, F
__fp_rnd_carry_done:
	MOVF	__fp_ma+3, F
	BZ	__fp_rnd_store
__fp_rnd_carry_out:
	BCF	STATUS, C, ACCESS
	RRCF	__fp_ma+3, F
	RRCF	__fp_ma+2, F
	RRCF	__fp_ma+1, F
	RRCF	__fp_ma+0, F
	INCF	__fp_ex+0, F
	BNC	__fp_rnd_recheck
	INCF	__fp_ex+1, F
__fp_rnd_recheck:
	MOVF	__fp_ex+1, F
	BNZ	__fp_rnd_inf
	MOVF	__fp_ex+0, W
	SUBLW	0xFE
	BNC	__fp_rnd_inf

; assemble sign:exponent:mantissa
__fp_rnd_store:
	MOVFF	__fp_ma+0, __fp_a+0
	MOVFF	__fp_ma+1, __fp_a+1
	MOVF	__fp_ma+2, W
	ANDLW	0x7F
	MOVWF	__fp_a+2
	CLRF	__fp_a+3
	BTFSS	__fp_ma+2, 7, ACCESS
	BRA	__fp_rnd_sign		; no implicit bit: subnormal, exponent 0
	BCF	STATUS, C, ACCESS
	RRCF	__fp_ex+0, W		; W = exp >> 1, C = exp bit 0
	MOVWF	__fp_a+3
	BTFSC	STATUS, C, ACCESS
	BSF	__fp_a+2, 7, ACCESS
__fp_rnd_sign:
	BTFSC	__fp_sg, 0, ACCESS
	BSF	__fp_a+3, 7, ACCESS
	RETURN

; --- __fp_sub / __fp_add: __fp_a = A +/- B -----------------------------------
; Operands in __fp_a and __fp_b; result in __fp_a. NaN propagates as a quiet
; NaN, inf - inf is a NaN, and a cancelling subtraction returns +0.
__fp_sub:
	MOVLW	0x80
	XORWF	__fp_b+3, F
__fp_add:
	MOVF	__fp_a+3, W
	ANDLW	0x7F
	XORLW	0x7F
	BNZ	__fp_add_a_finite
	BTFSS	__fp_a+2, 7, ACCESS
	BRA	__fp_add_a_finite
	MOVF	__fp_a+2, W
	ANDLW	0x7F
	IORWF	__fp_a+1, W
	IORWF	__fp_a+0, W
	BNZ	__fp_add_qnan		; A is NaN
	BRA	__fp_add_a_inf
__fp_add_a_finite:
	MOVF	__fp_b+3, W
	ANDLW	0x7F
	XORLW	0x7F
	BNZ	__fp_add_unpack
	BTFSS	__fp_b+2, 7, ACCESS
	BRA	__fp_add_unpack
	MOVF	__fp_b+2, W
	ANDLW	0x7F
	IORWF	__fp_b+1, W
	IORWF	__fp_b+0, W
	BNZ	__fp_add_qnan		; B is NaN
	MOVFF	__fp_b+0, __fp_a+0	; B is infinite, A is finite
	MOVFF	__fp_b+1, __fp_a+1
	MOVFF	__fp_b+2, __fp_a+2
	MOVFF	__fp_b+3, __fp_a+3
	RETURN

; A is infinite: only inf + (-inf) is undefined; otherwise A already is the sum.
__fp_add_a_inf:
	MOVF	__fp_b+3, W
	ANDLW	0x7F
	XORLW	0x7F
	BNZ	__fp_add_keep_a
	BTFSS	__fp_b+2, 7, ACCESS
	BRA	__fp_add_keep_a
	MOVF	__fp_b+2, W
	ANDLW	0x7F
	IORWF	__fp_b+1, W
	IORWF	__fp_b+0, W
	BNZ	__fp_add_qnan		; B is NaN
	MOVF	__fp_a+3, W
	XORWF	__fp_b+3, W
	ANDLW	0x80
	BNZ	__fp_add_qnan		; inf + (-inf)
__fp_add_keep_a:
	RETURN
__fp_add_qnan:
	CLRF	__fp_a+0
	CLRF	__fp_a+1
	MOVLW	0xC0
	MOVWF	__fp_a+2
	MOVLW	0x7F
	MOVWF	__fp_a+3
	RETURN

; unpack A -> __fp_sg / __fp_ex / __fp_ma (significand << 3)
__fp_add_unpack:
	CLRF	__fp_sg
	BTFSC	__fp_a+3, 7, ACCESS
	BSF	__fp_sg, 0, ACCESS
	MOVF	__fp_a+3, W
	ANDLW	0x7F
	MOVWF	__fp_t
	BCF	STATUS, C, ACCESS
	RLCF	__fp_t, F
	BTFSC	__fp_a+2, 7, ACCESS
	BSF	__fp_t, 0, ACCESS
	MOVFF	__fp_t, __fp_ex
	CLRF	__fp_ex+1
	MOVFF	__fp_a+0, __fp_ma+0
	MOVFF	__fp_a+1, __fp_ma+1
	MOVF	__fp_a+2, W
	ANDLW	0x7F
	MOVWF	__fp_ma+2
	CLRF	__fp_ma+3
	MOVF	__fp_ex+0, F
	BZ	__fp_add_a_sub
	BSF	__fp_ma+2, 7, ACCESS	; implicit bit
	BRA	__fp_add_a_scale
__fp_add_a_sub:
	MOVLW	0x01
	MOVWF	__fp_ex+0		; subnormals share exponent 1
__fp_add_a_scale:
	MOVLW	0x03
	MOVWF	__fp_c
__fp_add_a_shl:
	BCF	STATUS, C, ACCESS
	RLCF	__fp_ma+0, F
	RLCF	__fp_ma+1, F
	RLCF	__fp_ma+2, F
	RLCF	__fp_ma+3, F
	DECFSZ	__fp_c, F
	BRA	__fp_add_a_shl

; unpack B -> __fp_st / __fp_eb / __fp_mb
	CLRF	__fp_st
	BTFSC	__fp_b+3, 7, ACCESS
	BSF	__fp_st, 0, ACCESS
	MOVF	__fp_b+3, W
	ANDLW	0x7F
	MOVWF	__fp_t
	BCF	STATUS, C, ACCESS
	RLCF	__fp_t, F
	BTFSC	__fp_b+2, 7, ACCESS
	BSF	__fp_t, 0, ACCESS
	MOVFF	__fp_t, __fp_eb
	CLRF	__fp_eb+1
	MOVFF	__fp_b+0, __fp_mb+0
	MOVFF	__fp_b+1, __fp_mb+1
	MOVF	__fp_b+2, W
	ANDLW	0x7F
	MOVWF	__fp_mb+2
	CLRF	__fp_mb+3
	MOVF	__fp_eb+0, F
	BZ	__fp_add_b_sub
	BSF	__fp_mb+2, 7, ACCESS
	BRA	__fp_add_b_scale
__fp_add_b_sub:
	MOVLW	0x01
	MOVWF	__fp_eb+0
__fp_add_b_scale:
	MOVLW	0x03
	MOVWF	__fp_c
__fp_add_b_shl:
	BCF	STATUS, C, ACCESS
	RLCF	__fp_mb+0, F
	RLCF	__fp_mb+1, F
	RLCF	__fp_mb+2, F
	RLCF	__fp_mb+3, F
	DECFSZ	__fp_c, F
	BRA	__fp_add_b_shl

; keep the larger magnitude in A so the alignment shift is never negative
	MOVF	__fp_eb+0, W
	SUBWF	__fp_ex+0, W
	BNC	__fp_add_swap
	BNZ	__fp_add_ordered
	MOVF	__fp_mb+0, W
	SUBWF	__fp_ma+0, W
	MOVF	__fp_mb+1, W
	SUBWFB	__fp_ma+1, W
	MOVF	__fp_mb+2, W
	SUBWFB	__fp_ma+2, W
	MOVF	__fp_mb+3, W
	SUBWFB	__fp_ma+3, W
	BC	__fp_add_ordered
__fp_add_swap:
	MOVF	__fp_ma+0, W
	MOVFF	__fp_mb+0, __fp_ma+0
	MOVWF	__fp_mb+0
	MOVF	__fp_ma+1, W
	MOVFF	__fp_mb+1, __fp_ma+1
	MOVWF	__fp_mb+1
	MOVF	__fp_ma+2, W
	MOVFF	__fp_mb+2, __fp_ma+2
	MOVWF	__fp_mb+2
	MOVF	__fp_ma+3, W
	MOVFF	__fp_mb+3, __fp_ma+3
	MOVWF	__fp_mb+3
	MOVF	__fp_ex+0, W
	MOVFF	__fp_eb+0, __fp_ex+0
	MOVWF	__fp_eb+0
	MOVF	__fp_sg, W
	MOVFF	__fp_st, __fp_sg
	MOVWF	__fp_st

; align B: shift right by the exponent difference, folding the lost bits into
; a sticky bit so __fp_round can break ties correctly.
__fp_add_ordered:
	MOVF	__fp_eb+0, W
	SUBWF	__fp_ex+0, W
	MOVWF	__fp_c
	BZ	__fp_add_aligned
	MOVLW	0x1C
	SUBWF	__fp_c, W
	BC	__fp_add_far
__fp_add_shift:
	MOVF	__fp_mb+0, W
	ANDLW	0x01
	MOVWF	__fp_t
	BCF	STATUS, C, ACCESS
	RRCF	__fp_mb+3, F
	RRCF	__fp_mb+2, F
	RRCF	__fp_mb+1, F
	RRCF	__fp_mb+0, F
	MOVF	__fp_t, W
	IORWF	__fp_mb+0, F
	DECFSZ	__fp_c, F
	BRA	__fp_add_shift
	BRA	__fp_add_aligned
__fp_add_far:
	MOVF	__fp_mb+0, W
	IORWF	__fp_mb+1, W
	IORWF	__fp_mb+2, W
	IORWF	__fp_mb+3, W
	MOVWF	__fp_t
	CLRF	__fp_mb+0
	CLRF	__fp_mb+1
	CLRF	__fp_mb+2
	CLRF	__fp_mb+3
	MOVF	__fp_t, F
	BZ	__fp_add_aligned
	BSF	__fp_mb+0, 0, ACCESS

__fp_add_aligned:
	MOVF	__fp_sg, W
	XORWF	__fp_st, W
	ANDLW	0x01
	BNZ	__fp_add_unlike
	MOVF	__fp_mb+0, W
	ADDWF	__fp_ma+0, F
	MOVF	__fp_mb+1, W
	ADDWFC	__fp_ma+1, F
	MOVF	__fp_mb+2, W
	ADDWFC	__fp_ma+2, F
	MOVF	__fp_mb+3, W
	ADDWFC	__fp_ma+3, F
	BRA	__fp_round
__fp_add_unlike:
	MOVF	__fp_mb+0, W
	SUBWF	__fp_ma+0, F
	MOVF	__fp_mb+1, W
	SUBWFB	__fp_ma+1, F
	MOVF	__fp_mb+2, W
	SUBWFB	__fp_ma+2, F
	MOVF	__fp_mb+3, W
	SUBWFB	__fp_ma+3, F
	MOVF	__fp_ma+0, W
	IORWF	__fp_ma+1, W
	IORWF	__fp_ma+2, W
	IORWF	__fp_ma+3, W
	BZ	__fp_add_cancel		; conditional branches reach +/-128 words only
	BRA	__fp_round
__fp_add_cancel:
	CLRF	__fp_a+0		; exact cancellation is +0
	CLRF	__fp_a+1
	CLRF	__fp_a+2
	CLRF	__fp_a+3
	RETURN

; --- __fp_mul: __fp_a = A * B ------------------------------------------------
__fp_mul:
	MOVF	__fp_a+3, W
	XORWF	__fp_b+3, W
	ANDLW	0x80
	MOVWF	__fp_t
	CLRF	__fp_sg
	BTFSC	__fp_t, 7, ACCESS
	BSF	__fp_sg, 0, ACCESS	; sign is the XOR of the operand signs

	MOVF	__fp_a+3, W
	ANDLW	0x7F
	XORLW	0x7F
	BNZ	__fp_mul_a_notnan
	BTFSS	__fp_a+2, 7, ACCESS
	BRA	__fp_mul_a_notnan
	MOVF	__fp_a+2, W
	ANDLW	0x7F
	IORWF	__fp_a+1, W
	IORWF	__fp_a+0, W
	BNZ	__fp_mul_nan_hop
__fp_mul_a_notnan:
	MOVF	__fp_b+3, W
	ANDLW	0x7F
	XORLW	0x7F
	BNZ	__fp_mul_b_notnan
	BTFSS	__fp_b+2, 7, ACCESS
	BRA	__fp_mul_b_notnan
	MOVF	__fp_b+2, W
	ANDLW	0x7F
	IORWF	__fp_b+1, W
	IORWF	__fp_b+0, W
	BNZ	__fp_mul_nan_hop
	BRA	__fp_mul_b_notnan
__fp_mul_nan_hop:
	BRA	__fp_mul_qnan
__fp_mul_b_notnan:
	MOVF	__fp_a+3, W		; A infinite?
	ANDLW	0x7F
	XORLW	0x7F
	BNZ	__fp_mul_a_finite
	BTFSS	__fp_a+2, 7, ACCESS
	BRA	__fp_mul_a_finite
	MOVF	__fp_b+0, W		; 0 * inf is undefined
	IORWF	__fp_b+1, W
	IORWF	__fp_b+2, W
	MOVWF	__fp_t
	MOVF	__fp_b+3, W
	ANDLW	0x7F
	IORWF	__fp_t, W
	BZ	__fp_mul_nan_hop
	BRA	__fp_mul_inf
__fp_mul_a_finite:
	MOVF	__fp_b+3, W		; B infinite?
	ANDLW	0x7F
	XORLW	0x7F
	BNZ	__fp_mul_finite
	BTFSS	__fp_b+2, 7, ACCESS
	BRA	__fp_mul_finite
	MOVF	__fp_a+0, W
	IORWF	__fp_a+1, W
	IORWF	__fp_a+2, W
	MOVWF	__fp_t
	MOVF	__fp_a+3, W
	ANDLW	0x7F
	IORWF	__fp_t, W
	BZ	__fp_mul_nan_hop
	BRA	__fp_mul_inf

__fp_mul_finite:
	MOVF	__fp_a+0, W		; either operand zero -> signed zero
	IORWF	__fp_a+1, W
	IORWF	__fp_a+2, W
	MOVWF	__fp_t
	MOVF	__fp_a+3, W
	ANDLW	0x7F
	IORWF	__fp_t, W
	BZ	__fp_mul_zero_hop
	MOVF	__fp_b+0, W
	IORWF	__fp_b+1, W
	IORWF	__fp_b+2, W
	MOVWF	__fp_t
	MOVF	__fp_b+3, W
	ANDLW	0x7F
	IORWF	__fp_t, W
	BZ	__fp_mul_zero_hop
	BRA	__fp_mul_unpack_a
__fp_mul_zero_hop:
	BRA	__fp_mul_zero

; unpack and normalize A: significand into __fp_ma (24 bits), exponent __fp_ex
__fp_mul_unpack_a:
	MOVF	__fp_a+3, W
	ANDLW	0x7F
	MOVWF	__fp_t
	BCF	STATUS, C, ACCESS
	RLCF	__fp_t, F
	BTFSC	__fp_a+2, 7, ACCESS
	BSF	__fp_t, 0, ACCESS
	MOVFF	__fp_t, __fp_ex
	CLRF	__fp_ex+1
	MOVFF	__fp_a+0, __fp_ma+0
	MOVFF	__fp_a+1, __fp_ma+1
	MOVF	__fp_a+2, W
	ANDLW	0x7F
	MOVWF	__fp_ma+2
	CLRF	__fp_ma+3
	MOVF	__fp_ex+0, F
	BZ	__fp_mul_a_norm
	BSF	__fp_ma+2, 7, ACCESS
	BRA	__fp_mul_b_unpack
__fp_mul_a_norm:
	MOVLW	0x01
	MOVWF	__fp_ex+0
__fp_mul_a_shl:
	BTFSC	__fp_ma+2, 7, ACCESS
	BRA	__fp_mul_b_unpack
	BCF	STATUS, C, ACCESS
	RLCF	__fp_ma+0, F
	RLCF	__fp_ma+1, F
	RLCF	__fp_ma+2, F
	MOVLW	0x01
	SUBWF	__fp_ex+0, F
	BC	__fp_mul_a_shl
	DECF	__fp_ex+1, F
	BRA	__fp_mul_a_shl

__fp_mul_b_unpack:
	MOVF	__fp_b+3, W
	ANDLW	0x7F
	MOVWF	__fp_t
	BCF	STATUS, C, ACCESS
	RLCF	__fp_t, F
	BTFSC	__fp_b+2, 7, ACCESS
	BSF	__fp_t, 0, ACCESS
	MOVFF	__fp_t, __fp_eb
	CLRF	__fp_eb+1
	MOVFF	__fp_b+0, __fp_mb+0
	MOVFF	__fp_b+1, __fp_mb+1
	MOVF	__fp_b+2, W
	ANDLW	0x7F
	MOVWF	__fp_mb+2
	CLRF	__fp_mb+3
	MOVF	__fp_eb+0, F
	BZ	__fp_mul_b_norm
	BSF	__fp_mb+2, 7, ACCESS
	BRA	__fp_mul_product
__fp_mul_b_norm:
	MOVLW	0x01
	MOVWF	__fp_eb+0
__fp_mul_b_shl:
	BTFSC	__fp_mb+2, 7, ACCESS
	BRA	__fp_mul_product
	BCF	STATUS, C, ACCESS
	RLCF	__fp_mb+0, F
	RLCF	__fp_mb+1, F
	RLCF	__fp_mb+2, F
	MOVLW	0x01
	SUBWF	__fp_eb+0, F
	BC	__fp_mul_b_shl
	DECF	__fp_eb+1, F
	BRA	__fp_mul_b_shl

__fp_mul_product:
; 24 x 24 -> 48 bit schoolbook product, accumulated into __fp_p (6 bytes).
	CLRF	__fp_p
	CLRF	__fp_p+1
	CLRF	__fp_p+2
	CLRF	__fp_p+3
	CLRF	__fp_p+4
	CLRF	__fp_p+5
	MOVF	__fp_ma, W
	MULWF	__fp_mb, ACCESS
	MOVF	PRODL, W, ACCESS
	ADDWF	__fp_p, F
	MOVF	PRODH, W, ACCESS
	ADDWFC	__fp_p+1, F
	BNC	__fp_mul_p1
	INCF	__fp_p+2, F
	BNC	__fp_mul_p1
	INCF	__fp_p+3, F
	BNC	__fp_mul_p1
	INCF	__fp_p+4, F
	BNC	__fp_mul_p1
	INCF	__fp_p+5, F
__fp_mul_p1:
	MOVF	__fp_ma, W
	MULWF	__fp_mb+1, ACCESS
	MOVF	PRODL, W, ACCESS
	ADDWF	__fp_p+1, F
	MOVF	PRODH, W, ACCESS
	ADDWFC	__fp_p+2, F
	BNC	__fp_mul_p2
	INCF	__fp_p+3, F
	BNC	__fp_mul_p2
	INCF	__fp_p+4, F
	BNC	__fp_mul_p2
	INCF	__fp_p+5, F
__fp_mul_p2:
	MOVF	__fp_ma, W
	MULWF	__fp_mb+2, ACCESS
	MOVF	PRODL, W, ACCESS
	ADDWF	__fp_p+2, F
	MOVF	PRODH, W, ACCESS
	ADDWFC	__fp_p+3, F
	BNC	__fp_mul_p3
	INCF	__fp_p+4, F
	BNC	__fp_mul_p3
	INCF	__fp_p+5, F
__fp_mul_p3:
	MOVF	__fp_ma+1, W
	MULWF	__fp_mb, ACCESS
	MOVF	PRODL, W, ACCESS
	ADDWF	__fp_p+1, F
	MOVF	PRODH, W, ACCESS
	ADDWFC	__fp_p+2, F
	BNC	__fp_mul_p4
	INCF	__fp_p+3, F
	BNC	__fp_mul_p4
	INCF	__fp_p+4, F
	BNC	__fp_mul_p4
	INCF	__fp_p+5, F
__fp_mul_p4:
	MOVF	__fp_ma+1, W
	MULWF	__fp_mb+1, ACCESS
	MOVF	PRODL, W, ACCESS
	ADDWF	__fp_p+2, F
	MOVF	PRODH, W, ACCESS
	ADDWFC	__fp_p+3, F
	BNC	__fp_mul_p5
	INCF	__fp_p+4, F
	BNC	__fp_mul_p5
	INCF	__fp_p+5, F
__fp_mul_p5:
	MOVF	__fp_ma+1, W
	MULWF	__fp_mb+2, ACCESS
	MOVF	PRODL, W, ACCESS
	ADDWF	__fp_p+3, F
	MOVF	PRODH, W, ACCESS
	ADDWFC	__fp_p+4, F
	BNC	__fp_mul_p6
	INCF	__fp_p+5, F
__fp_mul_p6:
	MOVF	__fp_ma+2, W
	MULWF	__fp_mb, ACCESS
	MOVF	PRODL, W, ACCESS
	ADDWF	__fp_p+2, F
	MOVF	PRODH, W, ACCESS
	ADDWFC	__fp_p+3, F
	BNC	__fp_mul_p7
	INCF	__fp_p+4, F
	BNC	__fp_mul_p7
	INCF	__fp_p+5, F
__fp_mul_p7:
	MOVF	__fp_ma+2, W
	MULWF	__fp_mb+1, ACCESS
	MOVF	PRODL, W, ACCESS
	ADDWF	__fp_p+3, F
	MOVF	PRODH, W, ACCESS
	ADDWFC	__fp_p+4, F
	BNC	__fp_mul_p8
	INCF	__fp_p+5, F
__fp_mul_p8:
	MOVF	__fp_ma+2, W
	MULWF	__fp_mb+2, ACCESS
	MOVF	PRODL, W, ACCESS
	ADDWF	__fp_p+4, F
	MOVF	PRODH, W, ACCESS
	ADDWFC	__fp_p+5, F
__fp_mul_p9:

; exponent = ea + eb - 126 (the 21-bit reduction below is folded into it)
	MOVF	__fp_eb+0, W
	ADDWF	__fp_ex+0, F
	MOVF	__fp_eb+1, W
	ADDWFC	__fp_ex+1, F
	MOVLW	0x7E
	SUBWF	__fp_ex+0, F
	BC	__fp_mul_exp_done
	DECF	__fp_ex+1, F
__fp_mul_exp_done:

; sticky over the 21 discarded bits, then take the product >> 21
	MOVF	__fp_p+0, W
	IORWF	__fp_p+1, W
	MOVWF	__fp_t
	MOVF	__fp_p+2, W
	ANDLW	0x1F
	IORWF	__fp_t, F
	MOVFF	__fp_p+2, __fp_ma+0
	MOVFF	__fp_p+3, __fp_ma+1
	MOVFF	__fp_p+4, __fp_ma+2
	MOVFF	__fp_p+5, __fp_ma+3
	MOVLW	0x05
	MOVWF	__fp_c
__fp_mul_shr:
	BCF	STATUS, C, ACCESS
	RRCF	__fp_ma+3, F
	RRCF	__fp_ma+2, F
	RRCF	__fp_ma+1, F
	RRCF	__fp_ma+0, F
	DECFSZ	__fp_c, F
	BRA	__fp_mul_shr
	MOVF	__fp_t, F
	BZ	__fp_mul_round
	BSF	__fp_ma+0, 0, ACCESS
__fp_mul_round:
	BRA	__fp_round

__fp_mul_qnan:
	CLRF	__fp_a+0
	CLRF	__fp_a+1
	MOVLW	0xC0
	MOVWF	__fp_a+2
	MOVLW	0x7F
	MOVWF	__fp_a+3
	RETURN
__fp_mul_inf:
	CLRF	__fp_a+0
	CLRF	__fp_a+1
	MOVLW	0x80
	MOVWF	__fp_a+2
	MOVLW	0x7F
	MOVWF	__fp_a+3
	BRA	__fp_mul_apply_sign
__fp_mul_zero:
	CLRF	__fp_a+0
	CLRF	__fp_a+1
	CLRF	__fp_a+2
	CLRF	__fp_a+3
__fp_mul_apply_sign:
	BTFSC	__fp_sg, 0, ACCESS
	BSF	__fp_a+3, 7, ACCESS
	RETURN

; --- __fp_div: __fp_a = A / B ------------------------------------------------
__fp_div:
	MOVF	__fp_a+3, W
	XORWF	__fp_b+3, W
	ANDLW	0x80
	MOVWF	__fp_t
	CLRF	__fp_sg
	BTFSC	__fp_t, 7, ACCESS
	BSF	__fp_sg, 0, ACCESS

	MOVF	__fp_a+3, W
	ANDLW	0x7F
	XORLW	0x7F
	BNZ	__fp_div_a_notnan
	BTFSS	__fp_a+2, 7, ACCESS
	BRA	__fp_div_a_notnan
	MOVF	__fp_a+2, W
	ANDLW	0x7F
	IORWF	__fp_a+1, W
	IORWF	__fp_a+0, W
	BNZ	__fp_div_nan_hop
__fp_div_a_notnan:
	MOVF	__fp_b+3, W
	ANDLW	0x7F
	XORLW	0x7F
	BNZ	__fp_div_b_notnan
	BTFSS	__fp_b+2, 7, ACCESS
	BRA	__fp_div_b_notnan
	MOVF	__fp_b+2, W
	ANDLW	0x7F
	IORWF	__fp_b+1, W
	IORWF	__fp_b+0, W
	BNZ	__fp_div_nan_hop
	BRA	__fp_div_b_notnan
__fp_div_nan_hop:
	BRA	__fp_div_qnan

__fp_div_b_notnan:
	BRA	__fp_div_b_screened
__fp_div_far_qnan:
	BRA	__fp_div_qnan
__fp_div_far_inf:
	BRA	__fp_div_inf
__fp_div_far_zero:
	BRA	__fp_div_zero
__fp_div_b_screened:
	MOVF	__fp_a+3, W		; A infinite?
	ANDLW	0x7F
	XORLW	0x7F
	BNZ	__fp_div_a_finite
	BTFSS	__fp_a+2, 7, ACCESS
	BRA	__fp_div_a_finite
	MOVF	__fp_b+3, W		; inf / inf is undefined
	ANDLW	0x7F
	XORLW	0x7F
	BNZ	__fp_div_far_inf
	BTFSS	__fp_b+2, 7, ACCESS
	BRA	__fp_div_far_inf
	BRA	__fp_div_far_qnan
__fp_div_a_finite:
	MOVF	__fp_b+3, W		; B infinite -> signed zero
	ANDLW	0x7F
	XORLW	0x7F
	BNZ	__fp_div_b_finite
	BTFSC	__fp_b+2, 7, ACCESS
	BRA	__fp_div_far_zero
__fp_div_b_finite:
	MOVF	__fp_b+0, W		; B zero?
	IORWF	__fp_b+1, W
	IORWF	__fp_b+2, W
	MOVWF	__fp_t
	MOVF	__fp_b+3, W
	ANDLW	0x7F
	IORWF	__fp_t, W
	BNZ	__fp_div_a_zero
	MOVF	__fp_a+0, W		; 0 / 0 is undefined, x / 0 is infinity
	IORWF	__fp_a+1, W
	IORWF	__fp_a+2, W
	MOVWF	__fp_t
	MOVF	__fp_a+3, W
	ANDLW	0x7F
	IORWF	__fp_t, W
	BZ	__fp_div_far_qnan
	BRA	__fp_div_far_inf
__fp_div_a_zero:
	MOVF	__fp_a+0, W
	IORWF	__fp_a+1, W
	IORWF	__fp_a+2, W
	MOVWF	__fp_t
	MOVF	__fp_a+3, W
	ANDLW	0x7F
	IORWF	__fp_t, W
	BZ	__fp_div_far_zero

; unpack and normalize both operands to 24-bit significands
	MOVF	__fp_a+3, W
	ANDLW	0x7F
	MOVWF	__fp_t
	BCF	STATUS, C, ACCESS
	RLCF	__fp_t, F
	BTFSC	__fp_a+2, 7, ACCESS
	BSF	__fp_t, 0, ACCESS
	MOVFF	__fp_t, __fp_ex
	CLRF	__fp_ex+1
	MOVFF	__fp_a+0, __fp_ma+0
	MOVFF	__fp_a+1, __fp_ma+1
	MOVF	__fp_a+2, W
	ANDLW	0x7F
	MOVWF	__fp_ma+2
	CLRF	__fp_ma+3
	MOVF	__fp_ex+0, F
	BZ	__fp_div_a_norm
	BSF	__fp_ma+2, 7, ACCESS
	BRA	__fp_div_b_unpack
__fp_div_a_norm:
	MOVLW	0x01
	MOVWF	__fp_ex+0
__fp_div_a_shl:
	BTFSC	__fp_ma+2, 7, ACCESS
	BRA	__fp_div_b_unpack
	BCF	STATUS, C, ACCESS
	RLCF	__fp_ma+0, F
	RLCF	__fp_ma+1, F
	RLCF	__fp_ma+2, F
	MOVLW	0x01
	SUBWF	__fp_ex+0, F
	BC	__fp_div_a_shl
	DECF	__fp_ex+1, F
	BRA	__fp_div_a_shl

__fp_div_b_unpack:
	MOVF	__fp_b+3, W
	ANDLW	0x7F
	MOVWF	__fp_t
	BCF	STATUS, C, ACCESS
	RLCF	__fp_t, F
	BTFSC	__fp_b+2, 7, ACCESS
	BSF	__fp_t, 0, ACCESS
	MOVFF	__fp_t, __fp_eb
	CLRF	__fp_eb+1
	MOVFF	__fp_b+0, __fp_mb+0
	MOVFF	__fp_b+1, __fp_mb+1
	MOVF	__fp_b+2, W
	ANDLW	0x7F
	MOVWF	__fp_mb+2
	CLRF	__fp_mb+3
	MOVF	__fp_eb+0, F
	BZ	__fp_div_b_norm
	BSF	__fp_mb+2, 7, ACCESS
	BRA	__fp_div_setup
__fp_div_b_norm:
	MOVLW	0x01
	MOVWF	__fp_eb+0
__fp_div_b_shl:
	BTFSC	__fp_mb+2, 7, ACCESS
	BRA	__fp_div_setup
	BCF	STATUS, C, ACCESS
	RLCF	__fp_mb+0, F
	RLCF	__fp_mb+1, F
	RLCF	__fp_mb+2, F
	MOVLW	0x01
	SUBWF	__fp_eb+0, F
	BC	__fp_div_b_shl
	DECF	__fp_eb+1, F
	BRA	__fp_div_b_shl

; 27 restoring steps give floor(ma * 2^27 / mb): 24 significand bits plus the
; 3 guard bits __fp_round expects. __fp_ma holds the remainder, __fp_p the
; quotient.
__fp_div_setup:
	MOVF	__fp_eb+0, W		; exponent = ea - eb + 127
	SUBWF	__fp_ex+0, F
	MOVF	__fp_eb+1, W
	SUBWFB	__fp_ex+1, F
	MOVLW	0x7F
	ADDWF	__fp_ex+0, F
	BNC	__fp_div_prescale
	INCF	__fp_ex+1, F

; The restoring loop needs rem < mb on entry. Both significands sit in
; [2^23, 2^24), so ma < mb half the time: shift ma up once and pay for it in
; the exponent, which also pins the quotient into [2^26, 2^27).
__fp_div_prescale:
	MOVF	__fp_mb+0, W
	SUBWF	__fp_ma+0, W
	MOVF	__fp_mb+1, W
	SUBWFB	__fp_ma+1, W
	MOVF	__fp_mb+2, W
	SUBWFB	__fp_ma+2, W
	MOVF	__fp_mb+3, W
	SUBWFB	__fp_ma+3, W
	BC	__fp_div_first
	BCF	STATUS, C, ACCESS
	RLCF	__fp_ma+0, F
	RLCF	__fp_ma+1, F
	RLCF	__fp_ma+2, F
	RLCF	__fp_ma+3, F
	MOVLW	0x01
	SUBWF	__fp_ex+0, F
	BC	__fp_div_first
	DECF	__fp_ex+1, F

__fp_div_first:
	MOVF	__fp_mb+0, W		; rem = ma - mb, quotient starts at 1
	SUBWF	__fp_ma+0, F
	MOVF	__fp_mb+1, W
	SUBWFB	__fp_ma+1, F
	MOVF	__fp_mb+2, W
	SUBWFB	__fp_ma+2, F
	MOVF	__fp_mb+3, W
	SUBWFB	__fp_ma+3, F
	CLRF	__fp_p+1
	CLRF	__fp_p+2
	CLRF	__fp_p+3
	MOVLW	0x01
	MOVWF	__fp_p+0
	MOVLW	0x1A
	MOVWF	__fp_c
__fp_div_loop:
	BCF	STATUS, C, ACCESS
	RLCF	__fp_ma+0, F
	RLCF	__fp_ma+1, F
	RLCF	__fp_ma+2, F
	RLCF	__fp_ma+3, F
	BCF	STATUS, C, ACCESS
	RLCF	__fp_p+0, F
	RLCF	__fp_p+1, F
	RLCF	__fp_p+2, F
	RLCF	__fp_p+3, F
	MOVF	__fp_mb+0, W
	SUBWF	__fp_ma+0, W
	MOVF	__fp_mb+1, W
	SUBWFB	__fp_ma+1, W
	MOVF	__fp_mb+2, W
	SUBWFB	__fp_ma+2, W
	MOVF	__fp_mb+3, W
	SUBWFB	__fp_ma+3, W
	BNC	__fp_div_next
	MOVF	__fp_mb+0, W
	SUBWF	__fp_ma+0, F
	MOVF	__fp_mb+1, W
	SUBWFB	__fp_ma+1, F
	MOVF	__fp_mb+2, W
	SUBWFB	__fp_ma+2, F
	MOVF	__fp_mb+3, W
	SUBWFB	__fp_ma+3, F
	BSF	__fp_p+0, 0, ACCESS
__fp_div_next:
	DECFSZ	__fp_c, F
	BRA	__fp_div_loop

	MOVF	__fp_ma+0, W		; a non-zero remainder is the sticky bit
	IORWF	__fp_ma+1, W
	IORWF	__fp_ma+2, W
	IORWF	__fp_ma+3, W
	MOVWF	__fp_t
	MOVFF	__fp_p+0, __fp_ma+0
	MOVFF	__fp_p+1, __fp_ma+1
	MOVFF	__fp_p+2, __fp_ma+2
	MOVFF	__fp_p+3, __fp_ma+3
	MOVF	__fp_t, F
	BZ	__fp_div_round
	BSF	__fp_ma+0, 0, ACCESS
__fp_div_round:
	BRA	__fp_round

__fp_div_qnan:
	CLRF	__fp_a+0
	CLRF	__fp_a+1
	MOVLW	0xC0
	MOVWF	__fp_a+2
	MOVLW	0x7F
	MOVWF	__fp_a+3
	RETURN
__fp_div_inf:
	CLRF	__fp_a+0
	CLRF	__fp_a+1
	MOVLW	0x80
	MOVWF	__fp_a+2
	MOVLW	0x7F
	MOVWF	__fp_a+3
	BRA	__fp_div_apply_sign
__fp_div_zero:
	CLRF	__fp_a+0
	CLRF	__fp_a+1
	CLRF	__fp_a+2
	CLRF	__fp_a+3
__fp_div_apply_sign:
	BTFSC	__fp_sg, 0, ACCESS
	BSF	__fp_a+3, 7, ACCESS
	RETURN

; --- __fp_float: __fp_a = (float) int32 in __fp_a ----------------------------
__fp_float:
	MOVF	__fp_a+0, W
	IORWF	__fp_a+1, W
	IORWF	__fp_a+2, W
	IORWF	__fp_a+3, W
	BNZ	__fp_float_go
	RETURN				; +0 already sits in __fp_a
__fp_float_go:
	CLRF	__fp_sg
	BTFSS	__fp_a+3, 7, ACCESS
	BRA	__fp_float_mag
	BSF	__fp_sg, 0, ACCESS
	COMF	__fp_a+0, F
	COMF	__fp_a+1, F
	COMF	__fp_a+2, F
	COMF	__fp_a+3, F
	INCF	__fp_a+0, F
	BTFSC	STATUS, C, ACCESS
	INCF	__fp_a+1, F
	BTFSC	STATUS, C, ACCESS
	INCF	__fp_a+2, F
	BTFSC	STATUS, C, ACCESS
	INCF	__fp_a+3, F
__fp_float_mag:
	MOVFF	__fp_a+0, __fp_ma+0
	MOVFF	__fp_a+1, __fp_ma+1
	MOVFF	__fp_a+2, __fp_ma+2
	MOVFF	__fp_a+3, __fp_ma+3
	MOVLW	0x99			; magnitude at exponent 153 (no guard shift)
	MOVWF	__fp_ex+0
	CLRF	__fp_ex+1
	BRA	__fp_round

; --- __fp_fix: __fp_a = (int32) float in __fp_a, truncating toward zero ------
__fp_fix:
	CLRF	__fp_sg
	BTFSC	__fp_a+3, 7, ACCESS
	BSF	__fp_sg, 0, ACCESS
	MOVF	__fp_a+3, W
	ANDLW	0x7F
	MOVWF	__fp_t
	BCF	STATUS, C, ACCESS
	RLCF	__fp_t, F
	BTFSC	__fp_a+2, 7, ACCESS
	BSF	__fp_t, 0, ACCESS	; __fp_t = biased exponent

	MOVLW	0x9E			; exponent >= 158 leaves the int32 range
	SUBWF	__fp_t, W
	BC	__fp_fix_sat
	MOVLW	0x7F			; exponent < 127: magnitude below 1
	SUBWF	__fp_t, W
	BNC	__fp_fix_zero

	MOVFF	__fp_a+0, __fp_ma+0
	MOVFF	__fp_a+1, __fp_ma+1
	MOVF	__fp_a+2, W
	ANDLW	0x7F
	MOVWF	__fp_ma+2
	CLRF	__fp_ma+3
	BSF	__fp_ma+2, 7, ACCESS

	MOVLW	0x96			; shift = exponent - 150
	SUBWF	__fp_t, W
	MOVWF	__fp_c
	BC	__fp_fix_left
	COMF	__fp_c, F		; negative: shift right by its magnitude
	INCF	__fp_c, F
__fp_fix_right:
	MOVF	__fp_c, F
	BZ	__fp_fix_store
	BCF	STATUS, C, ACCESS
	RRCF	__fp_ma+3, F
	RRCF	__fp_ma+2, F
	RRCF	__fp_ma+1, F
	RRCF	__fp_ma+0, F
	DECF	__fp_c, F
	BRA	__fp_fix_right
__fp_fix_left:
	MOVF	__fp_c, F
	BZ	__fp_fix_store
	BCF	STATUS, C, ACCESS
	RLCF	__fp_ma+0, F
	RLCF	__fp_ma+1, F
	RLCF	__fp_ma+2, F
	RLCF	__fp_ma+3, F
	DECF	__fp_c, F
	BRA	__fp_fix_left

__fp_fix_store:
	MOVFF	__fp_ma+0, __fp_a+0
	MOVFF	__fp_ma+1, __fp_a+1
	MOVFF	__fp_ma+2, __fp_a+2
	MOVFF	__fp_ma+3, __fp_a+3
	BTFSS	__fp_sg, 0, ACCESS
	RETURN
	COMF	__fp_a+0, F
	COMF	__fp_a+1, F
	COMF	__fp_a+2, F
	COMF	__fp_a+3, F
	INCF	__fp_a+0, F
	BTFSC	STATUS, C, ACCESS
	INCF	__fp_a+1, F
	BTFSC	STATUS, C, ACCESS
	INCF	__fp_a+2, F
	BTFSC	STATUS, C, ACCESS
	INCF	__fp_a+3, F
	RETURN
__fp_fix_zero:
	CLRF	__fp_a+0
	CLRF	__fp_a+1
	CLRF	__fp_a+2
	CLRF	__fp_a+3
	RETURN
__fp_fix_sat:
	SETF	__fp_a+0
	SETF	__fp_a+1
	SETF	__fp_a+2
	MOVLW	0x7F
	MOVWF	__fp_a+3
	BTFSS	__fp_sg, 0, ACCESS
	RETURN
	CLRF	__fp_a+0		; negative saturation is -2^31
	CLRF	__fp_a+1
	CLRF	__fp_a+2
	MOVLW	0x80
	MOVWF	__fp_a+3
	RETURN

; --- __fp_floor: __fp_a = floor(__fp_a) --------------------------------------
__fp_floor:
	MOVF	__fp_a+3, W
	ANDLW	0x7F
	MOVWF	__fp_t
	BCF	STATUS, C, ACCESS
	RLCF	__fp_t, F
	BTFSC	__fp_a+2, 7, ACCESS
	BSF	__fp_t, 0, ACCESS	; __fp_t = biased exponent

	MOVF	__fp_t, W
	XORLW	0xFF
	BZ	__fp_floor_done		; inf or NaN
	MOVLW	0x96
	SUBWF	__fp_t, W
	BC	__fp_floor_done		; exponent >= 150: already integral
	MOVLW	0x7F
	SUBWF	__fp_t, W
	BC	__fp_floor_split

	MOVF	__fp_a+0, W		; |x| < 1
	IORWF	__fp_a+1, W
	IORWF	__fp_a+2, W
	MOVWF	__fp_c
	MOVF	__fp_a+3, W
	ANDLW	0x7F
	IORWF	__fp_c, W
	BZ	__fp_floor_done		; +/-0 is its own floor
	BTFSS	__fp_a+3, 7, ACCESS
	BRA	__fp_floor_zero
	CLRF	__fp_a+0		; negative fraction floors to -1.0
	CLRF	__fp_a+1
	MOVLW	0x80
	MOVWF	__fp_a+2
	MOVLW	0xBF
	MOVWF	__fp_a+3
__fp_floor_done:
	RETURN
__fp_floor_zero:
	CLRF	__fp_a+0
	CLRF	__fp_a+1
	CLRF	__fp_a+2
	CLRF	__fp_a+3
	RETURN

__fp_floor_split:
	CLRF	__fp_sg
	BTFSC	__fp_a+3, 7, ACCESS
	BSF	__fp_sg, 0, ACCESS
	MOVFF	__fp_t, __fp_ex
	CLRF	__fp_ex+1
	MOVFF	__fp_a+0, __fp_ma+0
	MOVFF	__fp_a+1, __fp_ma+1
	MOVF	__fp_a+2, W
	ANDLW	0x7F
	MOVWF	__fp_ma+2
	CLRF	__fp_ma+3
	BSF	__fp_ma+2, 7, ACCESS

	MOVLW	0x96			; count = 150 - exponent, the bits below the point
	MOVWF	__fp_c
	MOVF	__fp_t, W
	SUBWF	__fp_c, F
	MOVFF	__fp_c, __fp_st		; remember it for the shift back
	CLRF	__fp_t			; fraction seen
__fp_floor_drop:
	MOVF	__fp_c, F
	BZ	__fp_floor_adjust
	BCF	STATUS, C, ACCESS
	RRCF	__fp_ma+3, F
	RRCF	__fp_ma+2, F
	RRCF	__fp_ma+1, F
	RRCF	__fp_ma+0, F
	BTFSC	STATUS, C, ACCESS
	BSF	__fp_t, 0, ACCESS
	DECF	__fp_c, F
	BRA	__fp_floor_drop

__fp_floor_adjust:
	BTFSS	__fp_sg, 0, ACCESS
	BRA	__fp_floor_back		; positive: truncation already floors
	MOVF	__fp_t, F
	BZ	__fp_floor_back		; nothing was dropped
	INCF	__fp_ma+0, F
	BNC	__fp_floor_back
	INCF	__fp_ma+1, F
	BNC	__fp_floor_back
	INCF	__fp_ma+2, F
	BNC	__fp_floor_back
	INCF	__fp_ma+3, F

__fp_floor_back:
	MOVFF	__fp_st, __fp_c
__fp_floor_up:
	MOVF	__fp_c, F
	BZ	__fp_floor_scale
	BCF	STATUS, C, ACCESS
	RLCF	__fp_ma+0, F
	RLCF	__fp_ma+1, F
	RLCF	__fp_ma+2, F
	RLCF	__fp_ma+3, F
	DECF	__fp_c, F
	BRA	__fp_floor_up

__fp_floor_scale:
	MOVLW	0x03			; hand __fp_round the 3 guard bits it expects
	MOVWF	__fp_c
__fp_floor_guard:
	BCF	STATUS, C, ACCESS
	RLCF	__fp_ma+0, F
	RLCF	__fp_ma+1, F
	RLCF	__fp_ma+2, F
	RLCF	__fp_ma+3, F
	DECFSZ	__fp_c, F
	BRA	__fp_floor_guard
	BRA	__fp_round

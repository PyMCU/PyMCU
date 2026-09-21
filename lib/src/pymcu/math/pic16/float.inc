; -----------------------------------------------------------------------------
; PyMCU soft-float for PIC14/PIC14E (PIC16) -- IEEE-754 binary32
; Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
;
; SPDX-License-Identifier: MIT
; -----------------------------------------------------------------------------
;
; Calling convention
;   __fp_a  4 bytes, little-endian: operand A, and the result of every
;           arithmetic routine.
;   __fp_b  4 bytes, little-endian: operand B.
;   __fp_s  29 bytes of scratch, owned by these routines.
;   The cells are allocated in bank 0 by PIC14CodeGen, so an interrupt
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
	BANKSEL	__fp_a
	MOVF	__fp_a+3, W
	ANDLW	0x7F
	XORLW	0x7F
	MOVLW	HIGH(__fp_cmp_a_ok)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_cmp_a_ok
	MOVLW	HIGH(__fp_cmp_a_ok)
	MOVWF	PCLATH
	BTFSS	__fp_a+2, 7
	GOTO	__fp_cmp_a_ok
	MOVF	__fp_a+2, W
	ANDLW	0x7F
	IORWF	__fp_a+1, W
	IORWF	__fp_a+0, W
	MOVLW	HIGH(__fp_cmp_unordered)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_cmp_unordered
__fp_cmp_a_ok:
	MOVF	__fp_b+3, W
	ANDLW	0x7F
	XORLW	0x7F
	MOVLW	HIGH(__fp_cmp_b_ok)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_cmp_b_ok
	MOVLW	HIGH(__fp_cmp_b_ok)
	MOVWF	PCLATH
	BTFSS	__fp_b+2, 7
	GOTO	__fp_cmp_b_ok
	MOVF	__fp_b+2, W
	ANDLW	0x7F
	IORWF	__fp_b+1, W
	IORWF	__fp_b+0, W
	MOVLW	HIGH(__fp_cmp_unordered)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_cmp_unordered

; +0.0 and -0.0 compare equal, but the total-order key below separates them.
__fp_cmp_b_ok:
	MOVF	__fp_a+0, W
	IORWF	__fp_a+1, W
	IORWF	__fp_a+2, W
	MOVWF	__fp_s
	MOVF	__fp_a+3, W
	ANDLW	0x7F
	IORWF	__fp_s, W
	MOVLW	HIGH(__fp_cmp_keys)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_cmp_keys
	MOVF	__fp_b+0, W
	IORWF	__fp_b+1, W
	IORWF	__fp_b+2, W
	MOVWF	__fp_s
	MOVF	__fp_b+3, W
	ANDLW	0x7F
	IORWF	__fp_s, W
	MOVLW	HIGH(__fp_cmp_equal)
	MOVWF	PCLATH
	BTFSC	STATUS, 2
	GOTO	__fp_cmp_equal

; Total order: a negative float maps to its one's complement, a positive one
; to itself with the top bit set. Comparing the keys as unsigned 32-bit
; integers then yields the IEEE ordering.
__fp_cmp_keys:
	MOVLW	HIGH(__fp_cmp_a_pos)
	MOVWF	PCLATH
	BTFSS	__fp_a+3, 7
	GOTO	__fp_cmp_a_pos
	COMF	__fp_a+0, F
	COMF	__fp_a+1, F
	COMF	__fp_a+2, F
	COMF	__fp_a+3, F
	MOVLW	HIGH(__fp_cmp_key_b)
	MOVWF	PCLATH
	GOTO	__fp_cmp_key_b
__fp_cmp_a_pos:
	BSF	__fp_a+3, 7
__fp_cmp_key_b:
	MOVLW	HIGH(__fp_cmp_b_pos)
	MOVWF	PCLATH
	BTFSS	__fp_b+3, 7
	GOTO	__fp_cmp_b_pos
	COMF	__fp_b+0, F
	COMF	__fp_b+1, F
	COMF	__fp_b+2, F
	COMF	__fp_b+3, F
	MOVLW	HIGH(__fp_cmp_sub)
	MOVWF	PCLATH
	GOTO	__fp_cmp_sub
__fp_cmp_b_pos:
	BSF	__fp_b+3, 7

; A - B across four bytes. IORWF keeps the carry chain intact (it writes Z and
; N only), so the final carry is the full-width borrow and __fp_s accumulates
; the difference bytes for the equality test.
__fp_cmp_sub:
	MOVF	__fp_b+0, W
	SUBWF	__fp_a+0, W
	MOVWF	__fp_s
	MOVF	__fp_b+1, W
	MOVWF	__fp_w
	MOVLW	0x01
	IORWF	__fp_w, W
	BTFSS	STATUS, 0
	INCF	__fp_w, F
	BCF	__fp_wf, 0
	BTFSC	STATUS, 2
	BSF	__fp_wf, 0
	MOVF	__fp_w, W
	SUBWF	__fp_a+1, W
	BTFSC	__fp_wf, 0
	BCF	STATUS, 0
	IORWF	__fp_s, F
	MOVF	__fp_b+2, W
	MOVWF	__fp_w
	MOVLW	0x01
	IORWF	__fp_w, W
	BTFSS	STATUS, 0
	INCF	__fp_w, F
	BCF	__fp_wf, 0
	BTFSC	STATUS, 2
	BSF	__fp_wf, 0
	MOVF	__fp_w, W
	SUBWF	__fp_a+2, W
	BTFSC	__fp_wf, 0
	BCF	STATUS, 0
	IORWF	__fp_s, F
	MOVF	__fp_b+3, W
	MOVWF	__fp_w
	MOVLW	0x01
	IORWF	__fp_w, W
	BTFSS	STATUS, 0
	INCF	__fp_w, F
	BCF	__fp_wf, 0
	BTFSC	STATUS, 2
	BSF	__fp_wf, 0
	MOVF	__fp_w, W
	SUBWF	__fp_a+3, W
	BTFSC	__fp_wf, 0
	BCF	STATUS, 0
	IORWF	__fp_s, F
	MOVLW	HIGH(__fp_cmp_less)
	MOVWF	PCLATH
	BTFSS	STATUS, 0
	GOTO	__fp_cmp_less
	MOVF	__fp_s, F
	MOVLW	HIGH(__fp_cmp_equal)
	MOVWF	PCLATH
	BTFSC	STATUS, 2
	GOTO	__fp_cmp_equal
	RETLW	0x01
__fp_cmp_less:
	RETLW	0xFF
__fp_cmp_equal:
	RETLW	0x00
__fp_cmp_unordered:
	RETLW	0x01

; --- scratch layout ----------------------------------------------------------
; __fp_s is 16 bytes owned by these routines.
__fp_ma	EQU	__fp_s+0x00	; 4 bytes: working mantissa (28 significant bits max)
__fp_ex	EQU	__fp_s+0x04	; 2 bytes: working exponent, signed
__fp_sg	EQU	__fp_s+0x06	; result sign in bit 0
__fp_t	EQU	__fp_s+0x07	; byte temp
__fp_mb	EQU	__fp_s+0x08	; 4 bytes: second mantissa
__fp_eb	EQU	__fp_s+0x0C	; 2 bytes: second exponent, signed
__fp_c	EQU	__fp_s+0x0E	; loop counter
__fp_st	EQU	__fp_s+0x0F	; sticky accumulator
__fp_p	EQU	__fp_s+0x10	; 6 bytes: 48-bit product
__fp_ua	EQU	__fp_s+0x16	; mul8 multiplicand
__fp_ub	EQU	__fp_s+0x17	; mul8 multiplier (consumed)
__fp_uc	EQU	__fp_s+0x18	; mul8 bit counter
__fp_pl	EQU	__fp_s+0x19	; mul8 product low
__fp_ph	EQU	__fp_s+0x1A	; mul8 product high
__fp_w	EQU	__fp_s+0x1B	; borrow/carry operand scratch
__fp_wf	EQU	__fp_s+0x1C	; bit 0: operand-adjust wrapped

; --- __fp_round: pack (__fp_sg, __fp_ex, __fp_ma) into __fp_a ----------------
; __fp_ma carries the significand with 3 guard bits below it; the caller must
; have brought it under 2^32. Round to nearest, ties to even; overflow gives a
; signed infinity, underflow a subnormal or a signed zero.
__fp_round:
__fp_rnd_down:
	MOVF	__fp_ma+3, W
	ANDLW	0xF8
	MOVLW	HIGH(__fp_rnd_up)
	MOVWF	PCLATH
	BTFSC	STATUS, 2
	GOTO	__fp_rnd_up
	MOVF	__fp_ma+0, W
	ANDLW	0x01
	MOVWF	__fp_t
	BCF	STATUS, C
	RRF	__fp_ma+3, F
	RRF	__fp_ma+2, F
	RRF	__fp_ma+1, F
	RRF	__fp_ma+0, F
	MOVF	__fp_t, W
	IORWF	__fp_ma+0, F
	INCF	__fp_ex+0, F
	MOVF	__fp_ex+0, F
	MOVLW	HIGH(__fp_rnd_down)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_rnd_down
	INCF	__fp_ex+1, F
	MOVLW	HIGH(__fp_rnd_down)
	MOVWF	PCLATH
	GOTO	__fp_rnd_down

__fp_rnd_up:
	MOVF	__fp_ma+3, W
	ANDLW	0xFC
	MOVLW	HIGH(__fp_rnd_limits)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_rnd_limits		; already >= 2^26
	MOVLW	HIGH(__fp_rnd_limits)
	MOVWF	PCLATH
	BTFSC	__fp_ex+1, 7
	GOTO	__fp_rnd_limits		; exponent already negative
	MOVF	__fp_ex+1, F
	MOVLW	HIGH(__fp_rnd_shift_up)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_rnd_shift_up
	MOVF	__fp_ex+0, W
	SUBLW	0x01
	MOVLW	HIGH(__fp_rnd_limits)
	MOVWF	PCLATH
	BTFSC	STATUS, 0
	GOTO	__fp_rnd_limits		; ex <= 1: cannot borrow from the exponent
__fp_rnd_shift_up:
	BCF	STATUS, C
	RLF	__fp_ma+0, F
	RLF	__fp_ma+1, F
	RLF	__fp_ma+2, F
	RLF	__fp_ma+3, F
	MOVLW	0x01
	SUBWF	__fp_ex+0, F
	MOVLW	HIGH(__fp_rnd_up)
	MOVWF	PCLATH
	BTFSC	STATUS, 0
	GOTO	__fp_rnd_up
	DECF	__fp_ex+1, F
	MOVLW	HIGH(__fp_rnd_up)
	MOVWF	PCLATH
	GOTO	__fp_rnd_up

; Both limits are checked here: an already-normalized mantissa must still be
; able to reach the subnormal path, which an overflow-only test skipped.
__fp_rnd_limits:
	MOVLW	HIGH(__fp_rnd_under)
	MOVWF	PCLATH
	BTFSC	__fp_ex+1, 7
	GOTO	__fp_rnd_under		; exponent negative
	MOVF	__fp_ex+1, F
	MOVLW	HIGH(__fp_rnd_inf)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_rnd_inf		; exponent >= 256
	MOVF	__fp_ex+0, W
	SUBLW	0xFE
	MOVLW	HIGH(__fp_rnd_inf)
	MOVWF	PCLATH
	BTFSS	STATUS, 0
	GOTO	__fp_rnd_inf		; exponent > 254
	MOVF	__fp_ex+0, F
	MOVLW	HIGH(__fp_rnd_under)
	MOVWF	PCLATH
	BTFSC	STATUS, 2
	GOTO	__fp_rnd_under		; exponent 0
	MOVLW	HIGH(__fp_rnd_pack)
	MOVWF	PCLATH
	GOTO	__fp_rnd_pack

; ex <= 0: shift the mantissa down into the subnormal range, keeping a sticky.
__fp_rnd_under:
	COMF	__fp_ex+0, W
	ADDLW	0x02
	MOVWF	__fp_c		; shift = 1 - ex, low byte
	COMF	__fp_ex+1, W
	BTFSC	STATUS, C
	ADDLW	0x01
	MOVWF	__fp_t		; shift, high byte
	MOVLW	HIGH(__fp_rnd_zero)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_rnd_zero		; shift >= 256
	MOVLW	0x1C
	SUBWF	__fp_c, W
	MOVLW	HIGH(__fp_rnd_zero)
	MOVWF	PCLATH
	BTFSC	STATUS, 0
	GOTO	__fp_rnd_zero		; shift > 27: nothing survives
	CLRF	__fp_ex+0
	CLRF	__fp_ex+1
__fp_rnd_under_loop:
	MOVF	__fp_c, F
	MOVLW	HIGH(__fp_rnd_pack)
	MOVWF	PCLATH
	BTFSC	STATUS, 2
	GOTO	__fp_rnd_pack
	MOVF	__fp_ma+0, W
	ANDLW	0x01
	MOVWF	__fp_t
	BCF	STATUS, C
	RRF	__fp_ma+3, F
	RRF	__fp_ma+2, F
	RRF	__fp_ma+1, F
	RRF	__fp_ma+0, F
	MOVF	__fp_t, W
	IORWF	__fp_ma+0, F
	DECF	__fp_c, F
	MOVLW	HIGH(__fp_rnd_under_loop)
	MOVWF	PCLATH
	GOTO	__fp_rnd_under_loop

__fp_rnd_inf:
	CLRF	__fp_a+0
	CLRF	__fp_a+1
	MOVLW	0x80
	MOVWF	__fp_a+2
	MOVLW	0x7F
	MOVWF	__fp_a+3
	MOVLW	HIGH(__fp_rnd_sign)
	MOVWF	PCLATH
	GOTO	__fp_rnd_sign
__fp_rnd_zero:
	CLRF	__fp_a+0
	CLRF	__fp_a+1
	CLRF	__fp_a+2
	CLRF	__fp_a+3
	MOVLW	HIGH(__fp_rnd_sign)
	MOVWF	PCLATH
	GOTO	__fp_rnd_sign

; round to nearest, ties to even, then drop the 3 guard bits
__fp_rnd_pack:
	MOVF	__fp_ma+0, W
	ANDLW	0x07
	MOVWF	__fp_t
	BCF	STATUS, C
	RRF	__fp_ma+3, F
	RRF	__fp_ma+2, F
	RRF	__fp_ma+1, F
	RRF	__fp_ma+0, F
	BCF	STATUS, C
	RRF	__fp_ma+3, F
	RRF	__fp_ma+2, F
	RRF	__fp_ma+1, F
	RRF	__fp_ma+0, F
	BCF	STATUS, C
	RRF	__fp_ma+3, F
	RRF	__fp_ma+2, F
	RRF	__fp_ma+1, F
	RRF	__fp_ma+0, F
	MOVLW	0x05
	SUBWF	__fp_t, W
	MOVLW	HIGH(__fp_rnd_inc)
	MOVWF	PCLATH
	BTFSC	STATUS, 0
	GOTO	__fp_rnd_inc		; guard > 4
	MOVF	__fp_t, W
	XORLW	0x04
	MOVLW	HIGH(__fp_rnd_store)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_rnd_store		; guard < 4
	MOVLW	HIGH(__fp_rnd_store)
	MOVWF	PCLATH
	BTFSS	__fp_ma+0, 0
	GOTO	__fp_rnd_store		; tie and even: stay
__fp_rnd_inc:
	INCF	__fp_ma+0, F
	MOVF	__fp_ma+0, F
	MOVLW	HIGH(__fp_rnd_carry_done)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_rnd_carry_done
	INCF	__fp_ma+1, F
	MOVF	__fp_ma+1, F
	MOVLW	HIGH(__fp_rnd_carry_done)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_rnd_carry_done
	INCF	__fp_ma+2, F
	MOVF	__fp_ma+2, F
	MOVLW	HIGH(__fp_rnd_carry_done)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_rnd_carry_done
	INCF	__fp_ma+3, F
__fp_rnd_carry_done:
	MOVF	__fp_ma+3, F
	MOVLW	HIGH(__fp_rnd_store)
	MOVWF	PCLATH
	BTFSC	STATUS, 2
	GOTO	__fp_rnd_store
__fp_rnd_carry_out:
	BCF	STATUS, C
	RRF	__fp_ma+3, F
	RRF	__fp_ma+2, F
	RRF	__fp_ma+1, F
	RRF	__fp_ma+0, F
	INCF	__fp_ex+0, F
	MOVF	__fp_ex+0, F
	MOVLW	HIGH(__fp_rnd_recheck)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_rnd_recheck
	INCF	__fp_ex+1, F
__fp_rnd_recheck:
	MOVF	__fp_ex+1, F
	MOVLW	HIGH(__fp_rnd_inf)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_rnd_inf
	MOVF	__fp_ex+0, W
	SUBLW	0xFE
	MOVLW	HIGH(__fp_rnd_inf)
	MOVWF	PCLATH
	BTFSS	STATUS, 0
	GOTO	__fp_rnd_inf

; assemble sign:exponent:mantissa
__fp_rnd_store:
	MOVF	__fp_ma+0, W
	MOVWF	__fp_a+0
	MOVF	__fp_ma+1, W
	MOVWF	__fp_a+1
	MOVF	__fp_ma+2, W
	ANDLW	0x7F
	MOVWF	__fp_a+2
	CLRF	__fp_a+3
	MOVLW	HIGH(__fp_rnd_sign)
	MOVWF	PCLATH
	BTFSS	__fp_ma+2, 7
	GOTO	__fp_rnd_sign		; no implicit bit: subnormal, exponent 0
	BCF	STATUS, C
	RRF	__fp_ex+0, W		; W = exp >> 1, C = exp bit 0
	MOVWF	__fp_a+3
	BTFSC	STATUS, C
	BSF	__fp_a+2, 7
__fp_rnd_sign:
	BTFSC	__fp_sg, 0
	BSF	__fp_a+3, 7
	RETURN	

; --- __fp_sub / __fp_add: __fp_a = A +/- B -----------------------------------
; Operands in __fp_a and __fp_b; result in __fp_a. NaN propagates as a quiet
; NaN, inf - inf is a NaN, and a cancelling subtraction returns +0.
__fp_sub:
	BANKSEL	__fp_a
	MOVLW	0x80
	XORWF	__fp_b+3, F
__fp_add:
	BANKSEL	__fp_a
	MOVF	__fp_a+3, W
	ANDLW	0x7F
	XORLW	0x7F
	MOVLW	HIGH(__fp_add_a_finite)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_add_a_finite
	MOVLW	HIGH(__fp_add_a_finite)
	MOVWF	PCLATH
	BTFSS	__fp_a+2, 7
	GOTO	__fp_add_a_finite
	MOVF	__fp_a+2, W
	ANDLW	0x7F
	IORWF	__fp_a+1, W
	IORWF	__fp_a+0, W
	MOVLW	HIGH(__fp_add_qnan)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_add_qnan		; A is NaN
	MOVLW	HIGH(__fp_add_a_inf)
	MOVWF	PCLATH
	GOTO	__fp_add_a_inf
__fp_add_a_finite:
	MOVF	__fp_b+3, W
	ANDLW	0x7F
	XORLW	0x7F
	MOVLW	HIGH(__fp_add_unpack)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_add_unpack
	MOVLW	HIGH(__fp_add_unpack)
	MOVWF	PCLATH
	BTFSS	__fp_b+2, 7
	GOTO	__fp_add_unpack
	MOVF	__fp_b+2, W
	ANDLW	0x7F
	IORWF	__fp_b+1, W
	IORWF	__fp_b+0, W
	MOVLW	HIGH(__fp_add_qnan)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_add_qnan		; B is NaN
	MOVF	__fp_b+0, W
	MOVWF	__fp_a+0		; B is infinite, A is finite
	MOVF	__fp_b+1, W
	MOVWF	__fp_a+1
	MOVF	__fp_b+2, W
	MOVWF	__fp_a+2
	MOVF	__fp_b+3, W
	MOVWF	__fp_a+3
	RETURN	

; A is infinite: only inf + (-inf) is undefined; otherwise A already is the sum.
__fp_add_a_inf:
	MOVF	__fp_b+3, W
	ANDLW	0x7F
	XORLW	0x7F
	MOVLW	HIGH(__fp_add_keep_a)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_add_keep_a
	MOVLW	HIGH(__fp_add_keep_a)
	MOVWF	PCLATH
	BTFSS	__fp_b+2, 7
	GOTO	__fp_add_keep_a
	MOVF	__fp_b+2, W
	ANDLW	0x7F
	IORWF	__fp_b+1, W
	IORWF	__fp_b+0, W
	MOVLW	HIGH(__fp_add_qnan)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_add_qnan		; B is NaN
	MOVF	__fp_a+3, W
	XORWF	__fp_b+3, W
	ANDLW	0x80
	MOVLW	HIGH(__fp_add_qnan)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_add_qnan		; inf + (-inf)
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
	BTFSC	__fp_a+3, 7
	BSF	__fp_sg, 0
	MOVF	__fp_a+3, W
	ANDLW	0x7F
	MOVWF	__fp_t
	BCF	STATUS, C
	RLF	__fp_t, F
	BTFSC	__fp_a+2, 7
	BSF	__fp_t, 0
	MOVF	__fp_t, W
	MOVWF	__fp_ex
	CLRF	__fp_ex+1
	MOVF	__fp_a+0, W
	MOVWF	__fp_ma+0
	MOVF	__fp_a+1, W
	MOVWF	__fp_ma+1
	MOVF	__fp_a+2, W
	ANDLW	0x7F
	MOVWF	__fp_ma+2
	CLRF	__fp_ma+3
	MOVF	__fp_ex+0, F
	MOVLW	HIGH(__fp_add_a_sub)
	MOVWF	PCLATH
	BTFSC	STATUS, 2
	GOTO	__fp_add_a_sub
	BSF	__fp_ma+2, 7		; implicit bit
	MOVLW	HIGH(__fp_add_a_scale)
	MOVWF	PCLATH
	GOTO	__fp_add_a_scale
__fp_add_a_sub:
	MOVLW	0x01
	MOVWF	__fp_ex+0		; subnormals share exponent 1
__fp_add_a_scale:
	MOVLW	0x03
	MOVWF	__fp_c
__fp_add_a_shl:
	BCF	STATUS, C
	RLF	__fp_ma+0, F
	RLF	__fp_ma+1, F
	RLF	__fp_ma+2, F
	RLF	__fp_ma+3, F
	MOVLW	HIGH(__fp_add_a_shl)
	MOVWF	PCLATH
	DECFSZ	__fp_c, F
	GOTO	__fp_add_a_shl

; unpack B -> __fp_st / __fp_eb / __fp_mb
	CLRF	__fp_st
	BTFSC	__fp_b+3, 7
	BSF	__fp_st, 0
	MOVF	__fp_b+3, W
	ANDLW	0x7F
	MOVWF	__fp_t
	BCF	STATUS, C
	RLF	__fp_t, F
	BTFSC	__fp_b+2, 7
	BSF	__fp_t, 0
	MOVF	__fp_t, W
	MOVWF	__fp_eb
	CLRF	__fp_eb+1
	MOVF	__fp_b+0, W
	MOVWF	__fp_mb+0
	MOVF	__fp_b+1, W
	MOVWF	__fp_mb+1
	MOVF	__fp_b+2, W
	ANDLW	0x7F
	MOVWF	__fp_mb+2
	CLRF	__fp_mb+3
	MOVF	__fp_eb+0, F
	MOVLW	HIGH(__fp_add_b_sub)
	MOVWF	PCLATH
	BTFSC	STATUS, 2
	GOTO	__fp_add_b_sub
	BSF	__fp_mb+2, 7
	MOVLW	HIGH(__fp_add_b_scale)
	MOVWF	PCLATH
	GOTO	__fp_add_b_scale
__fp_add_b_sub:
	MOVLW	0x01
	MOVWF	__fp_eb+0
__fp_add_b_scale:
	MOVLW	0x03
	MOVWF	__fp_c
__fp_add_b_shl:
	BCF	STATUS, C
	RLF	__fp_mb+0, F
	RLF	__fp_mb+1, F
	RLF	__fp_mb+2, F
	RLF	__fp_mb+3, F
	MOVLW	HIGH(__fp_add_b_shl)
	MOVWF	PCLATH
	DECFSZ	__fp_c, F
	GOTO	__fp_add_b_shl

; keep the larger magnitude in A so the alignment shift is never negative
	MOVF	__fp_eb+0, W
	SUBWF	__fp_ex+0, W
	MOVLW	HIGH(__fp_add_swap)
	MOVWF	PCLATH
	BTFSS	STATUS, 0
	GOTO	__fp_add_swap
	MOVLW	HIGH(__fp_add_ordered)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_add_ordered
	MOVF	__fp_mb+0, W
	SUBWF	__fp_ma+0, W
	MOVF	__fp_mb+1, W
	MOVWF	__fp_w
	MOVLW	0x01
	IORWF	__fp_w, W
	BTFSS	STATUS, 0
	INCF	__fp_w, F
	BCF	__fp_wf, 0
	BTFSC	STATUS, 2
	BSF	__fp_wf, 0
	MOVF	__fp_w, W
	SUBWF	__fp_ma+1, W
	BTFSC	__fp_wf, 0
	BCF	STATUS, 0
	MOVF	__fp_mb+2, W
	MOVWF	__fp_w
	MOVLW	0x01
	IORWF	__fp_w, W
	BTFSS	STATUS, 0
	INCF	__fp_w, F
	BCF	__fp_wf, 0
	BTFSC	STATUS, 2
	BSF	__fp_wf, 0
	MOVF	__fp_w, W
	SUBWF	__fp_ma+2, W
	BTFSC	__fp_wf, 0
	BCF	STATUS, 0
	MOVF	__fp_mb+3, W
	MOVWF	__fp_w
	MOVLW	0x01
	IORWF	__fp_w, W
	BTFSS	STATUS, 0
	INCF	__fp_w, F
	BCF	__fp_wf, 0
	BTFSC	STATUS, 2
	BSF	__fp_wf, 0
	MOVF	__fp_w, W
	SUBWF	__fp_ma+3, W
	BTFSC	__fp_wf, 0
	BCF	STATUS, 0
	MOVLW	HIGH(__fp_add_ordered)
	MOVWF	PCLATH
	BTFSC	STATUS, 0
	GOTO	__fp_add_ordered
__fp_add_swap:
	MOVF	__fp_ma+0, W
	MOVWF	__fp_w
	MOVF	__fp_mb+0, W
	MOVWF	__fp_ma+0
	MOVF	__fp_w, W
	MOVWF	__fp_mb+0
	MOVF	__fp_ma+1, W
	MOVWF	__fp_w
	MOVF	__fp_mb+1, W
	MOVWF	__fp_ma+1
	MOVF	__fp_w, W
	MOVWF	__fp_mb+1
	MOVF	__fp_ma+2, W
	MOVWF	__fp_w
	MOVF	__fp_mb+2, W
	MOVWF	__fp_ma+2
	MOVF	__fp_w, W
	MOVWF	__fp_mb+2
	MOVF	__fp_ma+3, W
	MOVWF	__fp_w
	MOVF	__fp_mb+3, W
	MOVWF	__fp_ma+3
	MOVF	__fp_w, W
	MOVWF	__fp_mb+3
	MOVF	__fp_ex+0, W
	MOVWF	__fp_w
	MOVF	__fp_eb+0, W
	MOVWF	__fp_ex+0
	MOVF	__fp_w, W
	MOVWF	__fp_eb+0
	MOVF	__fp_sg, W
	MOVWF	__fp_w
	MOVF	__fp_st, W
	MOVWF	__fp_sg
	MOVF	__fp_w, W
	MOVWF	__fp_st

; align B: shift right by the exponent difference, folding the lost bits into
; a sticky bit so __fp_round can break ties correctly.
__fp_add_ordered:
	MOVF	__fp_eb+0, W
	SUBWF	__fp_ex+0, W
	MOVWF	__fp_c
	MOVLW	HIGH(__fp_add_aligned)
	MOVWF	PCLATH
	BTFSC	STATUS, 2
	GOTO	__fp_add_aligned
	MOVLW	0x1C
	SUBWF	__fp_c, W
	MOVLW	HIGH(__fp_add_far)
	MOVWF	PCLATH
	BTFSC	STATUS, 0
	GOTO	__fp_add_far
__fp_add_shift:
	MOVF	__fp_mb+0, W
	ANDLW	0x01
	MOVWF	__fp_t
	BCF	STATUS, C
	RRF	__fp_mb+3, F
	RRF	__fp_mb+2, F
	RRF	__fp_mb+1, F
	RRF	__fp_mb+0, F
	MOVF	__fp_t, W
	IORWF	__fp_mb+0, F
	MOVLW	HIGH(__fp_add_shift)
	MOVWF	PCLATH
	DECFSZ	__fp_c, F
	GOTO	__fp_add_shift
	MOVLW	HIGH(__fp_add_aligned)
	MOVWF	PCLATH
	GOTO	__fp_add_aligned
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
	MOVLW	HIGH(__fp_add_aligned)
	MOVWF	PCLATH
	BTFSC	STATUS, 2
	GOTO	__fp_add_aligned
	BSF	__fp_mb+0, 0

__fp_add_aligned:
	MOVF	__fp_sg, W
	XORWF	__fp_st, W
	ANDLW	0x01
	MOVLW	HIGH(__fp_add_unlike)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_add_unlike
	MOVF	__fp_mb+0, W
	ADDWF	__fp_ma+0, F
	MOVF	__fp_mb+1, W
	MOVWF	__fp_w
	BTFSC	STATUS, 0
	INCFSZ	__fp_w, W
	ADDWF	__fp_ma+1, F
	MOVF	__fp_mb+2, W
	MOVWF	__fp_w
	BTFSC	STATUS, 0
	INCFSZ	__fp_w, W
	ADDWF	__fp_ma+2, F
	MOVF	__fp_mb+3, W
	MOVWF	__fp_w
	BTFSC	STATUS, 0
	INCFSZ	__fp_w, W
	ADDWF	__fp_ma+3, F
	MOVLW	HIGH(__fp_round)
	MOVWF	PCLATH
	GOTO	__fp_round
__fp_add_unlike:
	MOVF	__fp_mb+0, W
	SUBWF	__fp_ma+0, F
	MOVF	__fp_mb+1, W
	MOVWF	__fp_w
	BTFSS	STATUS, 0
	INCFSZ	__fp_w, W
	SUBWF	__fp_ma+1, F
	MOVF	__fp_mb+2, W
	MOVWF	__fp_w
	BTFSS	STATUS, 0
	INCFSZ	__fp_w, W
	SUBWF	__fp_ma+2, F
	MOVF	__fp_mb+3, W
	MOVWF	__fp_w
	BTFSS	STATUS, 0
	INCFSZ	__fp_w, W
	SUBWF	__fp_ma+3, F
	MOVF	__fp_ma+0, W
	IORWF	__fp_ma+1, W
	IORWF	__fp_ma+2, W
	IORWF	__fp_ma+3, W
	MOVLW	HIGH(__fp_add_cancel)
	MOVWF	PCLATH
	BTFSC	STATUS, 2
	GOTO	__fp_add_cancel		; conditional branches reach +/-128 words only
	MOVLW	HIGH(__fp_round)
	MOVWF	PCLATH
	GOTO	__fp_round
__fp_add_cancel:
	CLRF	__fp_a+0		; exact cancellation is +0
	CLRF	__fp_a+1
	CLRF	__fp_a+2
	CLRF	__fp_a+3
	RETURN	

; --- __fp_mul: __fp_a = A * B ------------------------------------------------
__fp_mul:
	BANKSEL	__fp_a
	MOVF	__fp_a+3, W
	XORWF	__fp_b+3, W
	ANDLW	0x80
	MOVWF	__fp_t
	CLRF	__fp_sg
	BTFSC	__fp_t, 7
	BSF	__fp_sg, 0		; sign is the XOR of the operand signs

	MOVF	__fp_a+3, W
	ANDLW	0x7F
	XORLW	0x7F
	MOVLW	HIGH(__fp_mul_a_notnan)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_mul_a_notnan
	MOVLW	HIGH(__fp_mul_a_notnan)
	MOVWF	PCLATH
	BTFSS	__fp_a+2, 7
	GOTO	__fp_mul_a_notnan
	MOVF	__fp_a+2, W
	ANDLW	0x7F
	IORWF	__fp_a+1, W
	IORWF	__fp_a+0, W
	MOVLW	HIGH(__fp_mul_nan_hop)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_mul_nan_hop
__fp_mul_a_notnan:
	MOVF	__fp_b+3, W
	ANDLW	0x7F
	XORLW	0x7F
	MOVLW	HIGH(__fp_mul_b_notnan)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_mul_b_notnan
	MOVLW	HIGH(__fp_mul_b_notnan)
	MOVWF	PCLATH
	BTFSS	__fp_b+2, 7
	GOTO	__fp_mul_b_notnan
	MOVF	__fp_b+2, W
	ANDLW	0x7F
	IORWF	__fp_b+1, W
	IORWF	__fp_b+0, W
	MOVLW	HIGH(__fp_mul_nan_hop)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_mul_nan_hop
	MOVLW	HIGH(__fp_mul_b_notnan)
	MOVWF	PCLATH
	GOTO	__fp_mul_b_notnan
__fp_mul_nan_hop:
	MOVLW	HIGH(__fp_mul_qnan)
	MOVWF	PCLATH
	GOTO	__fp_mul_qnan
__fp_mul_b_notnan:
	MOVF	__fp_a+3, W		; A infinite?
	ANDLW	0x7F
	XORLW	0x7F
	MOVLW	HIGH(__fp_mul_a_finite)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_mul_a_finite
	MOVLW	HIGH(__fp_mul_a_finite)
	MOVWF	PCLATH
	BTFSS	__fp_a+2, 7
	GOTO	__fp_mul_a_finite
	MOVF	__fp_b+0, W		; 0 * inf is undefined
	IORWF	__fp_b+1, W
	IORWF	__fp_b+2, W
	MOVWF	__fp_t
	MOVF	__fp_b+3, W
	ANDLW	0x7F
	IORWF	__fp_t, W
	MOVLW	HIGH(__fp_mul_nan_hop)
	MOVWF	PCLATH
	BTFSC	STATUS, 2
	GOTO	__fp_mul_nan_hop
	MOVLW	HIGH(__fp_mul_inf)
	MOVWF	PCLATH
	GOTO	__fp_mul_inf
__fp_mul_a_finite:
	MOVF	__fp_b+3, W		; B infinite?
	ANDLW	0x7F
	XORLW	0x7F
	MOVLW	HIGH(__fp_mul_finite)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_mul_finite
	MOVLW	HIGH(__fp_mul_finite)
	MOVWF	PCLATH
	BTFSS	__fp_b+2, 7
	GOTO	__fp_mul_finite
	MOVF	__fp_a+0, W
	IORWF	__fp_a+1, W
	IORWF	__fp_a+2, W
	MOVWF	__fp_t
	MOVF	__fp_a+3, W
	ANDLW	0x7F
	IORWF	__fp_t, W
	MOVLW	HIGH(__fp_mul_nan_hop)
	MOVWF	PCLATH
	BTFSC	STATUS, 2
	GOTO	__fp_mul_nan_hop
	MOVLW	HIGH(__fp_mul_inf)
	MOVWF	PCLATH
	GOTO	__fp_mul_inf

__fp_mul_finite:
	MOVF	__fp_a+0, W		; either operand zero -> signed zero
	IORWF	__fp_a+1, W
	IORWF	__fp_a+2, W
	MOVWF	__fp_t
	MOVF	__fp_a+3, W
	ANDLW	0x7F
	IORWF	__fp_t, W
	MOVLW	HIGH(__fp_mul_zero_hop)
	MOVWF	PCLATH
	BTFSC	STATUS, 2
	GOTO	__fp_mul_zero_hop
	MOVF	__fp_b+0, W
	IORWF	__fp_b+1, W
	IORWF	__fp_b+2, W
	MOVWF	__fp_t
	MOVF	__fp_b+3, W
	ANDLW	0x7F
	IORWF	__fp_t, W
	MOVLW	HIGH(__fp_mul_zero_hop)
	MOVWF	PCLATH
	BTFSC	STATUS, 2
	GOTO	__fp_mul_zero_hop
	MOVLW	HIGH(__fp_mul_unpack_a)
	MOVWF	PCLATH
	GOTO	__fp_mul_unpack_a
__fp_mul_zero_hop:
	MOVLW	HIGH(__fp_mul_zero)
	MOVWF	PCLATH
	GOTO	__fp_mul_zero

; unpack and normalize A: significand into __fp_ma (24 bits), exponent __fp_ex
__fp_mul_unpack_a:
	MOVF	__fp_a+3, W
	ANDLW	0x7F
	MOVWF	__fp_t
	BCF	STATUS, C
	RLF	__fp_t, F
	BTFSC	__fp_a+2, 7
	BSF	__fp_t, 0
	MOVF	__fp_t, W
	MOVWF	__fp_ex
	CLRF	__fp_ex+1
	MOVF	__fp_a+0, W
	MOVWF	__fp_ma+0
	MOVF	__fp_a+1, W
	MOVWF	__fp_ma+1
	MOVF	__fp_a+2, W
	ANDLW	0x7F
	MOVWF	__fp_ma+2
	CLRF	__fp_ma+3
	MOVF	__fp_ex+0, F
	MOVLW	HIGH(__fp_mul_a_norm)
	MOVWF	PCLATH
	BTFSC	STATUS, 2
	GOTO	__fp_mul_a_norm
	BSF	__fp_ma+2, 7
	MOVLW	HIGH(__fp_mul_b_unpack)
	MOVWF	PCLATH
	GOTO	__fp_mul_b_unpack
__fp_mul_a_norm:
	MOVLW	0x01
	MOVWF	__fp_ex+0
__fp_mul_a_shl:
	MOVLW	HIGH(__fp_mul_b_unpack)
	MOVWF	PCLATH
	BTFSC	__fp_ma+2, 7
	GOTO	__fp_mul_b_unpack
	BCF	STATUS, C
	RLF	__fp_ma+0, F
	RLF	__fp_ma+1, F
	RLF	__fp_ma+2, F
	MOVLW	0x01
	SUBWF	__fp_ex+0, F
	MOVLW	HIGH(__fp_mul_a_shl)
	MOVWF	PCLATH
	BTFSC	STATUS, 0
	GOTO	__fp_mul_a_shl
	DECF	__fp_ex+1, F
	MOVLW	HIGH(__fp_mul_a_shl)
	MOVWF	PCLATH
	GOTO	__fp_mul_a_shl

__fp_mul_b_unpack:
	MOVF	__fp_b+3, W
	ANDLW	0x7F
	MOVWF	__fp_t
	BCF	STATUS, C
	RLF	__fp_t, F
	BTFSC	__fp_b+2, 7
	BSF	__fp_t, 0
	MOVF	__fp_t, W
	MOVWF	__fp_eb
	CLRF	__fp_eb+1
	MOVF	__fp_b+0, W
	MOVWF	__fp_mb+0
	MOVF	__fp_b+1, W
	MOVWF	__fp_mb+1
	MOVF	__fp_b+2, W
	ANDLW	0x7F
	MOVWF	__fp_mb+2
	CLRF	__fp_mb+3
	MOVF	__fp_eb+0, F
	MOVLW	HIGH(__fp_mul_b_norm)
	MOVWF	PCLATH
	BTFSC	STATUS, 2
	GOTO	__fp_mul_b_norm
	BSF	__fp_mb+2, 7
	MOVLW	HIGH(__fp_mul_product)
	MOVWF	PCLATH
	GOTO	__fp_mul_product
__fp_mul_b_norm:
	MOVLW	0x01
	MOVWF	__fp_eb+0
__fp_mul_b_shl:
	MOVLW	HIGH(__fp_mul_product)
	MOVWF	PCLATH
	BTFSC	__fp_mb+2, 7
	GOTO	__fp_mul_product
	BCF	STATUS, C
	RLF	__fp_mb+0, F
	RLF	__fp_mb+1, F
	RLF	__fp_mb+2, F
	MOVLW	0x01
	SUBWF	__fp_eb+0, F
	MOVLW	HIGH(__fp_mul_b_shl)
	MOVWF	PCLATH
	BTFSC	STATUS, 0
	GOTO	__fp_mul_b_shl
	DECF	__fp_eb+1, F
	MOVLW	HIGH(__fp_mul_b_shl)
	MOVWF	PCLATH
	GOTO	__fp_mul_b_shl

__fp_mul_product:
; 24 x 24 -> 48 bit schoolbook product, accumulated into __fp_p (6 bytes).
	CLRF	__fp_p
	CLRF	__fp_p+1
	CLRF	__fp_p+2
	CLRF	__fp_p+3
	CLRF	__fp_p+4
	CLRF	__fp_p+5
	MOVF	__fp_ma, W
	MOVWF	__fp_ua
	MOVF	__fp_mb, W
	MOVWF	__fp_ub
	MOVLW	HIGH(__fp_mul8)
	MOVWF	PCLATH
	CALL	__fp_mul8
	MOVF	__fp_pl, W
	ADDWF	__fp_p, F
	MOVF	__fp_ph, W
	MOVWF	__fp_w
	BTFSC	STATUS, 0
	INCFSZ	__fp_w, W
	ADDWF	__fp_p+1, F
	MOVLW	HIGH(__fp_mul_p1)
	MOVWF	PCLATH
	BTFSS	STATUS, 0
	GOTO	__fp_mul_p1
	INCF	__fp_p+2, F
	MOVF	__fp_p+2, F
	MOVLW	HIGH(__fp_mul_p1)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_mul_p1
	INCF	__fp_p+3, F
	MOVF	__fp_p+3, F
	MOVLW	HIGH(__fp_mul_p1)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_mul_p1
	INCF	__fp_p+4, F
	MOVF	__fp_p+4, F
	MOVLW	HIGH(__fp_mul_p1)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_mul_p1
	INCF	__fp_p+5, F
__fp_mul_p1:
	MOVF	__fp_ma, W
	MOVWF	__fp_ua
	MOVF	__fp_mb+1, W
	MOVWF	__fp_ub
	MOVLW	HIGH(__fp_mul8)
	MOVWF	PCLATH
	CALL	__fp_mul8
	MOVF	__fp_pl, W
	ADDWF	__fp_p+1, F
	MOVF	__fp_ph, W
	MOVWF	__fp_w
	BTFSC	STATUS, 0
	INCFSZ	__fp_w, W
	ADDWF	__fp_p+2, F
	MOVLW	HIGH(__fp_mul_p2)
	MOVWF	PCLATH
	BTFSS	STATUS, 0
	GOTO	__fp_mul_p2
	INCF	__fp_p+3, F
	MOVF	__fp_p+3, F
	MOVLW	HIGH(__fp_mul_p2)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_mul_p2
	INCF	__fp_p+4, F
	MOVF	__fp_p+4, F
	MOVLW	HIGH(__fp_mul_p2)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_mul_p2
	INCF	__fp_p+5, F
__fp_mul_p2:
	MOVF	__fp_ma, W
	MOVWF	__fp_ua
	MOVF	__fp_mb+2, W
	MOVWF	__fp_ub
	MOVLW	HIGH(__fp_mul8)
	MOVWF	PCLATH
	CALL	__fp_mul8
	MOVF	__fp_pl, W
	ADDWF	__fp_p+2, F
	MOVF	__fp_ph, W
	MOVWF	__fp_w
	BTFSC	STATUS, 0
	INCFSZ	__fp_w, W
	ADDWF	__fp_p+3, F
	MOVLW	HIGH(__fp_mul_p3)
	MOVWF	PCLATH
	BTFSS	STATUS, 0
	GOTO	__fp_mul_p3
	INCF	__fp_p+4, F
	MOVF	__fp_p+4, F
	MOVLW	HIGH(__fp_mul_p3)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_mul_p3
	INCF	__fp_p+5, F
__fp_mul_p3:
	MOVF	__fp_ma+1, W
	MOVWF	__fp_ua
	MOVF	__fp_mb, W
	MOVWF	__fp_ub
	MOVLW	HIGH(__fp_mul8)
	MOVWF	PCLATH
	CALL	__fp_mul8
	MOVF	__fp_pl, W
	ADDWF	__fp_p+1, F
	MOVF	__fp_ph, W
	MOVWF	__fp_w
	BTFSC	STATUS, 0
	INCFSZ	__fp_w, W
	ADDWF	__fp_p+2, F
	MOVLW	HIGH(__fp_mul_p4)
	MOVWF	PCLATH
	BTFSS	STATUS, 0
	GOTO	__fp_mul_p4
	INCF	__fp_p+3, F
	MOVF	__fp_p+3, F
	MOVLW	HIGH(__fp_mul_p4)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_mul_p4
	INCF	__fp_p+4, F
	MOVF	__fp_p+4, F
	MOVLW	HIGH(__fp_mul_p4)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_mul_p4
	INCF	__fp_p+5, F
__fp_mul_p4:
	MOVF	__fp_ma+1, W
	MOVWF	__fp_ua
	MOVF	__fp_mb+1, W
	MOVWF	__fp_ub
	MOVLW	HIGH(__fp_mul8)
	MOVWF	PCLATH
	CALL	__fp_mul8
	MOVF	__fp_pl, W
	ADDWF	__fp_p+2, F
	MOVF	__fp_ph, W
	MOVWF	__fp_w
	BTFSC	STATUS, 0
	INCFSZ	__fp_w, W
	ADDWF	__fp_p+3, F
	MOVLW	HIGH(__fp_mul_p5)
	MOVWF	PCLATH
	BTFSS	STATUS, 0
	GOTO	__fp_mul_p5
	INCF	__fp_p+4, F
	MOVF	__fp_p+4, F
	MOVLW	HIGH(__fp_mul_p5)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_mul_p5
	INCF	__fp_p+5, F
__fp_mul_p5:
	MOVF	__fp_ma+1, W
	MOVWF	__fp_ua
	MOVF	__fp_mb+2, W
	MOVWF	__fp_ub
	MOVLW	HIGH(__fp_mul8)
	MOVWF	PCLATH
	CALL	__fp_mul8
	MOVF	__fp_pl, W
	ADDWF	__fp_p+3, F
	MOVF	__fp_ph, W
	MOVWF	__fp_w
	BTFSC	STATUS, 0
	INCFSZ	__fp_w, W
	ADDWF	__fp_p+4, F
	MOVLW	HIGH(__fp_mul_p6)
	MOVWF	PCLATH
	BTFSS	STATUS, 0
	GOTO	__fp_mul_p6
	INCF	__fp_p+5, F
__fp_mul_p6:
	MOVF	__fp_ma+2, W
	MOVWF	__fp_ua
	MOVF	__fp_mb, W
	MOVWF	__fp_ub
	MOVLW	HIGH(__fp_mul8)
	MOVWF	PCLATH
	CALL	__fp_mul8
	MOVF	__fp_pl, W
	ADDWF	__fp_p+2, F
	MOVF	__fp_ph, W
	MOVWF	__fp_w
	BTFSC	STATUS, 0
	INCFSZ	__fp_w, W
	ADDWF	__fp_p+3, F
	MOVLW	HIGH(__fp_mul_p7)
	MOVWF	PCLATH
	BTFSS	STATUS, 0
	GOTO	__fp_mul_p7
	INCF	__fp_p+4, F
	MOVF	__fp_p+4, F
	MOVLW	HIGH(__fp_mul_p7)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_mul_p7
	INCF	__fp_p+5, F
__fp_mul_p7:
	MOVF	__fp_ma+2, W
	MOVWF	__fp_ua
	MOVF	__fp_mb+1, W
	MOVWF	__fp_ub
	MOVLW	HIGH(__fp_mul8)
	MOVWF	PCLATH
	CALL	__fp_mul8
	MOVF	__fp_pl, W
	ADDWF	__fp_p+3, F
	MOVF	__fp_ph, W
	MOVWF	__fp_w
	BTFSC	STATUS, 0
	INCFSZ	__fp_w, W
	ADDWF	__fp_p+4, F
	MOVLW	HIGH(__fp_mul_p8)
	MOVWF	PCLATH
	BTFSS	STATUS, 0
	GOTO	__fp_mul_p8
	INCF	__fp_p+5, F
__fp_mul_p8:
	MOVF	__fp_ma+2, W
	MOVWF	__fp_ua
	MOVF	__fp_mb+2, W
	MOVWF	__fp_ub
	MOVLW	HIGH(__fp_mul8)
	MOVWF	PCLATH
	CALL	__fp_mul8
	MOVF	__fp_pl, W
	ADDWF	__fp_p+4, F
	MOVF	__fp_ph, W
	MOVWF	__fp_w
	BTFSC	STATUS, 0
	INCFSZ	__fp_w, W
	ADDWF	__fp_p+5, F
__fp_mul_p9:

; exponent = ea + eb - 126 (the 21-bit reduction below is folded into it)
	MOVF	__fp_eb+0, W
	ADDWF	__fp_ex+0, F
	MOVF	__fp_eb+1, W
	MOVWF	__fp_w
	BTFSC	STATUS, 0
	INCFSZ	__fp_w, W
	ADDWF	__fp_ex+1, F
	MOVLW	0x7E
	SUBWF	__fp_ex+0, F
	MOVLW	HIGH(__fp_mul_exp_done)
	MOVWF	PCLATH
	BTFSC	STATUS, 0
	GOTO	__fp_mul_exp_done
	DECF	__fp_ex+1, F
__fp_mul_exp_done:

; sticky over the 21 discarded bits, then take the product >> 21
	MOVF	__fp_p+0, W
	IORWF	__fp_p+1, W
	MOVWF	__fp_t
	MOVF	__fp_p+2, W
	ANDLW	0x1F
	IORWF	__fp_t, F
	MOVF	__fp_p+2, W
	MOVWF	__fp_ma+0
	MOVF	__fp_p+3, W
	MOVWF	__fp_ma+1
	MOVF	__fp_p+4, W
	MOVWF	__fp_ma+2
	MOVF	__fp_p+5, W
	MOVWF	__fp_ma+3
	MOVLW	0x05
	MOVWF	__fp_c
__fp_mul_shr:
	BCF	STATUS, C
	RRF	__fp_ma+3, F
	RRF	__fp_ma+2, F
	RRF	__fp_ma+1, F
	RRF	__fp_ma+0, F
	MOVLW	HIGH(__fp_mul_shr)
	MOVWF	PCLATH
	DECFSZ	__fp_c, F
	GOTO	__fp_mul_shr
	MOVF	__fp_t, F
	MOVLW	HIGH(__fp_mul_round)
	MOVWF	PCLATH
	BTFSC	STATUS, 2
	GOTO	__fp_mul_round
	BSF	__fp_ma+0, 0
__fp_mul_round:
	MOVLW	HIGH(__fp_round)
	MOVWF	PCLATH
	GOTO	__fp_round

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
	MOVLW	HIGH(__fp_mul_apply_sign)
	MOVWF	PCLATH
	GOTO	__fp_mul_apply_sign
__fp_mul_zero:
	CLRF	__fp_a+0
	CLRF	__fp_a+1
	CLRF	__fp_a+2
	CLRF	__fp_a+3
__fp_mul_apply_sign:
	BTFSC	__fp_sg, 0
	BSF	__fp_a+3, 7
	RETURN	

; --- __fp_div: __fp_a = A / B ------------------------------------------------
__fp_div:
	BANKSEL	__fp_a
	MOVF	__fp_a+3, W
	XORWF	__fp_b+3, W
	ANDLW	0x80
	MOVWF	__fp_t
	CLRF	__fp_sg
	BTFSC	__fp_t, 7
	BSF	__fp_sg, 0

	MOVF	__fp_a+3, W
	ANDLW	0x7F
	XORLW	0x7F
	MOVLW	HIGH(__fp_div_a_notnan)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_div_a_notnan
	MOVLW	HIGH(__fp_div_a_notnan)
	MOVWF	PCLATH
	BTFSS	__fp_a+2, 7
	GOTO	__fp_div_a_notnan
	MOVF	__fp_a+2, W
	ANDLW	0x7F
	IORWF	__fp_a+1, W
	IORWF	__fp_a+0, W
	MOVLW	HIGH(__fp_div_nan_hop)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_div_nan_hop
__fp_div_a_notnan:
	MOVF	__fp_b+3, W
	ANDLW	0x7F
	XORLW	0x7F
	MOVLW	HIGH(__fp_div_b_notnan)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_div_b_notnan
	MOVLW	HIGH(__fp_div_b_notnan)
	MOVWF	PCLATH
	BTFSS	__fp_b+2, 7
	GOTO	__fp_div_b_notnan
	MOVF	__fp_b+2, W
	ANDLW	0x7F
	IORWF	__fp_b+1, W
	IORWF	__fp_b+0, W
	MOVLW	HIGH(__fp_div_nan_hop)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_div_nan_hop
	MOVLW	HIGH(__fp_div_b_notnan)
	MOVWF	PCLATH
	GOTO	__fp_div_b_notnan
__fp_div_nan_hop:
	MOVLW	HIGH(__fp_div_qnan)
	MOVWF	PCLATH
	GOTO	__fp_div_qnan

__fp_div_b_notnan:
	MOVLW	HIGH(__fp_div_b_screened)
	MOVWF	PCLATH
	GOTO	__fp_div_b_screened
__fp_div_far_qnan:
	MOVLW	HIGH(__fp_div_qnan)
	MOVWF	PCLATH
	GOTO	__fp_div_qnan
__fp_div_far_inf:
	MOVLW	HIGH(__fp_div_inf)
	MOVWF	PCLATH
	GOTO	__fp_div_inf
__fp_div_far_zero:
	MOVLW	HIGH(__fp_div_zero)
	MOVWF	PCLATH
	GOTO	__fp_div_zero
__fp_div_b_screened:
	MOVF	__fp_a+3, W		; A infinite?
	ANDLW	0x7F
	XORLW	0x7F
	MOVLW	HIGH(__fp_div_a_finite)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_div_a_finite
	MOVLW	HIGH(__fp_div_a_finite)
	MOVWF	PCLATH
	BTFSS	__fp_a+2, 7
	GOTO	__fp_div_a_finite
	MOVF	__fp_b+3, W		; inf / inf is undefined
	ANDLW	0x7F
	XORLW	0x7F
	MOVLW	HIGH(__fp_div_far_inf)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_div_far_inf
	MOVLW	HIGH(__fp_div_far_inf)
	MOVWF	PCLATH
	BTFSS	__fp_b+2, 7
	GOTO	__fp_div_far_inf
	MOVLW	HIGH(__fp_div_far_qnan)
	MOVWF	PCLATH
	GOTO	__fp_div_far_qnan
__fp_div_a_finite:
	MOVF	__fp_b+3, W		; B infinite -> signed zero
	ANDLW	0x7F
	XORLW	0x7F
	MOVLW	HIGH(__fp_div_b_finite)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_div_b_finite
	MOVLW	HIGH(__fp_div_far_zero)
	MOVWF	PCLATH
	BTFSC	__fp_b+2, 7
	GOTO	__fp_div_far_zero
__fp_div_b_finite:
	MOVF	__fp_b+0, W		; B zero?
	IORWF	__fp_b+1, W
	IORWF	__fp_b+2, W
	MOVWF	__fp_t
	MOVF	__fp_b+3, W
	ANDLW	0x7F
	IORWF	__fp_t, W
	MOVLW	HIGH(__fp_div_a_zero)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_div_a_zero
	MOVF	__fp_a+0, W		; 0 / 0 is undefined, x / 0 is infinity
	IORWF	__fp_a+1, W
	IORWF	__fp_a+2, W
	MOVWF	__fp_t
	MOVF	__fp_a+3, W
	ANDLW	0x7F
	IORWF	__fp_t, W
	MOVLW	HIGH(__fp_div_far_qnan)
	MOVWF	PCLATH
	BTFSC	STATUS, 2
	GOTO	__fp_div_far_qnan
	MOVLW	HIGH(__fp_div_far_inf)
	MOVWF	PCLATH
	GOTO	__fp_div_far_inf
__fp_div_a_zero:
	MOVF	__fp_a+0, W
	IORWF	__fp_a+1, W
	IORWF	__fp_a+2, W
	MOVWF	__fp_t
	MOVF	__fp_a+3, W
	ANDLW	0x7F
	IORWF	__fp_t, W
	MOVLW	HIGH(__fp_div_far_zero)
	MOVWF	PCLATH
	BTFSC	STATUS, 2
	GOTO	__fp_div_far_zero

; unpack and normalize both operands to 24-bit significands
	MOVF	__fp_a+3, W
	ANDLW	0x7F
	MOVWF	__fp_t
	BCF	STATUS, C
	RLF	__fp_t, F
	BTFSC	__fp_a+2, 7
	BSF	__fp_t, 0
	MOVF	__fp_t, W
	MOVWF	__fp_ex
	CLRF	__fp_ex+1
	MOVF	__fp_a+0, W
	MOVWF	__fp_ma+0
	MOVF	__fp_a+1, W
	MOVWF	__fp_ma+1
	MOVF	__fp_a+2, W
	ANDLW	0x7F
	MOVWF	__fp_ma+2
	CLRF	__fp_ma+3
	MOVF	__fp_ex+0, F
	MOVLW	HIGH(__fp_div_a_norm)
	MOVWF	PCLATH
	BTFSC	STATUS, 2
	GOTO	__fp_div_a_norm
	BSF	__fp_ma+2, 7
	MOVLW	HIGH(__fp_div_b_unpack)
	MOVWF	PCLATH
	GOTO	__fp_div_b_unpack
__fp_div_a_norm:
	MOVLW	0x01
	MOVWF	__fp_ex+0
__fp_div_a_shl:
	MOVLW	HIGH(__fp_div_b_unpack)
	MOVWF	PCLATH
	BTFSC	__fp_ma+2, 7
	GOTO	__fp_div_b_unpack
	BCF	STATUS, C
	RLF	__fp_ma+0, F
	RLF	__fp_ma+1, F
	RLF	__fp_ma+2, F
	MOVLW	0x01
	SUBWF	__fp_ex+0, F
	MOVLW	HIGH(__fp_div_a_shl)
	MOVWF	PCLATH
	BTFSC	STATUS, 0
	GOTO	__fp_div_a_shl
	DECF	__fp_ex+1, F
	MOVLW	HIGH(__fp_div_a_shl)
	MOVWF	PCLATH
	GOTO	__fp_div_a_shl

__fp_div_b_unpack:
	MOVF	__fp_b+3, W
	ANDLW	0x7F
	MOVWF	__fp_t
	BCF	STATUS, C
	RLF	__fp_t, F
	BTFSC	__fp_b+2, 7
	BSF	__fp_t, 0
	MOVF	__fp_t, W
	MOVWF	__fp_eb
	CLRF	__fp_eb+1
	MOVF	__fp_b+0, W
	MOVWF	__fp_mb+0
	MOVF	__fp_b+1, W
	MOVWF	__fp_mb+1
	MOVF	__fp_b+2, W
	ANDLW	0x7F
	MOVWF	__fp_mb+2
	CLRF	__fp_mb+3
	MOVF	__fp_eb+0, F
	MOVLW	HIGH(__fp_div_b_norm)
	MOVWF	PCLATH
	BTFSC	STATUS, 2
	GOTO	__fp_div_b_norm
	BSF	__fp_mb+2, 7
	MOVLW	HIGH(__fp_div_setup)
	MOVWF	PCLATH
	GOTO	__fp_div_setup
__fp_div_b_norm:
	MOVLW	0x01
	MOVWF	__fp_eb+0
__fp_div_b_shl:
	MOVLW	HIGH(__fp_div_setup)
	MOVWF	PCLATH
	BTFSC	__fp_mb+2, 7
	GOTO	__fp_div_setup
	BCF	STATUS, C
	RLF	__fp_mb+0, F
	RLF	__fp_mb+1, F
	RLF	__fp_mb+2, F
	MOVLW	0x01
	SUBWF	__fp_eb+0, F
	MOVLW	HIGH(__fp_div_b_shl)
	MOVWF	PCLATH
	BTFSC	STATUS, 0
	GOTO	__fp_div_b_shl
	DECF	__fp_eb+1, F
	MOVLW	HIGH(__fp_div_b_shl)
	MOVWF	PCLATH
	GOTO	__fp_div_b_shl

; 27 restoring steps give floor(ma * 2^27 / mb): 24 significand bits plus the
; 3 guard bits __fp_round expects. __fp_ma holds the remainder, __fp_p the
; quotient.
__fp_div_setup:
	MOVF	__fp_eb+0, W		; exponent = ea - eb + 127
	SUBWF	__fp_ex+0, F
	MOVF	__fp_eb+1, W
	MOVWF	__fp_w
	BTFSS	STATUS, 0
	INCFSZ	__fp_w, W
	SUBWF	__fp_ex+1, F
	MOVLW	0x7F
	ADDWF	__fp_ex+0, F
	MOVLW	HIGH(__fp_div_prescale)
	MOVWF	PCLATH
	BTFSS	STATUS, 0
	GOTO	__fp_div_prescale
	INCF	__fp_ex+1, F

; The restoring loop needs rem < mb on entry. Both significands sit in
; [2^23, 2^24), so ma < mb half the time: shift ma up once and pay for it in
; the exponent, which also pins the quotient into [2^26, 2^27).
__fp_div_prescale:
	MOVF	__fp_mb+0, W
	SUBWF	__fp_ma+0, W
	MOVF	__fp_mb+1, W
	MOVWF	__fp_w
	MOVLW	0x01
	IORWF	__fp_w, W
	BTFSS	STATUS, 0
	INCF	__fp_w, F
	BCF	__fp_wf, 0
	BTFSC	STATUS, 2
	BSF	__fp_wf, 0
	MOVF	__fp_w, W
	SUBWF	__fp_ma+1, W
	BTFSC	__fp_wf, 0
	BCF	STATUS, 0
	MOVF	__fp_mb+2, W
	MOVWF	__fp_w
	MOVLW	0x01
	IORWF	__fp_w, W
	BTFSS	STATUS, 0
	INCF	__fp_w, F
	BCF	__fp_wf, 0
	BTFSC	STATUS, 2
	BSF	__fp_wf, 0
	MOVF	__fp_w, W
	SUBWF	__fp_ma+2, W
	BTFSC	__fp_wf, 0
	BCF	STATUS, 0
	MOVF	__fp_mb+3, W
	MOVWF	__fp_w
	MOVLW	0x01
	IORWF	__fp_w, W
	BTFSS	STATUS, 0
	INCF	__fp_w, F
	BCF	__fp_wf, 0
	BTFSC	STATUS, 2
	BSF	__fp_wf, 0
	MOVF	__fp_w, W
	SUBWF	__fp_ma+3, W
	BTFSC	__fp_wf, 0
	BCF	STATUS, 0
	MOVLW	HIGH(__fp_div_first)
	MOVWF	PCLATH
	BTFSC	STATUS, 0
	GOTO	__fp_div_first
	BCF	STATUS, C
	RLF	__fp_ma+0, F
	RLF	__fp_ma+1, F
	RLF	__fp_ma+2, F
	RLF	__fp_ma+3, F
	MOVLW	0x01
	SUBWF	__fp_ex+0, F
	MOVLW	HIGH(__fp_div_first)
	MOVWF	PCLATH
	BTFSC	STATUS, 0
	GOTO	__fp_div_first
	DECF	__fp_ex+1, F

__fp_div_first:
	MOVF	__fp_mb+0, W		; rem = ma - mb, quotient starts at 1
	SUBWF	__fp_ma+0, F
	MOVF	__fp_mb+1, W
	MOVWF	__fp_w
	BTFSS	STATUS, 0
	INCFSZ	__fp_w, W
	SUBWF	__fp_ma+1, F
	MOVF	__fp_mb+2, W
	MOVWF	__fp_w
	BTFSS	STATUS, 0
	INCFSZ	__fp_w, W
	SUBWF	__fp_ma+2, F
	MOVF	__fp_mb+3, W
	MOVWF	__fp_w
	BTFSS	STATUS, 0
	INCFSZ	__fp_w, W
	SUBWF	__fp_ma+3, F
	CLRF	__fp_p+1
	CLRF	__fp_p+2
	CLRF	__fp_p+3
	MOVLW	0x01
	MOVWF	__fp_p+0
	MOVLW	0x1A
	MOVWF	__fp_c
__fp_div_loop:
	BCF	STATUS, C
	RLF	__fp_ma+0, F
	RLF	__fp_ma+1, F
	RLF	__fp_ma+2, F
	RLF	__fp_ma+3, F
	BCF	STATUS, C
	RLF	__fp_p+0, F
	RLF	__fp_p+1, F
	RLF	__fp_p+2, F
	RLF	__fp_p+3, F
	MOVF	__fp_mb+0, W
	SUBWF	__fp_ma+0, W
	MOVF	__fp_mb+1, W
	MOVWF	__fp_w
	MOVLW	0x01
	IORWF	__fp_w, W
	BTFSS	STATUS, 0
	INCF	__fp_w, F
	BCF	__fp_wf, 0
	BTFSC	STATUS, 2
	BSF	__fp_wf, 0
	MOVF	__fp_w, W
	SUBWF	__fp_ma+1, W
	BTFSC	__fp_wf, 0
	BCF	STATUS, 0
	MOVF	__fp_mb+2, W
	MOVWF	__fp_w
	MOVLW	0x01
	IORWF	__fp_w, W
	BTFSS	STATUS, 0
	INCF	__fp_w, F
	BCF	__fp_wf, 0
	BTFSC	STATUS, 2
	BSF	__fp_wf, 0
	MOVF	__fp_w, W
	SUBWF	__fp_ma+2, W
	BTFSC	__fp_wf, 0
	BCF	STATUS, 0
	MOVF	__fp_mb+3, W
	MOVWF	__fp_w
	MOVLW	0x01
	IORWF	__fp_w, W
	BTFSS	STATUS, 0
	INCF	__fp_w, F
	BCF	__fp_wf, 0
	BTFSC	STATUS, 2
	BSF	__fp_wf, 0
	MOVF	__fp_w, W
	SUBWF	__fp_ma+3, W
	BTFSC	__fp_wf, 0
	BCF	STATUS, 0
	MOVLW	HIGH(__fp_div_next)
	MOVWF	PCLATH
	BTFSS	STATUS, 0
	GOTO	__fp_div_next
	MOVF	__fp_mb+0, W
	SUBWF	__fp_ma+0, F
	MOVF	__fp_mb+1, W
	MOVWF	__fp_w
	BTFSS	STATUS, 0
	INCFSZ	__fp_w, W
	SUBWF	__fp_ma+1, F
	MOVF	__fp_mb+2, W
	MOVWF	__fp_w
	BTFSS	STATUS, 0
	INCFSZ	__fp_w, W
	SUBWF	__fp_ma+2, F
	MOVF	__fp_mb+3, W
	MOVWF	__fp_w
	BTFSS	STATUS, 0
	INCFSZ	__fp_w, W
	SUBWF	__fp_ma+3, F
	BSF	__fp_p+0, 0
__fp_div_next:
	MOVLW	HIGH(__fp_div_loop)
	MOVWF	PCLATH
	DECFSZ	__fp_c, F
	GOTO	__fp_div_loop

	MOVF	__fp_ma+0, W		; a non-zero remainder is the sticky bit
	IORWF	__fp_ma+1, W
	IORWF	__fp_ma+2, W
	IORWF	__fp_ma+3, W
	MOVWF	__fp_t
	MOVF	__fp_p+0, W
	MOVWF	__fp_ma+0
	MOVF	__fp_p+1, W
	MOVWF	__fp_ma+1
	MOVF	__fp_p+2, W
	MOVWF	__fp_ma+2
	MOVF	__fp_p+3, W
	MOVWF	__fp_ma+3
	MOVF	__fp_t, F
	MOVLW	HIGH(__fp_div_round)
	MOVWF	PCLATH
	BTFSC	STATUS, 2
	GOTO	__fp_div_round
	BSF	__fp_ma+0, 0
__fp_div_round:
	MOVLW	HIGH(__fp_round)
	MOVWF	PCLATH
	GOTO	__fp_round

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
	MOVLW	HIGH(__fp_div_apply_sign)
	MOVWF	PCLATH
	GOTO	__fp_div_apply_sign
__fp_div_zero:
	CLRF	__fp_a+0
	CLRF	__fp_a+1
	CLRF	__fp_a+2
	CLRF	__fp_a+3
__fp_div_apply_sign:
	BTFSC	__fp_sg, 0
	BSF	__fp_a+3, 7
	RETURN	

; --- __fp_float: __fp_a = (float) int32 in __fp_a ----------------------------
__fp_float:
	BANKSEL	__fp_a
	MOVF	__fp_a+0, W
	IORWF	__fp_a+1, W
	IORWF	__fp_a+2, W
	IORWF	__fp_a+3, W
	MOVLW	HIGH(__fp_float_go)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_float_go
	RETURN			; +0 already sits in __fp_a
__fp_float_go:
	CLRF	__fp_sg
	MOVLW	HIGH(__fp_float_mag)
	MOVWF	PCLATH
	BTFSS	__fp_a+3, 7
	GOTO	__fp_float_mag
	BSF	__fp_sg, 0
	COMF	__fp_a+0, F
	COMF	__fp_a+1, F
	COMF	__fp_a+2, F
	COMF	__fp_a+3, F
	INCF	__fp_a+0, F
	MOVF	__fp_a+0, F
	BTFSC	STATUS, Z
	INCF	__fp_a+1, F
	MOVF	__fp_a+1, F
	BTFSC	STATUS, Z
	INCF	__fp_a+2, F
	MOVF	__fp_a+2, F
	BTFSC	STATUS, Z
	INCF	__fp_a+3, F
__fp_float_mag:
	MOVF	__fp_a+0, W
	MOVWF	__fp_ma+0
	MOVF	__fp_a+1, W
	MOVWF	__fp_ma+1
	MOVF	__fp_a+2, W
	MOVWF	__fp_ma+2
	MOVF	__fp_a+3, W
	MOVWF	__fp_ma+3
	MOVLW	0x99		; magnitude at exponent 153 (no guard shift)
	MOVWF	__fp_ex+0
	CLRF	__fp_ex+1
	MOVLW	HIGH(__fp_round)
	MOVWF	PCLATH
	GOTO	__fp_round

; --- __fp_fix: __fp_a = (int32) float in __fp_a, truncating toward zero ------
__fp_fix:
	BANKSEL	__fp_a
	CLRF	__fp_sg
	BTFSC	__fp_a+3, 7
	BSF	__fp_sg, 0
	MOVF	__fp_a+3, W
	ANDLW	0x7F
	MOVWF	__fp_t
	BCF	STATUS, C
	RLF	__fp_t, F
	BTFSC	__fp_a+2, 7
	BSF	__fp_t, 0		; __fp_t = biased exponent

	MOVLW	0x9E		; exponent >= 158 leaves the int32 range
	SUBWF	__fp_t, W
	MOVLW	HIGH(__fp_fix_sat)
	MOVWF	PCLATH
	BTFSC	STATUS, 0
	GOTO	__fp_fix_sat
	MOVLW	0x7F		; exponent < 127: magnitude below 1
	SUBWF	__fp_t, W
	MOVLW	HIGH(__fp_fix_zero)
	MOVWF	PCLATH
	BTFSS	STATUS, 0
	GOTO	__fp_fix_zero

	MOVF	__fp_a+0, W
	MOVWF	__fp_ma+0
	MOVF	__fp_a+1, W
	MOVWF	__fp_ma+1
	MOVF	__fp_a+2, W
	ANDLW	0x7F
	MOVWF	__fp_ma+2
	CLRF	__fp_ma+3
	BSF	__fp_ma+2, 7

	MOVLW	0x96		; shift = exponent - 150
	SUBWF	__fp_t, W
	MOVWF	__fp_c
	MOVLW	HIGH(__fp_fix_left)
	MOVWF	PCLATH
	BTFSC	STATUS, 0
	GOTO	__fp_fix_left
	COMF	__fp_c, F		; negative: shift right by its magnitude
	INCF	__fp_c, F
__fp_fix_right:
	MOVF	__fp_c, F
	MOVLW	HIGH(__fp_fix_store)
	MOVWF	PCLATH
	BTFSC	STATUS, 2
	GOTO	__fp_fix_store
	BCF	STATUS, C
	RRF	__fp_ma+3, F
	RRF	__fp_ma+2, F
	RRF	__fp_ma+1, F
	RRF	__fp_ma+0, F
	DECF	__fp_c, F
	MOVLW	HIGH(__fp_fix_right)
	MOVWF	PCLATH
	GOTO	__fp_fix_right
__fp_fix_left:
	MOVF	__fp_c, F
	MOVLW	HIGH(__fp_fix_store)
	MOVWF	PCLATH
	BTFSC	STATUS, 2
	GOTO	__fp_fix_store
	BCF	STATUS, C
	RLF	__fp_ma+0, F
	RLF	__fp_ma+1, F
	RLF	__fp_ma+2, F
	RLF	__fp_ma+3, F
	DECF	__fp_c, F
	MOVLW	HIGH(__fp_fix_left)
	MOVWF	PCLATH
	GOTO	__fp_fix_left

__fp_fix_store:
	MOVF	__fp_ma+0, W
	MOVWF	__fp_a+0
	MOVF	__fp_ma+1, W
	MOVWF	__fp_a+1
	MOVF	__fp_ma+2, W
	MOVWF	__fp_a+2
	MOVF	__fp_ma+3, W
	MOVWF	__fp_a+3
	BTFSS	__fp_sg, 0
	RETURN	
	COMF	__fp_a+0, F
	COMF	__fp_a+1, F
	COMF	__fp_a+2, F
	COMF	__fp_a+3, F
	INCF	__fp_a+0, F
	MOVF	__fp_a+0, F
	BTFSC	STATUS, Z
	INCF	__fp_a+1, F
	MOVF	__fp_a+1, F
	BTFSC	STATUS, Z
	INCF	__fp_a+2, F
	MOVF	__fp_a+2, F
	BTFSC	STATUS, Z
	INCF	__fp_a+3, F
	RETURN	
__fp_fix_zero:
	CLRF	__fp_a+0
	CLRF	__fp_a+1
	CLRF	__fp_a+2
	CLRF	__fp_a+3
	RETURN	
__fp_fix_sat:
	MOVLW	0xFF
	MOVWF	__fp_a+0
	MOVLW	0xFF
	MOVWF	__fp_a+1
	MOVLW	0xFF
	MOVWF	__fp_a+2
	MOVLW	0x7F
	MOVWF	__fp_a+3
	BTFSS	__fp_sg, 0
	RETURN	
	CLRF	__fp_a+0		; negative saturation is -2^31
	CLRF	__fp_a+1
	CLRF	__fp_a+2
	MOVLW	0x80
	MOVWF	__fp_a+3
	RETURN	

; --- __fp_floor: __fp_a = floor(__fp_a) --------------------------------------
__fp_floor:
	BANKSEL	__fp_a
	MOVF	__fp_a+3, W
	ANDLW	0x7F
	MOVWF	__fp_t
	BCF	STATUS, C
	RLF	__fp_t, F
	BTFSC	__fp_a+2, 7
	BSF	__fp_t, 0		; __fp_t = biased exponent

	MOVF	__fp_t, W
	XORLW	0xFF
	MOVLW	HIGH(__fp_floor_done)
	MOVWF	PCLATH
	BTFSC	STATUS, 2
	GOTO	__fp_floor_done		; inf or NaN
	MOVLW	0x96
	SUBWF	__fp_t, W
	MOVLW	HIGH(__fp_floor_done)
	MOVWF	PCLATH
	BTFSC	STATUS, 0
	GOTO	__fp_floor_done		; exponent >= 150: already integral
	MOVLW	0x7F
	SUBWF	__fp_t, W
	MOVLW	HIGH(__fp_floor_split)
	MOVWF	PCLATH
	BTFSC	STATUS, 0
	GOTO	__fp_floor_split

	MOVF	__fp_a+0, W		; |x| < 1
	IORWF	__fp_a+1, W
	IORWF	__fp_a+2, W
	MOVWF	__fp_c
	MOVF	__fp_a+3, W
	ANDLW	0x7F
	IORWF	__fp_c, W
	MOVLW	HIGH(__fp_floor_done)
	MOVWF	PCLATH
	BTFSC	STATUS, 2
	GOTO	__fp_floor_done		; +/-0 is its own floor
	MOVLW	HIGH(__fp_floor_zero)
	MOVWF	PCLATH
	BTFSS	__fp_a+3, 7
	GOTO	__fp_floor_zero
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
	BTFSC	__fp_a+3, 7
	BSF	__fp_sg, 0
	MOVF	__fp_t, W
	MOVWF	__fp_ex
	CLRF	__fp_ex+1
	MOVF	__fp_a+0, W
	MOVWF	__fp_ma+0
	MOVF	__fp_a+1, W
	MOVWF	__fp_ma+1
	MOVF	__fp_a+2, W
	ANDLW	0x7F
	MOVWF	__fp_ma+2
	CLRF	__fp_ma+3
	BSF	__fp_ma+2, 7

	MOVLW	0x96		; count = 150 - exponent, the bits below the point
	MOVWF	__fp_c
	MOVF	__fp_t, W
	SUBWF	__fp_c, F
	MOVF	__fp_c, W
	MOVWF	__fp_st		; remember it for the shift back
	CLRF	__fp_t		; fraction seen
__fp_floor_drop:
	MOVF	__fp_c, F
	MOVLW	HIGH(__fp_floor_adjust)
	MOVWF	PCLATH
	BTFSC	STATUS, 2
	GOTO	__fp_floor_adjust
	BCF	STATUS, C
	RRF	__fp_ma+3, F
	RRF	__fp_ma+2, F
	RRF	__fp_ma+1, F
	RRF	__fp_ma+0, F
	BTFSC	STATUS, C
	BSF	__fp_t, 0
	DECF	__fp_c, F
	MOVLW	HIGH(__fp_floor_drop)
	MOVWF	PCLATH
	GOTO	__fp_floor_drop

__fp_floor_adjust:
	MOVLW	HIGH(__fp_floor_back)
	MOVWF	PCLATH
	BTFSS	__fp_sg, 0
	GOTO	__fp_floor_back		; positive: truncation already floors
	MOVF	__fp_t, F
	MOVLW	HIGH(__fp_floor_back)
	MOVWF	PCLATH
	BTFSC	STATUS, 2
	GOTO	__fp_floor_back		; nothing was dropped
	INCF	__fp_ma+0, F
	MOVF	__fp_ma+0, F
	MOVLW	HIGH(__fp_floor_back)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_floor_back
	INCF	__fp_ma+1, F
	MOVF	__fp_ma+1, F
	MOVLW	HIGH(__fp_floor_back)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_floor_back
	INCF	__fp_ma+2, F
	MOVF	__fp_ma+2, F
	MOVLW	HIGH(__fp_floor_back)
	MOVWF	PCLATH
	BTFSS	STATUS, 2
	GOTO	__fp_floor_back
	INCF	__fp_ma+3, F

__fp_floor_back:
	MOVF	__fp_st, W
	MOVWF	__fp_c
__fp_floor_up:
	MOVF	__fp_c, F
	MOVLW	HIGH(__fp_floor_scale)
	MOVWF	PCLATH
	BTFSC	STATUS, 2
	GOTO	__fp_floor_scale
	BCF	STATUS, C
	RLF	__fp_ma+0, F
	RLF	__fp_ma+1, F
	RLF	__fp_ma+2, F
	RLF	__fp_ma+3, F
	DECF	__fp_c, F
	MOVLW	HIGH(__fp_floor_up)
	MOVWF	PCLATH
	GOTO	__fp_floor_up

__fp_floor_scale:
	MOVLW	0x03		; hand __fp_round the 3 guard bits it expects
	MOVWF	__fp_c
__fp_floor_guard:
	BCF	STATUS, C
	RLF	__fp_ma+0, F
	RLF	__fp_ma+1, F
	RLF	__fp_ma+2, F
	RLF	__fp_ma+3, F
	MOVLW	HIGH(__fp_floor_guard)
	MOVWF	PCLATH
	DECFSZ	__fp_c, F
	GOTO	__fp_floor_guard
	MOVLW	HIGH(__fp_round)
	MOVWF	PCLATH
	GOTO	__fp_round


; --- __fp_mul8: __fp_ph:__fp_pl = __fp_ua * __fp_ub ---------------------------
; 8x8 -> 16 shift-add. Runs in the float bank like every other routine here;
; callers reach it with PCLATH already set by the emitted page select.
__fp_mul8:
	CLRF	__fp_pl
	CLRF	__fp_ph
	MOVLW	0x08
	MOVWF	__fp_uc
__fp_mul8_loop:
	RRF	__fp_ub, F			; C = next multiplier bit
	MOVLW	HIGH(__fp_mul8_skip)
	MOVWF	PCLATH
	BTFSS	STATUS, 0
	GOTO	__fp_mul8_skip
	MOVF	__fp_ua, W
	ADDWF	__fp_ph, F			; partial add, C = carry out
__fp_mul8_skip:
	RRF	__fp_ph, F			; C -> ph7, ph0 -> C
	RRF	__fp_pl, F
	MOVLW	HIGH(__fp_mul8_loop)
	MOVWF	PCLATH
	DECFSZ	__fp_uc, F
	GOTO	__fp_mul8_loop
	RETURN

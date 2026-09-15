; -----------------------------------------------------------------------------
; PyMCU AVR Math Runtime -- unsigned 32-bit modulo
; -----------------------------------------------------------------------------
;
; Split out of div32.S (PyMCU/PyMCU#408) so that a program calling only __div32
; does not carry __mod32's own wrapper too. The build driver splices whole
; files and the linker cannot drop what it never saw as a separate section, so
; the two used to be paid for together whichever one a program actually
; called -- the same shape div32s.S's header already documents for the signed
; entry points.
;
; Depends on __div32_core, which lives in div32.S: the driver pulls both files
; in whenever __mod32 is referenced.
; -----------------------------------------------------------------------------

; __mod32: Unsigned 32-bit Modulo
;   Input:  R23:R22:R25:R24 (Dividend), R21:R20:R19:R18 (Divisor)
;   Output: R23:R22:R25:R24 (Remainder)
;   Clobbers: R26:R27:R30:R31 (X, Z -- intermediate quotient), R16 (loop counter)
; Nothing is PUSH/POP'd: see div32.S's header for why X, Z and R16 need no save.
__mod32:
    clr r26
    clr r27
    clr r30
    clr r31
    ldi r16, 33
    rcall __div32_core
    mov r24, r26        ; Move remainder to result registers
    mov r25, r27
    mov r22, r30
    mov r23, r31
    ret

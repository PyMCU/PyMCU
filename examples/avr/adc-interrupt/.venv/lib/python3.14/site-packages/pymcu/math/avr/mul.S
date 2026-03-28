; -----------------------------------------------------------------------------
; Whipsnake AVR Math Runtime
; -----------------------------------------------------------------------------
;
; __mul8: Unsigned 8-bit Multiplication
;   Input:  R24 (Multiplicand), R18 (Multiplier)
;   Output: R24 (Result Low), R25 (Result High)
;   Clobbers: R0, R1
;
; -----------------------------------------------------------------------------

__mul8:
    mul r24, r18
    mov r24, r0
    mov r25, r1
    clr r1          ; Restore zero register
    ret

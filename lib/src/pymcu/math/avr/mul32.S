; -----------------------------------------------------------------------------
; PyMCU AVR Math Runtime
; -----------------------------------------------------------------------------
;
; __mul32: Unsigned 32-bit Multiplication (low 32 bits of the product)
;   Input:  R23:R22:R25:R24 (a, MSB..LSB), R21:R20:R19:R18 (b, MSB..LSB)
;   Output: R23:R22:R25:R24 (low 32 bits of a*b)
;
; Byte positions (LSB first):
;   a: a0=R24, a1=R25, a2=R22, a3=R23
;   b: b0=R18, b1=R19, b2=R20, b3=R21
;
; Schoolbook 8x8->16 (MUL) partial products summed into r4:r5:r6:r7, keeping only the
; low four bytes (products that land entirely above byte 3 are dropped). MUL clobbers R1,
; so carry propagation uses a dedicated zero register R8 (cleared once) rather than R1.
; R4-R8 are saved/restored; R0/R1 are scratch (R1 restored to 0).
; -----------------------------------------------------------------------------

__mul32:
    push r4
    push r5
    push r6
    push r7
    push r8
    clr r4
    clr r5
    clr r6
    clr r7
    clr r8                 ; zero register for carry-only adds (R1 is busy with MUL)

    ; offset 0 -> bytes 0,1 (carry to 2,3)
    mul r24, r18           ; a0*b0
    add r4, r0
    adc r5, r1
    adc r6, r8
    adc r7, r8

    ; offset 1 -> bytes 1,2 (carry to 3)
    mul r24, r19           ; a0*b1
    add r5, r0
    adc r6, r1
    adc r7, r8
    mul r25, r18           ; a1*b0
    add r5, r0
    adc r6, r1
    adc r7, r8

    ; offset 2 -> bytes 2,3
    mul r24, r20           ; a0*b2
    add r6, r0
    adc r7, r1
    mul r25, r19           ; a1*b1
    add r6, r0
    adc r7, r1
    mul r22, r18           ; a2*b0
    add r6, r0
    adc r7, r1

    ; offset 3 -> byte 3 (low half only; high half overflows 32 bits)
    mul r24, r21           ; a0*b3
    add r7, r0
    mul r25, r20           ; a1*b2
    add r7, r0
    mul r22, r19           ; a2*b1
    add r7, r0
    mul r23, r18           ; a3*b0
    add r7, r0

    clr r1                 ; restore the zero register
    mov r24, r4            ; result -> output registers
    mov r25, r5
    mov r22, r6
    mov r23, r7
    pop r8
    pop r7
    pop r6
    pop r5
    pop r4
    ret

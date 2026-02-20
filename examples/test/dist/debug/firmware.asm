	LIST P=pic16f877a
#include <p16f877a.inc>

; --- Compiled Stack (Overlays) ---
	CBLOCK 0x20
_stack_base: 18
	ENDC

; --- Variable Offsets ---
delay_soft.count EQU _stack_base + 11
delay_soft.inner EQU _stack_base + 13
main.CCPR1L EQU _stack_base + 2
main.PR2 EQU _stack_base + 3
main.duty EQU _stack_base + 4
main.going_up EQU _stack_base + 6
tmp.0 EQU _stack_base + 15
tmp.1 EQU _stack_base + 17
tmp.2 EQU _stack_base + 7
tmp.3 EQU _stack_base + 9

; --- Reset Vector ---
	ORG 0x0000
	GOTO	main

; --- Interrupt Vector ---
	ORG 0x0004
__interrupt:
	RETFIE
delay_soft:
; main.py:8:     while count > 0:
L.0:
	BCF	STATUS, 5
	BCF	STATUS, 6
	MOVF	delay_soft.count, W
	SUBLW	0x00
	BTFSC	STATUS, 0
	GOTO	L.1
	MOVLW	0x00
	MOVWF	delay_soft.inner
	MOVWF	delay_soft.inner+1
; main.py:11:         while inner < 50:
L.2:
	BCF	STATUS, 5
	BCF	STATUS, 6
	MOVF	delay_soft.inner, W
	SUBLW	0x31
	BTFSS	STATUS, 0
	GOTO	L.3
; main.py:12:             inner = inner + 1
	MOVF	delay_soft.inner, W
	MOVWF	tmp.0
	MOVF	delay_soft.inner+1, W
	MOVWF	tmp.0+1
	MOVLW	0x01
	ADDWF	tmp.0, F
	BTFSC	STATUS, 0
	INCF	tmp.0+1, F
	MOVLW	0x00
	ADDWF	tmp.0+1, F
	MOVF	tmp.0, W
	MOVWF	delay_soft.inner
	MOVF	tmp.0+1, W
	MOVWF	delay_soft.inner+1
	GOTO	L.2
L.3:
; main.py:13:         count = count - 1
	BCF	STATUS, 5
	BCF	STATUS, 6
	DECF	delay_soft.count, F
	MOVF	tmp.1+1, W
	MOVWF	delay_soft.count+1
	GOTO	L.0
L.1:
	RETURN
main:
; main.py:19:     TRISC[RC2] = 0
	BSF	STATUS, 5
	BCF	STATUS, 6
	BCF	0x87, 2
; main.py:20:     TRISA[RA0] = 1
	MOVLW	0x0F
	IORWF	0x85, F
; main.py:13:         count = count - 1
	BSF	0x85, 3
; main.py:39:     T2CON[T2CKPS1] = 1
; main.py:45:     going_up: bool = True
; main.py:26:     ra3.high()  # Set RA3 high (pull-up) to detect button press (active low)
	BCF	STATUS, 5
	BSF	0x05, 3
; main.py:30:     PR2 = 0xFF
	MOVLW	0xFF
	MOVWF	main.PR2
; main.py:34:     CCP1CON[CCP1M3] = 1
	BSF	0x17, 3
; main.py:35:     CCP1CON[CCP1M2] = 1
	BSF	0x17, 2
; main.py:39:     T2CON[T2CKPS1] = 1
	BSF	0x12, 1
; main.py:41:     T2CON[TMR2ON] = 1
	BSF	0x12, 2
	MOVLW	0x00
	MOVWF	main.duty
	MOVWF	main.duty+1
	MOVLW	0x01
	MOVWF	main.going_up
; main.py:47:     while True:
L.76:
; main.py:50:         CCPR1L = duty
	BCF	STATUS, 5
	BCF	STATUS, 6
	MOVF	main.duty, W
	MOVWF	main.CCPR1L
; main.py:53:         if going_up:
	MOVF	main.going_up, W
	BTFSC	STATUS, 2
	GOTO	L.79
; main.py:54:             duty = duty + 1
	MOVF	main.duty, W
	MOVWF	tmp.2
	MOVF	main.duty+1, W
	MOVWF	tmp.2+1
	MOVLW	0x01
	ADDWF	tmp.2, F
	BTFSC	STATUS, 0
	INCF	tmp.2+1, F
	MOVLW	0x00
	ADDWF	tmp.2+1, F
	MOVF	tmp.2, W
	MOVWF	main.duty
	MOVF	tmp.2+1, W
	MOVWF	main.duty+1
; main.py:55:             if duty >= 250:
	MOVF	main.duty, W
	SUBLW	0xF9
	BTFSC	STATUS, 0
	GOTO	L.80
; main.py:56:                 going_up = False
	MOVLW	0x00
	MOVWF	main.going_up
L.80:
	GOTO	L.78
L.79:
; main.py:58:             duty = duty - 1
	BCF	STATUS, 5
	BCF	STATUS, 6
	MOVF	main.duty, W
	MOVWF	tmp.3
	MOVF	main.duty+1, W
	MOVWF	tmp.3+1
	MOVLW	0x01
	SUBWF	tmp.3, F
	BTFSS	STATUS, 0
	DECF	tmp.3+1, F
	MOVLW	0x00
	SUBWF	tmp.3+1, F
	MOVF	tmp.3, W
	MOVWF	main.duty
	MOVF	tmp.3+1, W
	MOVWF	main.duty+1
; main.py:59:             if duty <= 0:
	MOVF	main.duty, W
	SUBLW	0x00
	BTFSS	STATUS, 0
	GOTO	L.81
; main.py:60:                 going_up = True
	MOVLW	0x01
	MOVWF	main.going_up
L.81:
L.78:
; main.py:63:         delay_soft(2)
	MOVLW	0x02
	BCF	STATUS, 5
	BCF	STATUS, 6
	MOVWF	delay_soft.count
	MOVLW	0x00
	MOVWF	delay_soft.count+1
	CALL	delay_soft
	GOTO	L.76
	END

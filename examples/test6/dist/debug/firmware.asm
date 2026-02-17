	LIST P=pic16f877a
#include <p16f877a.inc>

; --- Reset Vector ---
	ORG 0x0000
	GOTO	main

; --- Interrupt Vector ---
	ORG 0x0004
__interrupt:
	RETFIE
main:
; main.py:7:     led = Pin("RA0", Pin.OUTPUT)
; main.py:10:         led.high()
	BSF	STATUS, 5
	BCF	STATUS, 6
	BCF	0x85, 0
; main.py:9:     while True:
L.6:
	BCF	STATUS, 5
	BCF	STATUS, 6
	BSF	0x05, 0
; Delay 500 ms
__dly_c1 EQU 0x20
__dly_c2 EQU 0x21
__dly_c3 EQU 0x22
	MOVLW	0x02
	MOVWF	__dly_c3
	CLRF	__dly_c2
	CLRF	__dly_c1
dly3_0:
	DECFSZ	__dly_c1, F
	GOTO	dly3_0
	DECFSZ	__dly_c2, F
	GOTO	dly3_0
	DECFSZ	__dly_c3, F
	GOTO	dly3_0
	MOVLW	0x89
	MOVWF	__dly_c2
	MOVLW	0xFF
	MOVWF	__dly_c1
	INCF	__dly_c1, F
dly2_1:
	DECFSZ	__dly_c1, F
	GOTO	dly2_1
	DECFSZ	__dly_c2, F
	GOTO	dly2_1
	MOVLW	0x59
	MOVWF	__dly_c1
dly1_2:
	DECFSZ	__dly_c1, F
	GOTO	dly1_2
	NOP
	NOP
	MOVLW	0x55
	MOVWF	__dly_c1
dly1_3:
	DECFSZ	__dly_c1, F
	GOTO	dly1_3
	BCF	STATUS, 5
	BCF	STATUS, 6
	BCF	0x05, 0
; Delay 500 ms
	MOVLW	0x02
	MOVWF	__dly_c3
	CLRF	__dly_c2
	CLRF	__dly_c1
dly3_4:
	DECFSZ	__dly_c1, F
	GOTO	dly3_4
	DECFSZ	__dly_c2, F
	GOTO	dly3_4
	DECFSZ	__dly_c3, F
	GOTO	dly3_4
	MOVLW	0x89
	MOVWF	__dly_c2
	MOVLW	0xFF
	MOVWF	__dly_c1
	INCF	__dly_c1, F
dly2_5:
	DECFSZ	__dly_c1, F
	GOTO	dly2_5
	DECFSZ	__dly_c2, F
	GOTO	dly2_5
	MOVLW	0x59
	MOVWF	__dly_c1
dly1_6:
	DECFSZ	__dly_c1, F
	GOTO	dly1_6
	NOP
	NOP
	MOVLW	0x55
	MOVWF	__dly_c1
dly1_7:
	DECFSZ	__dly_c1, F
	GOTO	dly1_7
	GOTO	L.6
	END

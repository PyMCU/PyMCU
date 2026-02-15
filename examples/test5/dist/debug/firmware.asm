; --- Compiled Stack (Overlays) ---
	CBLOCK 0x20
_stack_base: 6
	ENDC

; --- Variable Offsets ---
main.state EQU _stack_base + 0
nco_init.inc_val EQU _stack_base + 3
tmp.0 EQU _stack_base + 4
tmp.1 EQU _stack_base + 5
tmp.10 EQU _stack_base + 2
tmp.11 EQU _stack_base + 2
tmp.12 EQU _stack_base + 2
tmp.13 EQU _stack_base + 2
tmp.14 EQU _stack_base + 2
tmp.15 EQU _stack_base + 2
tmp.2 EQU _stack_base + 3
tmp.3 EQU _stack_base + 3
tmp.4 EQU _stack_base + 2
tmp.5 EQU _stack_base + 2
tmp.6 EQU _stack_base + 2
tmp.7 EQU _stack_base + 2
tmp.8 EQU _stack_base + 2
tmp.9 EQU _stack_base + 2
uart_write.data EQU _stack_base + 3


	LIST P=pic16f18877
#include <p16f18877.inc>
	errorlevel -302
	ORG 0x04
__interrupt:
	RETFIE
nco_init:
; Line 6: 
	MOVLW	0x00
	MOVLB	11
	MOVWF	0x592
; Line 10: NOTE_G4 = 49152
	MOVLW	0x01
	MOVWF	0x593
; Line 15: 
	MOVLB	0
	MOVF	nco_init.inc_val, W
	ANDLW	0xFF
	MOVWF	tmp.0
	MOVF	tmp.0, W
	MOVLB	11
	MOVWF	0x58F
	MOVLB	0
	MOVF	nco_init.inc_val, W
	MOVWF	tmp.1
	MOVF	tmp.1, W
	MOVLB	11
	MOVWF	0x590
; Line 19: 
	MOVLW	0x81
	MOVWF	0x592
	MOVLW	0x00
	RETURN
pymcu.time_delay_ms:
	MOVLW	0x00
	RETURN
pymcu.time_delay_us:
	MOVLW	0x00
	RETURN
uart_init:
	CALL	unlock_pps
	MOVLB	0
	MOVWF	tmp.2
; Line 30:     TRISA[0] = 0 # RA0 Output
	MOVLW	0x10
	MOVLB	62
	MOVWF	0x1F26
; Line 34:     while True:
	MOVLW	0x17
	MOVLB	61
	MOVWF	0x1ECB
	CALL	lock_pps
	MOVLB	0
	MOVWF	tmp.3
; Line 40:             uart.write(0x45) # 'E'
	BCF	0x13, 6
; Line 41:             state = 1
	BSF	0x13, 7
; Line 43:             nco.init(NOTE_G4)
	MOVLW	0x19
	MOVLB	2
	MOVWF	0x11B
	MOVLW	0x00
	MOVWF	0x11C
; Line 44:             uart.write(0x47) # 'G'
	MOVLW	0x24
	MOVWF	0x11E
; Line 45:             state = 2
	MOVLW	0x90
	MOVWF	0x11D
	MOVLW	0x00
	RETURN
uart_write:
L.0:
	MOVLB	14
	BTFSC	0x70F, 5
	GOTO	L.1
	GOTO	L.0
L.1:
	MOVLB	0
	MOVF	uart_write.data, W
	MOVLB	2
	MOVWF	0x11A
	MOVLW	0x00
	RETURN
main:
	CALL	uart_init
	MOVLB	0
	MOVWF	tmp.4
	MOVLW	0x48
	MOVWF	uart_write.data
	CALL	uart_write
	MOVWF	tmp.5
	MOVLW	0x69
	MOVWF	uart_write.data
	CALL	uart_write
	MOVWF	tmp.6
	MOVLW	0x00
	MOVWF	nco_init.inc_val
	CALL	nco_init
	MOVWF	tmp.7
	CALL	uart_unlock_pps
	MOVWF	tmp.8
; Line 27:     RA0PPS.value = 0x18 # 0x18 = NCO1 Output
	MOVLW	0x18
	MOVLB	62
	MOVWF	0x1F10
	CALL	uart_lock_pps
	MOVLB	0
	MOVWF	tmp.9
; Line 30:     TRISA[0] = 0 # RA0 Output
	BCF	0x11, 0
; Line 32:     state = 0
	MOVLW	0x00
	MOVWF	main.state
; Line 34:     while True:
L.2:
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
; Line 38:         if state == 0:
	MOVF	main.state, W
	XORLW	0x00
	BTFSS	STATUS, 2
	GOTO	L.5
	MOVLW	0xF0
	MOVWF	nco_init.inc_val
	CALL	nco_init
	MOVWF	tmp.10
	MOVLW	0x45
	MOVWF	uart_write.data
	CALL	uart_write
	MOVWF	tmp.11
; Line 41:             state = 1
	MOVLW	0x01
	MOVWF	main.state
	GOTO	L.4
L.5:
	MOVF	main.state, W
	XORLW	0x01
	BTFSS	STATUS, 2
	GOTO	L.6
	MOVLW	0x00
	MOVWF	nco_init.inc_val
	CALL	nco_init
	MOVWF	tmp.12
	MOVLW	0x47
	MOVWF	uart_write.data
	CALL	uart_write
	MOVWF	tmp.13
; Line 45:             state = 2
	MOVLW	0x02
	MOVWF	main.state
	GOTO	L.4
L.6:
	MOVLW	0x00
	MOVWF	nco_init.inc_val
	CALL	nco_init
	MOVWF	tmp.14
	MOVLW	0x43
	MOVWF	uart_write.data
	CALL	uart_write
	MOVWF	tmp.15
; Line 49:             state = 0
	MOVLW	0x00
	MOVWF	main.state
L.4:
	GOTO	L.2
L.3:
	MOVLW	0x00
	RETURN
	END

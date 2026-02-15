; --- Compiled Stack (Overlays) ---
	CBLOCK 0x20
_stack_base: 8
	ENDC

; --- Variable Offsets ---
adc_val_high EQU _stack_base + 0
adc_val_low EQU _stack_base + 1
eeprom_addr EQU _stack_base + 2
inline1.eeprom_write_byte.addr EQU _stack_base + 4
inline1.eeprom_write_byte.data EQU _stack_base + 5
inline1.uart_write.byte_val EQU _stack_base + 6
main.counter EQU _stack_base + 7
tx_data EQU _stack_base + 3

	ORG 0x04
__interrupt:
; Context Save
	MOVWF	W_TEMP
	SWAPF	STATUS, W
	MOVWF	STATUS_TEMP
	BCF	STATUS, 5
	BCF	STATUS, 6
; Line 14:     if PIR1[5]:
	BCF	STATUS, 5
	BCF	STATUS, 6
	BTFSS	0x0C, 5
	GOTO	L.0
; Line 15:         global tx_data
; Line 16:         tx_data = RCREG.value
	MOVF	0x1A, W
	MOVWF	tx_data
L.0:
; Context Restore
	SWAPF	STATUS_TEMP, W
	MOVWF	STATUS
	SWAPF	W_TEMP, F
	SWAPF	W_TEMP, W
	RETFIE
main:
; Line 89:     TRISB[STATUS_LED] = 0
	BSF	STATUS, 5
	BCF	STATUS, 6
	BCF	0x86, 0
; Line 21:     TRISC[6] = 0  # TX output
	BCF	0x87, 6
; Line 22:     TRISC[7] = 1  # RX input
	BSF	0x87, 7
; Line 23:     SPBRG.value = BAUD_RATE_CONST
	MOVLW	0x19
	MOVWF	0x99
; Line 24:     TXSTA.value = 0x24  # TXEN, BRGH
	MOVLW	0x24
	MOVWF	0x98
; Line 25:     RCSTA.value = 0x90  # SPEN, CREN
	MOVLW	0x90
	BCF	STATUS, 5
	BCF	STATUS, 6
	MOVWF	0x18
L.7:
; Line 37:     ADCON1.value = 0x0E
	MOVLW	0x0E
	BSF	STATUS, 5
	BCF	STATUS, 6
	MOVWF	0x9F
; Line 38:     ADCON0.value = 0x41
	MOVLW	0x41
	BCF	STATUS, 5
	BCF	STATUS, 6
	MOVWF	0x1F
L.8:
; Line 95:     INTCON[6] = 1
	BCF	STATUS, 5
	BCF	STATUS, 6
	BSF	0x0B, 6
; Line 96:     INTCON[7] = 1
	BSF	0x0B, 7
; Line 98:     counter = 0
	MOVLW	0x00
	MOVWF	main.counter
; Line 100:     while True:
L.9:
; Line 43:     global adc_val_high
; Line 45:     ADCON0[2] = 1
	BCF	STATUS, 5
	BCF	STATUS, 6
	BSF	0x1F, 2
; Line 47:     while ADCON0[2]:
L.12:
	BCF	STATUS, 5
	BCF	STATUS, 6
	BTFSS	0x1F, 2
	GOTO	L.13
	GOTO	L.12
L.13:
; Line 50:     adc_val_high = ptr(0x1E).value
	BCF	STATUS, 5
	BCF	STATUS, 6
	MOVF	0x1E, W
	MOVWF	adc_val_high
L.11:
; Line 105:         if adc_val_high > 200:
	BCF	STATUS, 5
	BCF	STATUS, 6
	MOVF	adc_val_high, W
	SUBLW	0xC8
	BTFSC	STATUS, 0
	GOTO	L.15
; Line 106:             PORTB[STATUS_LED] = 1  # Prender LED de alerta
	BSF	0x06, 0
	MOVLW	0x00
	MOVWF	inline1.eeprom_write_byte.addr
	MOVF	adc_val_high, W
	MOVWF	inline1.eeprom_write_byte.data
; Line 58:     while EECON1[1]:
L.17:
	BSF	STATUS, 5
	BSF	STATUS, 6
	BTFSS	0x18C, 1
	GOTO	L.18
	GOTO	L.17
L.18:
; Line 61:     EEADR.value = addr
	BCF	STATUS, 5
	BCF	STATUS, 6
	MOVF	inline1.eeprom_write_byte.addr, W
	BCF	STATUS, 5
	BSF	STATUS, 6
	MOVWF	0x10D
; Line 62:     EEDATA.value = data
	BCF	STATUS, 5
	BCF	STATUS, 6
	MOVF	inline1.eeprom_write_byte.data, W
	BCF	STATUS, 5
	BSF	STATUS, 6
	MOVWF	0x10C
; Line 64:     EECON1[7] = 0  # EEPGD (Access Data Memory)
	BSF	STATUS, 5
	BSF	STATUS, 6
	BCF	0x18C, 7
; Line 65:     EECON1[2] = 1  # WREN (Enable Write)
	BSF	0x18C, 2
; Line 68:     INTCON[7] = 0  # GIE Off
	BCF	STATUS, 5
	BCF	STATUS, 6
	BCF	0x0B, 7
; Line 71:     EECON2.value = 0x55
	MOVLW	0x55
	BSF	STATUS, 5
	BSF	STATUS, 6
	MOVWF	0x18D
; Line 72:     EECON2.value = 0xAA
	MOVLW	0xAA
	MOVWF	0x18D
; Line 75:     EECON1[1] = 1  # WR = 1
	BSF	0x18C, 1
; Line 77:     INTCON[7] = 1  # GIE On
	BCF	STATUS, 5
	BCF	STATUS, 6
	BSF	0x0B, 7
; Line 81:     EECON1[2] = 0
	BSF	STATUS, 5
	BSF	STATUS, 6
	BCF	0x18C, 2
L.16:
	MOVLW	0x21
	BCF	STATUS, 5
	BCF	STATUS, 6
	MOVWF	inline1.uart_write.byte_val
; Line 30:     while not TXSTA[1]:
L.20:
	BSF	STATUS, 5
	BCF	STATUS, 6
	BTFSC	0x98, 1
	GOTO	L.21
	GOTO	L.20
L.21:
; Line 32:     TXREG.value = byte_val
	BCF	STATUS, 5
	BCF	STATUS, 6
	MOVF	inline1.uart_write.byte_val, W
	MOVWF	0x19
L.19:
	GOTO	L.14
L.15:
; Line 114:             PORTB[STATUS_LED] = 0
	BCF	STATUS, 5
	BCF	STATUS, 6
	BCF	0x06, 0
L.14:
	BCF	STATUS, 5
	BCF	STATUS, 6
	MOVF	adc_val_high, W
	MOVWF	inline1.uart_write.byte_val
; Line 30:     while not TXSTA[1]:
L.23:
	BSF	STATUS, 5
	BCF	STATUS, 6
	BTFSC	0x98, 1
	GOTO	L.24
	GOTO	L.23
L.24:
; Line 32:     TXREG.value = byte_val
	BCF	STATUS, 5
	BCF	STATUS, 6
	MOVF	inline1.uart_write.byte_val, W
	MOVWF	0x19
L.22:
; Delay 100 ms
__dly_c1 EQU 0x20
__dly_c2 EQU 0x21
	MOVLW	0x81
	MOVWF	__dly_c2
	MOVLW	0xFF
	MOVWF	__dly_c1
	INCF	__dly_c1, F
dly2_0:
	DECFSZ	__dly_c1, F
	GOTO	dly2_0
	DECFSZ	__dly_c2, F
	GOTO	dly2_0
	MOVLW	0xDF
	MOVWF	__dly_c1
dly1_1:
	DECFSZ	__dly_c1, F
	GOTO	dly1_1
	GOTO	L.9
L.10:
	MOVLW	0x00
	RETURN
	END

	LIST P=16F877A
	#include <P16F877A.INC>
; --- Compiled Stack (Overlays) ---
	CBLOCK 0x20
_stack_base: 13
	ENDC

; --- Variable Offsets ---
crc_reg EQU _stack_base + 0
entropy_pool EQU _stack_base + 1
lfsr_reg EQU _stack_base + 2
main.input_val EQU _stack_base + 3
main.output EQU _stack_base + 4
main.val_to_hash EQU _stack_base + 5
mix_entropy.sensor_val EQU _stack_base + 8
mix_entropy.temp EQU _stack_base + 9
tmp.1 EQU _stack_base + 8
tmp.2 EQU _stack_base + 8
tmp.3 EQU _stack_base + 9
tmp.4 EQU _stack_base + 10
tmp.5 EQU _stack_base + 10
tmp.6 EQU _stack_base + 11
update_crc.data_byte EQU _stack_base + 11
update_crc.i EQU _stack_base + 12
update_lfsr.lsb EQU _stack_base + 9

; --- Code ---
	ORG 0x00
	GOTO	main
	ORG 0x04
__interrupt:
	RETFIE
update_lfsr:
	MOVF	lfsr_reg, W
	ANDLW	0x01
	MOVWF	update_lfsr.lsb
	MOVLW	0x01
	MOVWF	__tmp
	MOVF	__tmp, F
	BTFSC	STATUS, 2
	GOTO	augrs_done_1
augrs_0:
	BCF	STATUS, 0
	RRF	lfsr_reg, F
	DECFSZ	__tmp, F
	GOTO	augrs_0
augrs_done_1:
	MOVLW	0x01
	SUBWF	update_lfsr.lsb, W
	BTFSS	STATUS, 2
	GOTO	L.0
	MOVLW	0xB4
	XORWF	lfsr_reg, F
L.0:
	MOVLW	0x00
	RETURN
update_crc:
	MOVF	update_crc.data_byte, W
	XORWF	crc_reg, F
	MOVLW	0x00
	MOVWF	update_crc.i
L.1:
	MOVLW	0x08
	SUBWF	update_crc.i, W
	BTFSC	STATUS, 0
	GOTO	L.2
	MOVF	crc_reg, W
	ANDLW	0x80
	MOVWF	tmp.3
	MOVLW	0x80
	SUBWF	tmp.3, W
	BTFSS	STATUS, 2
	GOTO	L.4
	MOVLW	0x01
	MOVWF	__tmp
	MOVF	__tmp, F
	BTFSC	STATUS, 2
	GOTO	augls_done_3
augls_2:
	BCF	STATUS, 0
	RLF	crc_reg, F
	DECFSZ	__tmp, F
	GOTO	augls_2
augls_done_3:
	MOVLW	0x07
	XORWF	crc_reg, F
	GOTO	L.3
L.4:
	MOVLW	0x01
	MOVWF	__tmp
	MOVF	__tmp, F
	BTFSC	STATUS, 2
	GOTO	augls_done_5
augls_4:
	BCF	STATUS, 0
	RLF	crc_reg, F
	DECFSZ	__tmp, F
	GOTO	augls_4
augls_done_5:
L.3:
	MOVLW	0x01
	ADDWF	update_crc.i, F
	GOTO	L.1
L.2:
	MOVLW	0x00
	RETURN
mix_entropy:
	MOVF	entropy_pool, W
	MOVWF	mix_entropy.temp
	MOVF	mix_entropy.sensor_val, W
	ADDWF	mix_entropy.temp, F
	MOVF	entropy_pool, W
	SUBWF	mix_entropy.temp, W
	BTFSC	STATUS, 0
	GOTO	L.5
	MOVLW	0xFF
	MOVWF	mix_entropy.temp
L.5:
	MOVF	mix_entropy.temp, W
	MOVWF	entropy_pool
	MOVLW	0x0A
	SUBWF	entropy_pool, W
	CLRF	tmp.6
	BTFSS	STATUS, 0
	GOTO	L_GT_6
	BTFSC	STATUS, 2
	GOTO	L_GT_6
	INCF	tmp.6, F
L_GT_6:
	MOVF	tmp.6, W
	BTFSC	STATUS, 2
	GOTO	L.6
	MOVLW	0x0A
	SUBWF	entropy_pool, F
L.6:
	MOVLW	0x03
	IORWF	entropy_pool, F
	MOVLW	0x7F
	ANDWF	entropy_pool, F
	MOVLW	0x00
	RETURN
main:
	MOVLW	0xFF
	BSF	STATUS, 5
	BCF	STATUS, 6
	MOVWF	0x86
	MOVLW	0x00
	MOVWF	0x88
	MOVLW	0xFF
	MOVWF	crc_reg
	MOVLW	0x55
	MOVWF	lfsr_reg
L.7:
	BCF	STATUS, 5
	BCF	STATUS, 6
	MOVF	0x06, W
	MOVWF	main.input_val
	CALL	update_lfsr
	MOVF	main.input_val, W
	MOVWF	mix_entropy.sensor_val
	CALL	mix_entropy
	MOVF	entropy_pool, W
	XORWF	lfsr_reg, W
	MOVWF	main.val_to_hash
	MOVWF	update_crc.data_byte
	CALL	update_crc
	MOVF	crc_reg, W
	MOVWF	main.output
	MOVLW	0xFF
	XORWF	main.output, F
	MOVF	main.output, W
	MOVWF	0x08
	GOTO	L.7
	END

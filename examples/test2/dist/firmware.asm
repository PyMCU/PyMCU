	LIST P=16F877A
	#include <P16F877A.INC>
; --- Compiled Stack (Overlays) ---
	CBLOCK 0x20
_stack_base: 14
	ENDC

; --- Variable Offsets ---
main.inverse_speed EQU _stack_base + 0
main.mode EQU _stack_base + 1
main.sensor_val EQU _stack_base + 2
set_motor_speed.speed EQU _stack_base + 9
system_error_blink.count EQU _stack_base + 9
system_error_blink.d EQU _stack_base + 10
tmp.0 EQU _stack_base + 9
tmp.1 EQU _stack_base + 10
tmp.10 EQU _stack_base + 4
tmp.11 EQU _stack_base + 5
tmp.12 EQU _stack_base + 6
tmp.13 EQU _stack_base + 7
tmp.14 EQU _stack_base + 8
tmp.2 EQU _stack_base + 11
tmp.3 EQU _stack_base + 12
tmp.5 EQU _stack_base + 13

; --- Code ---
	ORG 0x00
	GOTO	main
	ORG 0x04
__interrupt:
	RETFIE
adc_init:
	MOVLW	0x0E
	BSF	STATUS, 5
	BCF	STATUS, 6
	MOVWF	0x9F
	MOVLW	0x81
	BCF	STATUS, 5
	MOVWF	0x1F
	MOVLW	0x00
	RETURN
pwm_init:
	MOVLW	0xFF
	BSF	STATUS, 5
	BCF	STATUS, 6
	MOVWF	0x92
	BCF	STATUS, 5
	BSF	0x12, 1
	BSF	0x12, 2
	BSF	0x17, 3
	BSF	0x17, 2
	BSF	STATUS, 5
	BCF	0x87, 2
	MOVLW	0x00
	RETURN
read_adc:
	BCF	STATUS, 5
	BCF	STATUS, 6
	BSF	0x1F, 2
L.0:
	CLRF	tmp.0
	BTFSC	0x1F, 2
	INCF	tmp.0, F
	MOVLW	0x01
	SUBWF	tmp.0, W
	BTFSS	STATUS, 2
	GOTO	L.1
	GOTO	L.0
L.1:
	MOVF	0x1E, W
	RETURN
set_motor_speed:
	MOVF	set_motor_speed.speed, W
	BCF	STATUS, 5
	BCF	STATUS, 6
	MOVWF	0x15
	MOVLW	0x00
	RETURN
system_error_blink:
	MOVLW	0x00
	MOVWF	system_error_blink.count
L.2:
	MOVLW	0x05
	SUBWF	system_error_blink.count, W
	BTFSC	STATUS, 0
	GOTO	L.3
	MOVLW	0xFF
	BCF	STATUS, 5
	BCF	STATUS, 6
	MOVWF	0x08
	MOVLW	0x00
	MOVWF	system_error_blink.d
L.4:
	MOVLW	0xC8
	SUBWF	system_error_blink.d, W
	BTFSC	STATUS, 0
	GOTO	L.5
	MOVF	system_error_blink.d, W
	ADDLW	0x01
	MOVWF	system_error_blink.d
	GOTO	L.4
L.5:
	MOVLW	0x00
	MOVWF	0x08
	MOVWF	system_error_blink.d
L.6:
	MOVLW	0xC8
	SUBWF	system_error_blink.d, W
	BTFSC	STATUS, 0
	GOTO	L.7
	MOVF	system_error_blink.d, W
	ADDLW	0x01
	MOVWF	system_error_blink.d
	GOTO	L.6
L.7:
	MOVF	system_error_blink.count, W
	ADDLW	0x01
	MOVWF	system_error_blink.count
	GOTO	L.2
L.3:
	MOVLW	0x00
	RETURN
main:
	BSF	STATUS, 5
	BCF	STATUS, 6
	BSF	0x85, 0
	MOVLW	0xFF
	MOVWF	0x86
	MOVLW	0x00
	MOVWF	0x88
	CALL	adc_init
	CALL	pwm_init
L.8:
	BCF	STATUS, 5
	BCF	STATUS, 6
	MOVF	0x06, W
	ANDLW	0x0F
	MOVWF	main.mode
	CALL	read_adc
	MOVWF	main.sensor_val
	MOVLW	0x00
	SUBWF	main.mode, W
	BTFSS	STATUS, 2
	GOTO	L.11
	MOVLW	0x00
	MOVWF	set_motor_speed.speed
	CALL	set_motor_speed
	MOVLW	0x01
	MOVWF	0x08
	GOTO	L.10
L.11:
	MOVLW	0x01
	SUBWF	main.mode, W
	BTFSS	STATUS, 2
	GOTO	L.12
	MOVF	main.sensor_val, W
	MOVWF	set_motor_speed.speed
	CALL	set_motor_speed
	MOVLW	0x02
	MOVWF	0x08
	GOTO	L.10
L.12:
	MOVLW	0x02
	SUBWF	main.mode, W
	BTFSS	STATUS, 2
	GOTO	L.13
	MOVLW	0x80
	SUBWF	main.sensor_val, W
	CLRF	tmp.13
	BTFSS	STATUS, 0
	GOTO	L_GT_0
	BTFSC	STATUS, 2
	GOTO	L_GT_0
	INCF	tmp.13, F
L_GT_0:
	MOVF	tmp.13, W
	BTFSC	STATUS, 2
	GOTO	L.15
	MOVLW	0xFF
	MOVWF	set_motor_speed.speed
	CALL	set_motor_speed
	MOVLW	0x04
	MOVWF	0x08
	GOTO	L.14
L.15:
	MOVLW	0x00
	MOVWF	set_motor_speed.speed
	CALL	set_motor_speed
	MOVLW	0x08
	MOVWF	0x08
L.14:
	GOTO	L.10
L.13:
	MOVLW	0x03
	SUBWF	main.mode, W
	BTFSS	STATUS, 2
	GOTO	L.16
	MOVF	main.sensor_val, W
	SUBLW	0xFF
	MOVWF	main.inverse_speed
	MOVWF	set_motor_speed.speed
	CALL	set_motor_speed
	MOVLW	0x10
	MOVWF	0x08
	GOTO	L.10
L.16:
	MOVLW	0x00
	MOVWF	set_motor_speed.speed
	CALL	set_motor_speed
	CALL	system_error_blink
L.10:
	GOTO	L.8
	END

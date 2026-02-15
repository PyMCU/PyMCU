	LIST P=18F45K50
	#include <p18f45k50.inc>
_stack_base EQU 0x060
current_duty EQU _stack_base + 0x000
main.last_tick EQU _stack_base + 0x003
main.state EQU _stack_base + 0x004
pwm_direction EQU _stack_base + 0x001
system_tick EQU _stack_base + 0x002
tmp.1 EQU _stack_base + 0x009
tmp.2 EQU _stack_base + 0x00A
tmp.3 EQU _stack_base + 0x00B
tmp.4 EQU _stack_base + 0x00C
tmp.5 EQU _stack_base + 0x00D
tmp.6 EQU _stack_base + 0x005
tmp.7 EQU _stack_base + 0x006
tmp.8 EQU _stack_base + 0x007
tmp.9 EQU _stack_base + 0x008
tmp.0 EQU 0x060
    ORG 0x0000
	GOTO	main
isr_handler:
; Line 12:     if PIR1[0]:
	BTFSS	0x0C, 0, ACCESS
	BRA	L.0
; Line 13:         PIR1[0] = 0
; Line 15:         TMR1.value = 0xFC18
; Line 17:         global system_tick
; Line 18:         system_tick = system_tick + 1
L.0:
	RETURN
setup_pwm:
; Line 23:     TRISC[2] = 0
	MOVLB	0
	BCF	0x87, 2, BANKED
; Line 24:     PR2.value = 0xFF
	MOVLW	0xFF
	MOVWF	0x92, BANKED
; Line 25:     T2CON.value = 0x04
	MOVLW	0x04
	MOVWF	0x12, ACCESS
; Line 26:     CCP1CON.value = 0x0C
	MOVLW	0x0C
	MOVWF	0x17, ACCESS
; Line 27:     CCPR1L.value = 0
	MOVLW	0x00
	MOVWF	0x15, ACCESS
	RETURN
setup_timer1:
; Line 32:     T1CON.value = 0x31
	MOVLW	0x31
	MOVWF	0x10, ACCESS
; Line 33:     TMR1.value = 0xFC18
	MOVLW	0x18
	MOVWF	0x0E, ACCESS
	RETURN
enable_interrupts:
; Line 38:     PIE1[0] = 1
	MOVLB	0
	BSF	0x8C, 0, BANKED
; Line 39:     INTCON[6] = 1
	BSF	0x0B, 6, ACCESS
; Line 40:     INTCON[7] = 1
	BSF	0x0B, 7, ACCESS
	RETURN
update_breathing:
; Line 44:     global current_duty, pwm_direction
; Line 46:     if pwm_direction == 0:
	MOVLB	0
	MOVF	pwm_direction, W, BANKED
	MOVF	pwm_direction, F, BANKED
	CLRF	WREG, ACCESS
	BTFSC	STATUS, Z, ACCESS
	MOVLW	1
comp_false_0:
	MOVLB	0
	MOVWF	tmp.1, BANKED
	MOVF	tmp.1, W, BANKED
	ANDLW	0xFF
	BZ	L.2
; Line 48:         current_duty = current_duty + 5
	MOVF	current_duty, W, BANKED
	ADDLW	0x05
	MOVWF	tmp.2, BANKED
	MOVFF	tmp.2, current_duty
; Line 49:         if current_duty > 250:
	MOVF	current_duty, W, BANKED
	MOVLW	0xFA
	SUBWF	current_duty, W, BANKED
	CLRF	WREG, ACCESS
	BTFSS	STATUS, C, ACCESS
	BRA	comp_false_2
comp_false_2:
	MOVLB	0
	MOVWF	tmp.3, BANKED
	MOVF	tmp.3, W, BANKED
	ANDLW	0xFF
	BZ	L.3
; Line 50:             pwm_direction = 1
	MOVLW	0x01
	MOVWF	pwm_direction, BANKED
L.3:
	BRA	L.1
L.2:
; Line 53:         current_duty = current_duty - 5
	MOVLB	0
	MOVF	current_duty, W, BANKED
	ADDLW	(0x100 - (0x05)) & 0xFF
	MOVWF	tmp.4, BANKED
	MOVWF	tmp.4, BANKED
	MOVFF	tmp.4, current_duty
; Line 54:         if current_duty < 5:
	MOVF	current_duty, W, BANKED
	MOVLW	0x05
	SUBWF	current_duty, W, BANKED
	CLRF	WREG, ACCESS
	BTFSS	STATUS, C, ACCESS
	MOVLW	1
comp_false_4:
	MOVLB	0
	MOVWF	tmp.5, BANKED
	MOVF	tmp.5, W, BANKED
	ANDLW	0xFF
	BZ	L.4
; Line 55:             pwm_direction = 0
	MOVLW	0x00
	MOVWF	pwm_direction, BANKED
L.4:
L.1:
; Line 58:     CCPR1L.value = current_duty
	MOVFF	current_duty, 0x15
	RETURN
main:
; Line 23:     TRISC[2] = 0
	MOVLB	0
	BCF	0x87, 2, BANKED
; Line 24:     PR2.value = 0xFF
	MOVLW	0xFF
	MOVWF	0x92, BANKED
; Line 25:     T2CON.value = 0x04
	MOVLW	0x04
	MOVWF	0x12, ACCESS
; Line 26:     CCP1CON.value = 0x0C
	MOVLW	0x0C
	MOVWF	0x17, ACCESS
; Line 27:     CCPR1L.value = 0
	MOVLW	0x00
	MOVWF	0x15, ACCESS
L.5:
; Line 32:     T1CON.value = 0x31
	MOVLW	0x31
	MOVWF	0x10, ACCESS
; Line 33:     TMR1.value = 0xFC18
	MOVLW	0x18
	MOVWF	0x0E, ACCESS
L.6:
; Line 38:     PIE1[0] = 1
	MOVLB	0
	BSF	0x8C, 0, BANKED
; Line 39:     INTCON[6] = 1
	BSF	0x0B, 6, ACCESS
; Line 40:     INTCON[7] = 1
	BSF	0x0B, 7, ACCESS
L.7:
; Line 65:     last_tick = 0
	MOVLW	0x00
	MOVLB	0
	MOVWF	main.last_tick, BANKED
; Line 67:     state = 0
	MOVLW	0x00
	MOVWF	main.state, BANKED
; Line 69:     while True:
L.8:
	MOVLW	0x01
	ANDLW	0xFF
	BZ	L.9
; Line 70:         if system_tick != last_tick:
	MOVLB	0
	MOVF	system_tick, W, BANKED
	MOVF	main.last_tick, W, BANKED
	SUBWF	system_tick, W, BANKED
	CLRF	WREG, ACCESS
	BTFSS	STATUS, Z, ACCESS
	MOVLW	1
comp_false_6:
	MOVLB	0
	MOVWF	tmp.6, BANKED
	MOVF	tmp.6, W, BANKED
	ANDLW	0xFF
	BZ	L.10
; Line 71:             last_tick = system_tick
	MOVFF	system_tick, main.last_tick
	MOVF	main.state, W, BANKED
	MOVF	main.state, F, BANKED
	CLRF	WREG, ACCESS
	BTFSC	STATUS, Z, ACCESS
	MOVLW	1
comp_false_8:
	MOVLB	0
	MOVWF	tmp.7, BANKED
	MOVF	tmp.7, W, BANKED
	ANDLW	0xFF
	BZ	L.12
	CALL	update_breathing
; Line 77:                     if not PORTB[0]:
	BTFSC	0x06, 0, ACCESS
	BRA	L.13
; Line 78:                         state = 1
L.13:
	BRA	L.11
L.12:
	MOVLB	0
	MOVF	main.state, W, BANKED
	MOVLW	0x01
	SUBWF	main.state, W, BANKED
	CLRF	WREG, ACCESS
	BTFSC	STATUS, Z, ACCESS
	MOVLW	1
comp_false_10:
	MOVLB	0
	MOVWF	tmp.8, BANKED
	MOVF	tmp.8, W, BANKED
	ANDLW	0xFF
	BZ	L.14
; Line 81:                     if system_tick & 1:
	MOVF	system_tick, W, BANKED
	ANDLW	0x01
	MOVWF	tmp.9, BANKED
	MOVF	tmp.9, W, BANKED
	ANDLW	0xFF
	BZ	L.16
; Line 82:                         CCPR1L.value = 255
	MOVLW	0xFF
	MOVWF	0x15, ACCESS
	BRA	L.15
L.16:
; Line 84:                         CCPR1L.value = 0
	MOVLW	0x00
	MOVWF	0x15, ACCESS
L.15:
; Line 86:                     if PORTB[0]:
	BTFSS	0x06, 0, ACCESS
	BRA	L.17
; Line 87:                         state = 0
L.17:
	BRA	L.11
L.14:
L.11:
L.10:
	BRA	L.8
L.9:
	RETURN
	END

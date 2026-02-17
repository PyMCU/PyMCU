	LIST P=pic16f18877
#include <p16f18877.inc>
	errorlevel -302

; --- Compiled Stack (Overlays) ---
	CBLOCK 0x20
_stack_base: 17
	ENDC

; --- Variable Offsets ---
button_pressed EQU _stack_base + 0
chase_animation.direction EQU _stack_base + 10
chase_animation.pos EQU _stack_base + 11
i EQU _stack_base + 10
inline1.write_leds.pattern EQU _stack_base + 11
main.adc_val EQU _stack_base + 3
main.i EQU _stack_base + 4
stack_animation.bit EQU _stack_base + 12
stack_animation.pattern EQU _stack_base + 13
tmp.0 EQU _stack_base + 7
tmp.1 EQU _stack_base + 14
tmp.10 EQU _stack_base + 14
tmp.11 EQU _stack_base + 14
tmp.12 EQU _stack_base + 14
tmp.13 EQU _stack_base + 7
tmp.14 EQU _stack_base + 8
tmp.15 EQU _stack_base + 9
tmp.16 EQU _stack_base + 5
tmp.17 EQU _stack_base + 6
tmp.18 EQU _stack_base + 6
tmp.2 EQU _stack_base + 15
tmp.20 EQU _stack_base + 6
tmp.21 EQU _stack_base + 6
tmp.22 EQU _stack_base + 6
tmp.23 EQU _stack_base + 7
tmp.3 EQU _stack_base + 16
tmp.5 EQU _stack_base + 17
tmp.6 EQU _stack_base + 11
tmp.7 EQU _stack_base + 11
tmp.8 EQU _stack_base + 15
tmp.9 EQU _stack_base + 16

; --- Reset Vector ---
	ORG 0x0000
	GOTO	main

; --- Interrupt Vector ---
	ORG 0x0004
__interrupt:
; Context Save (Hardware Automatic)
; main.py:42:     global button_pressed
; main.py:43:     button_pressed = 1
	MOVLW	0x01
	MOVLB	0
	MOVWF	button_pressed
; Context Restore (Hardware Automatic)
	RETFIE
adc_init:
; main.py:52:     ANSELA.value = 0x01             # RA0 analog, rest digital
	MOVLW	0x01
	MOVLB	62
	MOVWF	0x1F38
; main.py:53:     TRISA.value = TRISA.value | 0x01  # RA0 as input
	MOVLB	0
	MOVF	0x11, W
	IORLW	0x01
	MOVWF	0x11
; main.py:54:     ADPCH.value = 0x00              # Channel AN0
	MOVLW	0x00
	MOVLB	1
	MOVWF	0x9E
; main.py:55:     ADCLK.value = 0x1F              # Fosc/64 conversion clock
	MOVLW	0x1F
	MOVWF	0x98
; main.py:56:     ADREF.value = 0x00              # VDD / VSS references
	MOVLW	0x00
	MOVWF	0x9A
; main.py:57:     ADCON0.value = 0x00
	MOVWF	0x93
; main.py:58:     ADCON0[ADON] = 1                # Enable ADC module (bit-index write)
	BSF	0x93, 0
	RETURN
adc_read:
; main.py:61:     ADCON0[GO] = 1                  # Start conversion (bit-index write)
	BSF	0x93, 0
; main.py:63:     while ADCON0[GO]:               # Bit-index read in condition
L.0:
	BTFSS	0x93, 0
	GOTO	L.1
	GOTO	L.0
L.1:
; main.py:65:     return ADRESH.value             # Return 8-bit result (right-justified high)
	MOVF	0x8D, W
	RETURN
chase_animation:
	MOVLW	0x00
	MOVLB	0
	MOVWF	chase_animation.pos
	MOVWF	chase_animation.direction
; main.py:83:     for i in range(NUM_LEDS * 2 - 2):       # for-loop with range()
	MOVLW	0x08
	MOVWF	tmp.1
	ADDLW	0xFE
	MOVWF	tmp.2
	MOVLW	0x00
	MOVWF	i
L.2:
	MOVF	tmp.2, W
	SUBWF	i, W
	BTFSC	STATUS, 0
	GOTO	L.3
; main.py:84:         write_leds(1 << pos)                  # bit-shift expression
	MOVLW	0x01
	MOVWF	tmp.3
	MOVF	chase_animation.pos, W
	BTFSC	STATUS, 2
	GOTO	shift_done_1
shift_0:
	BCF	STATUS, 0
	RLF	tmp.3, F
	DECFSZ	chase_animation.pos, F
	GOTO	shift_0
shift_done_1:
	MOVF	tmp.3, W
	MOVWF	inline1.write_leds.pattern
; main.py:73:     PORTD.value = pattern
	MOVWF	0x0F
; main.py:86:         if direction == 0:
	MOVF	chase_animation.direction, W
	BTFSS	STATUS, 2
	GOTO	L.6
	MOVLW	0x01
	ADDWF	chase_animation.pos, F
; main.py:88:             if pos >= NUM_LEDS - 1:
	MOVF	chase_animation.pos, W
	SUBLW	0x06
	BTFSC	STATUS, 0
	GOTO	L.7
; main.py:89:                 direction = 1
	MOVLW	0x01
	MOVWF	chase_animation.direction
L.7:
	GOTO	L.5
L.6:
	MOVLW	0x01
	SUBWF	chase_animation.pos, F
; main.py:92:             if pos == 0:
	MOVF	chase_animation.pos, W
	BTFSS	STATUS, 2
	GOTO	L.8
; main.py:93:                 direction = 0
	MOVLW	0x00
	MOVWF	chase_animation.direction
L.8:
L.5:
; main.py:95:         delay_ms(CHASE_SPEED)
	CALL	pymcu_time_delay_ms
	MOVLB	0
	MOVWF	tmp.5
	MOVLW	0x01
	ADDWF	i, F
	GOTO	L.2
L.3:
	RETURN
blink_animation:
; main.py:102:     for i in range(4):
	MOVLW	0x00
	MOVWF	i
L.9:
	MOVF	i, W
	SUBLW	0x03
	BTFSS	STATUS, 0
	GOTO	L.10
; main.py:103:         write_leds(0xFF)
; main.py:73:     PORTD.value = pattern
	MOVLW	0xFF
	MOVWF	0x0F
; main.py:104:         delay_ms(CHASE_SPEED)
	CALL	pymcu_time_delay_ms
	MOVLB	0
	MOVWF	tmp.6
; main.py:105:         write_leds(0x00)
; main.py:73:     PORTD.value = pattern
	MOVLW	0x00
	MOVWF	0x0F
; main.py:106:         delay_ms(CHASE_SPEED)
	CALL	pymcu_time_delay_ms
	MOVLB	0
	MOVWF	tmp.7
	MOVLW	0x01
	ADDWF	i, F
	GOTO	L.9
L.10:
	RETURN
stack_animation:
	MOVLW	0x00
	MOVWF	stack_animation.pattern
	MOVWF	stack_animation.bit
; main.py:116:     while bit < NUM_LEDS:
L.13:
	MOVF	stack_animation.bit, W
	SUBLW	0x07
	BTFSS	STATUS, 0
	GOTO	L.14
; main.py:117:         pattern = pattern | (1 << bit)
	MOVLW	0x01
	MOVWF	tmp.8
	MOVF	stack_animation.bit, W
	BTFSC	STATUS, 2
	GOTO	shift_done_3
shift_2:
	BCF	STATUS, 0
	RLF	tmp.8, F
	DECFSZ	stack_animation.bit, F
	GOTO	shift_2
shift_done_3:
	MOVF	tmp.8, W
	IORWF	stack_animation.pattern, W
	MOVWF	stack_animation.pattern
; main.py:118:         write_leds(pattern)
	MOVWF	inline1.write_leds.pattern
; main.py:73:     PORTD.value = pattern
	MOVLW	0x00
	MOVWF	0x0F
; main.py:119:         delay_ms(CHASE_SPEED)
	CALL	pymcu_time_delay_ms
	MOVLB	0
	MOVWF	tmp.10
	MOVLW	0x01
	ADDWF	stack_animation.bit, F
	GOTO	L.13
L.14:
; main.py:123:     for i in range(NUM_LEDS):
	MOVLW	0x00
	MOVWF	i
L.16:
	MOVF	i, W
	SUBLW	0x07
	BTFSS	STATUS, 0
	GOTO	L.17
; main.py:124:         delay_ms(CHASE_SPEED)
	CALL	pymcu_time_delay_ms
	MOVLB	0
	MOVWF	tmp.11
; main.py:125:         pattern = pattern >> 1                # right-shift to drain
	MOVF	stack_animation.pattern, W
	MOVWF	tmp.12
	BCF	STATUS, 0
	RRF	tmp.12, F
	MOVF	tmp.12, W
	MOVWF	stack_animation.pattern
; main.py:126:         write_leds(pattern)
	MOVWF	inline1.write_leds.pattern
; main.py:73:     PORTD.value = pattern
	MOVLW	0x00
	MOVWF	0x0F
	MOVLW	0x01
	ADDWF	i, F
	GOTO	L.16
L.17:
	RETURN
run_current_mode:
; main.py:133:     global mode
	CLRF	tmp.13
	MOVLW	0x00
	SUBWF	0x00, W
	BTFSC	STATUS, 2
	INCF	tmp.13, F
cmp_end_5:
	MOVF	tmp.13, W
	BTFSC	STATUS, 2
	GOTO	L.20
; main.py:136:             chase_animation()
	CALL	chase_animation
	GOTO	L.19
L.20:
	MOVLB	0
	CLRF	tmp.14
	MOVLW	0x01
	SUBWF	0x00, W
	BTFSC	STATUS, 2
	INCF	tmp.14, F
cmp_end_7:
	MOVF	tmp.14, W
	BTFSC	STATUS, 2
	GOTO	L.21
; main.py:138:             blink_animation()
	CALL	blink_animation
	GOTO	L.19
L.21:
	MOVLB	0
	CLRF	tmp.15
	MOVLW	0x02
	SUBWF	0x00, W
	BTFSC	STATUS, 2
	INCF	tmp.15, F
cmp_end_9:
	MOVF	tmp.15, W
	BTFSC	STATUS, 2
	GOTO	L.22
; main.py:140:             stack_animation()
	CALL	stack_animation
	GOTO	L.19
L.22:
; main.py:142:             mode = MODE_CHASE
	MOVLW	0x00
	MOVLB	0
	MOVWF	0x00
; main.py:143:             chase_animation()
	CALL	chase_animation
L.19:
	RETURN
main:
; main.py:149:     global mode
; main.py:150:     global button_pressed
; main.py:154:     btn = Pin("RB4", Pin.IN, pull=Pin.PULL_UP)
; main.py:22: #  Module-level constants and globals
; main.py:26: ADC_THRESHOLD: const = const(128)    # midpoint of 8-bit ADC
; main.py:6: #   - 8 LEDs on PORTD (RD0-RD7), active-high
; main.py:27: MODE_CHASE: const = const(0)
	MOVLB	0
	BCF	0x12, 4
; main.py:27: MODE_CHASE: const = const(0)
; main.py:29: MODE_STACK: const = const(2)
; main.py:30: NUM_MODES: const = const(3)
	MOVLB	4
	BSF	0x20D, 4
; main.py:33: button_pressed: uint8 = 0
; main.py:155:     btn.irq(Pin.IRQ_FALLING)
	MOVLB	7
	BSF	0x396, 4
	MOVLB	14
	BSF	0x716, 4
	MOVLB	0
	BSF	0x0B, 7
; main.py:158:     TRISD.value = 0x00
	MOVLW	0x00
	MOVWF	0x14
; main.py:159:     PORTD.value = 0x00
	MOVWF	0x0F
; main.py:162:     adc_init()
	CALL	adc_init
; main.py:165:     INTCON[GIE] = 1                         # bit-index write
	MOVLB	0
	BSF	0x0B, 7
; main.py:166:     INTCON[PEIE] = 1
	BSF	0x0B, 6
; main.py:169:     for i in range(NUM_LEDS):
	MOVLW	0x00
	MOVWF	i
L.135:
	MOVF	i, W
	SUBLW	0x07
	BTFSS	STATUS, 0
	GOTO	L.136
; main.py:170:         write_leds(1 << i)
	MOVLW	0x01
	MOVWF	tmp.16
	MOVF	main.i, W
	BTFSC	STATUS, 2
	GOTO	shift_done_11
shift_10:
	BCF	STATUS, 0
	RLF	tmp.16, F
	DECFSZ	main.i, F
	GOTO	shift_10
shift_done_11:
	MOVF	tmp.16, W
	MOVWF	inline1.write_leds.pattern
; main.py:73:     PORTD.value = pattern
	MOVLW	0x00
	MOVWF	0x0F
; main.py:171:         delay_ms(40)
	CALL	pymcu_time_delay_ms
	MOVLB	0
	MOVWF	tmp.17
	MOVLW	0x01
	ADDWF	i, F
	GOTO	L.135
L.136:
; main.py:172:     write_leds(0x00)
; main.py:73:     PORTD.value = pattern
	MOVLW	0x00
	MOVWF	0x0F
; main.py:173:     delay_ms(200)
	CALL	pymcu_time_delay_ms
	MOVLB	0
	MOVWF	tmp.18
; main.py:176:     while True:
L.139:
; main.py:178:         if button_pressed == 1:
	MOVF	button_pressed, W
	XORLW	0x01
	BTFSS	STATUS, 2
	GOTO	L.141
; main.py:179:             button_pressed = 0
	MOVLW	0x00
	MOVWF	button_pressed
	MOVLW	0x01
	ADDWF	0x00, F
; main.py:181:             if mode >= NUM_MODES:
; main.py:185:             write_leds(mode + 1)
; main.py:73:     PORTD.value = pattern
	MOVLW	0x01
	MOVWF	0x0F
; main.py:186:             delay_ms(300)
	CALL	pymcu_time_delay_ms
	MOVLB	0
	MOVWF	tmp.20
; main.py:187:             write_leds(0x00)
; main.py:73:     PORTD.value = pattern
	MOVLW	0x00
	MOVWF	0x0F
; main.py:188:             delay_ms(100)
	CALL	pymcu_time_delay_ms
	MOVLB	0
	MOVWF	tmp.21
L.141:
	CALL	adc_read
	MOVLB	0
	MOVWF	main.adc_val
; main.py:197:         if adc_val >= ADC_THRESHOLD:
	SUBLW	0x7F
	BTFSC	STATUS, 0
	GOTO	L.146
; main.py:198:             run_current_mode()
	CALL	run_current_mode
	GOTO	L.145
L.146:
; main.py:200:             write_leds(0x00)
; main.py:73:     PORTD.value = pattern
	MOVLW	0x00
	MOVLB	0
	MOVWF	0x0F
; main.py:201:             delay_ms(100)
	CALL	pymcu_time_delay_ms
	MOVLB	0
	MOVWF	tmp.23
L.145:
	GOTO	L.139
	END

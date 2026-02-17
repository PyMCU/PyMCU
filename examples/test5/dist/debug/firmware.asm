	LIST P=pic16f18877
#include <p16f18877.inc>
	errorlevel -302

; --- Compiled Stack (Overlays) ---
	CBLOCK 0x20
_stack_base: 7
	ENDC

; --- Variable Offsets ---
main.state EQU _stack_base + 0
nco_init.inc_val EQU _stack_base + 5
uart_write.data EQU _stack_base + 5

; --- Reset Vector ---
	ORG 0x0000
	GOTO	main

; --- Interrupt Vector ---
	ORG 0x0004
__interrupt:
	RETFIE
nco_init:
; nco.py:6:     NCO1CON.value = 0x00 # Apagar primero
	MOVLW	0x00
	MOVLB	11
	MOVWF	0x592
; nco.py:10:     NCO1CLK.value = 0x01
	MOVLW	0x01
	MOVWF	0x593
; nco.py:15:     NCO1INC.value = inc_val
	MOVLB	0
	MOVF	nco_init.inc_val, W
	MOVLB	11
	MOVWF	0x58F
	MOVLB	0
	MOVF	nco_init.inc_val+1, W
	MOVLB	11
	MOVWF	0x590
; nco.py:19:     NCO1CON.value = 0x81
	MOVLW	0x81
	MOVWF	0x592
	RETURN
uart_init:
; uart.py:12:     INTCON[7] = 0 # GIE Off
	MOVLB	0
	BCF	0x0B, 7
; uart.py:13:     PPSLOCK.value = 0x55
	MOVLW	0x55
	MOVLB	61
	MOVWF	0x1E8F
; uart.py:14:     PPSLOCK.value = 0xAA
	MOVLW	0xAA
	MOVWF	0x1E8F
; uart.py:15:     PPSLOCK[0] = 0 # Clear PPSLOCKED bit
	BCF	0x1E8F, 0
; uart.py:30:     RC6PPS.value = 0x10  # 0x10 es la función TX/CK
	MOVLW	0x10
	MOVLB	62
	MOVWF	0x1F26
; uart.py:34:     RXPPS.value = 0x17   # 0x17 es el pin RC7
	MOVLW	0x17
	MOVLB	61
	MOVWF	0x1ECB
; uart.py:19:     PPSLOCK.value = 0x55
	MOVLW	0x55
	MOVWF	0x1E8F
; uart.py:20:     PPSLOCK.value = 0xAA
	MOVLW	0xAA
	MOVWF	0x1E8F
; uart.py:21:     PPSLOCK[0] = 1 # Set PPSLOCKED bit
	BSF	0x1E8F, 0
; uart.py:22:     INTCON[7] = 1 # GIE On
	MOVLB	0
	BSF	0x0B, 7
; uart.py:40:     TRISC[6] = 0 # TX Output
	BCF	0x13, 6
; uart.py:41:     TRISC[7] = 1 # RX Input
	BSF	0x13, 7
; uart.py:43:     SP1BRG.value = BAUD_VAL
	MOVLW	0x19
	MOVLB	2
	MOVWF	0x11B
	MOVLW	0x00
	MOVWF	0x11C
; uart.py:44:     TX1STA.value = 0x24 # TXEN, BRGH
	MOVLW	0x24
	MOVWF	0x11E
; uart.py:45:     RC1STA.value = 0x90 # SPEN, CREN
	MOVLW	0x90
	MOVWF	0x11D
	RETURN
uart_write:
; uart.py:50:     while not PIR3[5]: # TXIF está en PIR3 bit 5 en este chip
L.2:
	MOVLB	14
	BTFSC	0x70F, 5
	GOTO	L.3
	GOTO	L.2
L.3:
; uart.py:52:     TX1REG.value = data
	MOVLB	0
	MOVF	uart_write.data, W
	MOVLB	2
	MOVWF	0x11A
	RETURN
main:
	CALL	uart_init
	MOVLW	0x48
	MOVLB	0
	MOVWF	uart_write.data
	MOVLW	0x00
	MOVWF	uart_write.data+1
	CALL	uart_write
	MOVLW	0x69
	MOVLB	0
	MOVWF	uart_write.data
	MOVLW	0x00
	MOVWF	uart_write.data+1
	CALL	uart_write
	MOVLW	0x00
	MOVLB	0
	MOVWF	nco_init.inc_val
	MOVLW	0x80
	MOVWF	nco_init.inc_val+1
	CALL	nco_init
; main.py:12: def main():
	MOVLB	0
	BCF	0x0B, 7
; main.py:13:     # Inicialización usando Namespaces
	MOVLW	0x55
	MOVLB	61
	MOVWF	0x1E8F
; main.py:14:     uart.init()
	MOVLW	0xAA
	MOVWF	0x1E8F
; main.py:15: 
	BCF	0x1E8F, 0
; main.py:27:     RA0PPS.value = 0x18 # 0x18 = NCO1 Output
	MOVLW	0x18
	MOVLB	62
	MOVWF	0x1F10
; main.py:19: 
	MOVLW	0x55
	MOVLB	61
	MOVWF	0x1E8F
; main.py:20:     # Configurar NCO
	MOVLW	0xAA
	MOVWF	0x1E8F
; main.py:21:     nco.init(NOTE_C4)
	BSF	0x1E8F, 0
; main.py:22: 
	MOVLB	0
	BSF	0x0B, 7
; main.py:30:     TRISA[0] = 0 # RA0 Output
	BCF	0x11, 0
; main.py:32:     state = 0
	MOVLW	0x00
	MOVWF	main.state
; main.py:34:     while True:
L.6:
; Delay 500 ms
__dly_c1 EQU 0x27
__dly_c2 EQU 0x28
__dly_c3 EQU 0x29
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
; main.py:38:         if state == 0:
	MOVF	main.state, W
	BTFSS	STATUS, 2
	GOTO	L.9
	MOVLW	0xF0
	MOVWF	nco_init.inc_val
	MOVLW	0xA0
	MOVWF	nco_init.inc_val+1
	CALL	nco_init
	MOVLW	0x45
	MOVLB	0
	MOVWF	uart_write.data
	MOVLW	0x00
	MOVWF	uart_write.data+1
	CALL	uart_write
; main.py:41:             state = 1
	MOVLW	0x01
	MOVLB	0
	MOVWF	main.state
	GOTO	L.8
L.9:
	MOVF	main.state, W
	XORLW	0x01
	BTFSS	STATUS, 2
	GOTO	L.10
	MOVLW	0x00
	MOVWF	nco_init.inc_val
	MOVLW	0xC0
	MOVWF	nco_init.inc_val+1
	CALL	nco_init
	MOVLW	0x47
	MOVLB	0
	MOVWF	uart_write.data
	MOVLW	0x00
	MOVWF	uart_write.data+1
	CALL	uart_write
; main.py:45:             state = 2
	MOVLW	0x02
	MOVLB	0
	MOVWF	main.state
	GOTO	L.8
L.10:
	MOVLW	0x00
	MOVWF	nco_init.inc_val
	MOVLW	0x80
	MOVWF	nco_init.inc_val+1
	CALL	nco_init
	MOVLW	0x43
	MOVLB	0
	MOVWF	uart_write.data
	MOVLW	0x00
	MOVWF	uart_write.data+1
	CALL	uart_write
; main.py:49:             state = 0
	MOVLW	0x00
	MOVLB	0
	MOVWF	main.state
L.8:
	GOTO	L.6
	END

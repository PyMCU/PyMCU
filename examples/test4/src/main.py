from whipsnake.chips.pic16f877a import *

STATUS_LED = 0  # RB0
BAUD_RATE_CONST = 25

adc_val_high = 0
adc_val_low = 0
tx_data = 0
eeprom_addr = 0


@interrupt
def isr_handler():
    if PIR1[5]:
        global tx_data
        tx_data = RCREG.value


@inline
def uart_init():
    TRISC[6] = 0  # TX output
    TRISC[7] = 1  # RX input
    SPBRG.value = BAUD_RATE_CONST
    TXSTA.value = 0x24  # TXEN, BRGH
    RCSTA.value = 0x90  # SPEN, CREN


@inline
def uart_write(byte_val: int):
    while not TXSTA[1]:
        pass  # Spinlock
    TXREG.value = byte_val


@inline
def adc_init():
    ADCON1.value = 0x0E
    ADCON0.value = 0x41


@inline
def adc_read():
    global adc_val_high

    ADCON0[2] = 1

    while ADCON0[2]:
        pass

    adc_val_high = ptr(0x1E).value


# ==========================================
#  EEPROM WRITE (La prueba de fuego)
# ==========================================
@inline
def eeprom_write_byte(addr: int, data: int):
    while EECON1[1]:
        pass

    EEADR.value = addr
    EEDATA.value = data

    EECON1[7] = 0  # EEPGD (Access Data Memory)
    EECON1[2] = 1  # WREN (Enable Write)

    # --- SECUENCIA CRÍTICA (No puede haber interrupciones) ---
    INTCON[7] = 0  # GIE Off

    # Secuencia mágica 0x55 -> 0xAA
    EECON2.value = 0x55
    EECON2.value = 0xAA

    # Iniciar escritura
    EECON1[1] = 1  # WR = 1

    INTCON[7] = 1  # GIE On
    # ---------------------------------------------------------

    # Deshabilitar escritura
    EECON1[2] = 0


# ==========================================
#  MAIN
# ==========================================
def main():
    # Configurar puertos
    TRISB[STATUS_LED] = 0

    uart_init()
    adc_init()

    # Habilitar interrupciones globales y periféricos
    INTCON[6] = 1
    INTCON[7] = 1

    counter = 0

    while True:
        # Leer Sensor
        adc_read()

        # Si el valor supera umbral, guardar en EEPROM
        if adc_val_high > 200:
            PORTB[STATUS_LED] = 1  # Prender LED de alerta

            # Guardar en dirección 0
            eeprom_write_byte(0, adc_val_high)

            # Enviar aviso por Serial
            uart_write(0x21)  # '!'
        else:
            PORTB[STATUS_LED] = 0

        # Enviar valor leido por Serial (Telemetry)
        uart_write(adc_val_high)

        delay_ms(100)

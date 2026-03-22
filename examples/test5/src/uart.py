# uart.py
from whisnake.chips.pic16f18877 import *
from whisnake.types import inline

# Constantes privadas del módulo
BAUD_VAL = 25  # 9600 baudios @ 4MHz

@inline
def unlock_pps():
    # Secuencia mágica para desbloquear asignación de pines
    # PPSLOCK está en Banco 61 (0x1E8F)
    INTCON[7] = 0 # GIE Off
    PPSLOCK.value = 0x55
    PPSLOCK.value = 0xAA
    PPSLOCK[0] = 0 # Clear PPSLOCKED bit

@inline
def lock_pps():
    PPSLOCK.value = 0x55
    PPSLOCK.value = 0xAA
    PPSLOCK[0] = 1 # Set PPSLOCKED bit
    INTCON[7] = 1 # GIE On

def init():
    # 1. Configurar Pines (Requiere PPS en Banco 61/62)
    unlock_pps()

    # Asignar RC6 como TX (Salida)
    # RC6PPS está en 0x1F26 (Banco 62)
    RC6PPS.value = 0x10  # 0x10 es la función TX/CK

    # Asignar RC7 como RX (Entrada)
    # RXPPS está en 0x1ECB (Banco 61)
    RXPPS.value = 0x17   # 0x17 es el pin RC7

    lock_pps()

    # 2. Configurar Registros UART (Banco 3)
    # OJO: TRISC está en Banco 0 ahora en el 18877
    TRISC[6] = 0 # TX Output
    TRISC[7] = 1 # RX Input

    SP1BRG.value = BAUD_VAL
    TX1STA.value = 0x24 # TXEN, BRGH
    RC1STA.value = 0x90 # SPEN, CREN

def write(data: int):
    # Esperar a que el buffer esté vacío
    # PIR3 está en Banco 14
    while not PIR3[5]: # TXIF está en PIR3 bit 5 en este chip
        pass
    TX1REG.value = data
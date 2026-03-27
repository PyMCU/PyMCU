# nco.py
from pymcu.chips.pic16f18877 import *

def init(inc_val: int):
    # NCO1CON está en Banco 11
    NCO1CON.value = 0x00 # Apagar primero

    # Configurar Clock Source (NCO1CLK)
    # 0x01 = FOSC (4MHz)
    NCO1CLK.value = 0x01

    # Configurar Incremento (Controla la frecuencia)
    # NCO1INC es un registro de 16 bits (Low+High)
    # Tu compilador debe manejar escritura de 16 bits en Banco 11
    NCO1INC.value = inc_val

    # Habilitar NCO, Modo Fijo (Fixed Duty Cycle)
    # Bit 7: N1EN, Bit 0: N1PFM
    NCO1CON.value = 0x81
# main.py
from whisnake.chips.pic16f18877 import *
# IMPORTS DE MÓDULOS (La nueva funcionalidad)
import uart
import nco

# Constantes para melodía (Valores de incremento NCO)
NOTE_C4 = 32768
NOTE_E4 = 41200
NOTE_G4 = 49152

def main():
    # Inicialización usando Namespaces
    uart.init()

    # Enviar mensaje de arranque
    uart.write(0x48) # 'H'
    uart.write(0x69) # 'i'

    # Configurar NCO
    nco.init(NOTE_C4)

    # Asignar salida del NCO al pin RA0 usando PPS
    # (Hacemos trampa y usamos la func del uart para desbloquear o
    # lo hacemos manual aqui para probar acceso local)
    uart.unlock_pps()
    RA0PPS.value = 0x18 # 0x18 = NCO1 Output
    uart.lock_pps()

    TRISA[0] = 0 # RA0 Output

    state = 0

    while True:
        # Loop simple que cambia notas
        delay_ms(500)

        if state == 0:
            nco.init(NOTE_E4)
            uart.write(0x45) # 'E'
            state = 1
        elif state == 1:
            nco.init(NOTE_G4)
            uart.write(0x47) # 'G'
            state = 2
        else:
            nco.init(NOTE_C4)
            uart.write(0x43) # 'C'
            state = 0
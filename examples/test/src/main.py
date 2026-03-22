from whisnake.chips.pic16f877a import *
from whisnake.hal.gpio import Pin

# Función de retardo simple para hacer visible el efecto
# Como no hay temporizadores complejos ni interrupciones aún,
# usamos "quemado" de ciclos de CPU.
def delay_soft(count: int):
    while count > 0:
        # Loop interno para gastar tiempo
        inner: int = 0
        while inner < 50:
            inner = inner + 1
        count = count - 1

def main():
    # 1. Configuración de Puertos
    # El módulo CCP1 (PWM) sale por el pin RC2 en el PIC16F877A
    # Necesitamos poner RC2 como salida (TRIS bit = 0)
    TRISC[RC2] = 0
    TRISA[RA0] = 1
    TRISA[RA1] = 1
    TRISA[RA2] = 1
    TRISA[RA3] = 1
    ra3 = Pin("RA3", Pin.IN)

    ra3.high()  # Set RA3 high (pull-up) to detect button press (active low)

    # 2. Configuración del PWM (Hardware)
    # Periodo del PWM (PR2). 0xFF es el máximo (~1.22 kHz a 20MHz/prescaler)
    PR2 = 0xFF

    # Configurar CCP1CON para modo PWM
    # Bits CCP1M3 y CCP1M2 deben estar en 1 para modo PWM
    CCP1CON[CCP1M3] = 1
    CCP1CON[CCP1M2] = 1

    # 3. Configuración del Timer 2 (Necesario para PWM)
    # Pre-scaler 1:16 (T2CKPS1 = 1)
    T2CON[T2CKPS1] = 1
    # Encender Timer 2
    T2CON[TMR2ON] = 1

    # Variables de estado
    duty: int = 0
    going_up: bool = True

    while True:
        # Escribir el ciclo de trabajo actual al registro del hardware
        # Esto controla el brillo del LED
        CCPR1L = duty

        # Lógica de desvanecimiento (Fading)
        if going_up:
            duty = duty + 1
            if duty >= 250:
                going_up = False
        else:
            duty = duty - 1
            if duty <= 0:
                going_up = True

        # Esperar un poco para que el ojo humano vea el cambio
        delay_soft(2)
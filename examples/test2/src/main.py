from whisnake.chips.pic16f877a import *

def main():
    # Configuración de Puertos
    TRISB = 0xFF # Entrada (Sensores/Datos)
    TRISD = 0x00 # Salida (Hash visual)

    # Inicialización
    crc_reg = 0xFF
    lfsr_reg = 0x55

    while True:
        # 1. Leer dato del puerto B
        input_val = PORTB

        # 2. Actualizar generador aleatorio
        update_lfsr()

        # 3. Mezclar entropía con operaciones aritméticas
        mix_entropy(input_val)

        # 4. Calcular CRC mezclando el LFSR y la Entropía
        # Aquí forzamos al compilador a resolver una expresión antes de pasarla
        val_to_hash: int = lfsr_reg ^ entropy_pool
        update_crc(val_to_hash)

        # 5. Visualizar resultado
        # Invertir resultado final para mostrar en LEDs (Active Low simulado)
        output: int = crc_reg
        output ^= 0xFF
        PORTD = output
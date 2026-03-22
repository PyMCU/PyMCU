from whisnake.types import uint8, uint16, inline
from whisnake.hal.gpio import Pin
# Asumimos que tu módulo time se llama así:
from whisnake.time import delay_ms, delay_us


class DHT11:
    @inline
    def __init__(self, pin: Pin):
        self.pin = pin  # Guarda la instancia del Pin
        self.failed = False
        self.temperature = 0
        self.humidity = 0

    @inline
    def measure(self):
        """
        Inicia la comunicación, lee los 40 bits, verifica el checksum 
        y retorna los datos empaquetados (High Byte = Hum, Low Byte = Temp).
        Retorna 0xFFFF si hay un error.
        """
        # 1. Start Signal (MCU)
        # Bajar la línea durante al menos 18ms para despertar al DHT11
        self.pin.mode(Pin.OUT)
        self.pin.low()
        delay_ms(18)

        # Subir la línea y esperar 20-40us para que el sensor tome el control
        self.pin.high()
        delay_us(30)
        self.pin.mode(Pin.IN)  # Cambiamos a entrada para escuchar

        # 2. Acknowledge (Sensor)
        # El sensor debe responder bajando la línea ~80us, luego subiéndola ~80us.
        # Usamos pulse_in(0) para esperar y medir el pulso LOW del sensor.
        ack_low = self.pin.pulse_in(0, 1000)
        if ack_low == 0:
            self.failed = True
            return 0xFFFF  # Timeout, no hay sensor conectado o falló

        # Esperamos el fin del pulso HIGH de confirmación
        ack_high = self.pin.pulse_in(1, 1000)
        if ack_high == 0:
            self.failed = True
            return 0xFFFF

        # 3. Leer los 5 bytes (40 bits)
        hum_int = self._read_byte()
        hum_dec = self._read_byte()  # En DHT11 normalmente es 0
        temp_int = self._read_byte()
        temp_dec = self._read_byte()  # En DHT11 normalmente es 0
        checksum = self._read_byte()

        # 4. Validar Checksum
        # El checksum es la suma de los primeros 4 bytes truncada a 8 bits
        expected_chk = (hum_int + hum_dec + temp_int + temp_dec) & 0xFF

        if checksum != expected_chk:
            self.failed = True
            return 0xFFFF

        # 5. Éxito: Guardar estado y empaquetar
        self.failed = False
        self.humidity = hum_int
        self.temperature = temp_int

    @inline
    def _read_byte(self) -> uint8:
        """
        Lee 8 bits consecutivos del sensor.
        """
        result: uint8 = 0
        i: uint8 = 0

        while i < 8:
            # Cada bit comienza con 50us en LOW que podemos ignorar.
            # El bit real se determina por el tiempo que la línea se mantiene en HIGH:
            # ~26-28us = '0' lógico
            # ~70us    = '1' lógico
            # pulse_in(1, 100) espera a que sea HIGH y mide cuánto dura antes de bajar.
            duration: uint16 = self.pin.pulse_in(1, 1000)

            # Desplazamos los bits hacia la izquierda
            result = result << 1

            # Usamos 40us como umbral seguro para decidir si es 0 o 1
            if duration > 40:
                result = result | 1

            i = i + 1

        return result
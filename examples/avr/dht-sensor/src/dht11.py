from pymcu.types import uint8, uint16, inline
from pymcu.hal.gpio import Pin
from pymcu.time import delay_ms, delay_us


class DHT11:
    @inline
    def __init__(self, pin: Pin):
        self.pin = pin
        self.failed = False
        self.temperature = 0
        self.humidity = 0

    @inline
    def measure(self):
        # 1. Start Signal (MCU)
        # Pull the line LOW for at least 18ms to wake the DHT11
        self.pin.mode(Pin.OUT)
        self.pin.low()
        delay_ms(18)

        # Pull HIGH and wait 20-40us for the sensor to take control
        self.pin.high()
        delay_us(30)
        self.pin.mode(Pin.IN)

        # 2. Acknowledge (Sensor)
        # Sensor responds by pulling LOW ~80us then HIGH ~80us.
        # pulse_in(0) waits and measures the LOW pulse from the sensor.
        ack_low = self.pin.pulse_in(0, 1000)
        if ack_low == 0:
            self.failed = True
            return 0xFFFF  # Timeout - no sensor connected or failure

        # Wait for end of HIGH confirmation pulse
        ack_high = self.pin.pulse_in(1, 1000)
        if ack_high == 0:
            self.failed = True
            return 0xFFFF

        # 3. Read 5 bytes (40 bits)
        hum_int = self._read_byte()
        hum_dec = self._read_byte()   # In DHT11 this is normally 0
        temp_int = self._read_byte()
        temp_dec = self._read_byte()  # In DHT11 this is normally 0
        checksum = self._read_byte()

        # 4. Validate Checksum
        # Checksum is the sum of the first 4 bytes truncated to 8 bits
        expected_chk = (hum_int + hum_dec + temp_int + temp_dec) & 0xFF

        if checksum != expected_chk:
            self.failed = True
            return 0xFFFF

        # 5. Success: save state
        self.failed = False
        self.humidity = hum_int
        self.temperature = temp_int

    @inline
    def _read_byte(self) -> uint8:
        # Read 8 consecutive bits from the sensor.
        result: uint8 = 0
        i: uint8 = 0

        while i < 8:
            # Each bit starts with 50us LOW (ignored).
            # The actual bit is determined by how long the line stays HIGH:
            # ~26-28us = logical '0'
            # ~70us    = logical '1'
            # pulse_in(1, 100) waits for HIGH and measures duration.
            duration: uint16 = self.pin.pulse_in(1, 1000)

            result = result << 1

            # Use 40us as safe threshold to decide 0 or 1
            if duration > 40:
                result = result | 1

            i = i + 1

        return result


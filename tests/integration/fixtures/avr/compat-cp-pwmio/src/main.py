# CircuitPython pwmio integration test fixture
#
# Verifies that pwmio.PWMOut:
#   - Configures Timer0 in Fast PWM mode (TCCR0A bits)
#   - Writes the correct duty cycle to OCR0A (32768 >> 8 = 128 = 50% duty)
#
# After PWM setup, sends 0x44 ('D') via busio.UART to signal completion.
#
import board
import busio
from pwmio import PWMOut


def main():
    uart = busio.UART(board.TX, board.RX, baudrate=9600)
    # 50% duty cycle: CircuitPython 16-bit 32768 -> OCR0A = 32768 >> 8 = 128
    pwm = PWMOut(board.D6, duty_cycle=32768)
    uart.write(0x44)  # 'D' done marker
    while True:
        pass

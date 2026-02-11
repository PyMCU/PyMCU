from pymcu.types import ptr, uint8

# ==========================================
#  Device Memory Configuration
# ==========================================
# PIC16F84A has 68 bytes of RAM (small device for testing limits)
# RAM: 0x0C-0x4F (Bank 0: 0x0C-0x2F, Bank 1: 0x8C-0xAF mapped to 0x0C-0x2F)
# We use 0x20 as start to align with general purpose registers
RAM_START = 0x20
RAM_SIZE = 68

# ==========================================
#  Register Definitions
# ==========================================

# Special Function Registers (Bank 0)
INDF:    ptr[uint8] = ptr(0x00)
TMR0:    ptr[uint8] = ptr(0x01)
PCL:     ptr[uint8] = ptr(0x02)
STATUS:  ptr[uint8] = ptr(0x03)
FSR:     ptr[uint8] = ptr(0x04)
PORTA:   ptr[uint8] = ptr(0x05)
PORTB:   ptr[uint8] = ptr(0x06)
EEDATA:  ptr[uint8] = ptr(0x08)
EEADR:   ptr[uint8] = ptr(0x09)
PCLATH:  ptr[uint8] = ptr(0x0A)
INTCON:  ptr[uint8] = ptr(0x0B)

# Special Function Registers (Bank 1)
OPTION_REG: ptr[uint8] = ptr(0x81)
TRISA:      ptr[uint8] = ptr(0x85)
TRISB:      ptr[uint8] = ptr(0x86)
EECON1:     ptr[uint8] = ptr(0x88)
EECON2:     ptr[uint8] = ptr(0x89)

# Status Register Bits
C = 0    # Carry bit
DC = 1   # Digit carry bit
Z = 2    # Zero bit
PD = 3   # Power-down bit
TO = 4   # Time-out bit
RP0 = 5  # Register bank select bit 0
RP1 = 6  # Register bank select bit 1
IRP = 7  # Indirect addressing register bank select

# PORTA Bits
RA0 = 0
RA1 = 1
RA2 = 2
RA3 = 3
RA4 = 4

# PORTB Bits
RB0 = 0
RB1 = 1
RB2 = 2
RB3 = 3
RB4 = 4
RB5 = 5
RB6 = 6
RB7 = 7

# INTCON Bits
RBIF = 0   # RB Port Change Interrupt Flag
INTF = 1   # External Interrupt Flag
T0IF = 2   # TMR0 Overflow Interrupt Flag
RBIE = 3   # RB Port Change Interrupt Enable
INTE = 4   # External Interrupt Enable
T0IE = 5   # TMR0 Overflow Interrupt Enable
EEIE = 6   # EEPROM Write Complete Interrupt Enable
GIE = 7    # Global Interrupt Enable

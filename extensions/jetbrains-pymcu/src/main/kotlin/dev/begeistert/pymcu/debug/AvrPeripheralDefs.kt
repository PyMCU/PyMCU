// SPDX-License-Identifier: Apache-2.0
package dev.begeistert.pymcu.debug

/**
 * ATmega328P I/O register map.
 *
 * All addresses are data-space addresses (as returned by getMemory).
 * The AVR I/O space begins at 0x20. Values written here are used by
 * [PyMcuPeripheralsPanel] to read from the emulator memory snapshot.
 */
object AvrPeripheralDefs {

    data class BitField(val name: String, val msb: Int, val lsb: Int, val description: String = "")

    data class Register(
        val name: String,
        val address: Int,
        val description: String = "",
        val fields: List<BitField> = emptyList()
    )

    data class Peripheral(val name: String, val registers: List<Register>)

    val peripherals: List<Peripheral> = listOf(

        Peripheral("GPIO", listOf(
            Register("PINB",  0x23, "Port B Input Pins", listOf(
                BitField("PINB7", 7, 7), BitField("PINB6", 6, 6), BitField("PINB5", 5, 5),
                BitField("PINB4", 4, 4), BitField("PINB3", 3, 3), BitField("PINB2", 2, 2),
                BitField("PINB1", 1, 1), BitField("PINB0", 0, 0)
            )),
            Register("DDRB",  0x24, "Port B Data Direction Register", listOf(
                BitField("DDB7", 7, 7), BitField("DDB6", 6, 6), BitField("DDB5", 5, 5),
                BitField("DDB4", 4, 4), BitField("DDB3", 3, 3), BitField("DDB2", 2, 2),
                BitField("DDB1", 1, 1), BitField("DDB0", 0, 0)
            )),
            Register("PORTB", 0x25, "Port B Data Register", listOf(
                BitField("PORTB7", 7, 7), BitField("PORTB6", 6, 6), BitField("PORTB5", 5, 5),
                BitField("PORTB4", 4, 4), BitField("PORTB3", 3, 3), BitField("PORTB2", 2, 2),
                BitField("PORTB1", 1, 1), BitField("PORTB0", 0, 0)
            )),
            Register("PINC",  0x26, "Port C Input Pins", listOf(
                BitField("PINC6", 6, 6), BitField("PINC5", 5, 5), BitField("PINC4", 4, 4),
                BitField("PINC3", 3, 3), BitField("PINC2", 2, 2), BitField("PINC1", 1, 1),
                BitField("PINC0", 0, 0)
            )),
            Register("DDRC",  0x27, "Port C Data Direction Register", listOf(
                BitField("DDC6", 6, 6), BitField("DDC5", 5, 5), BitField("DDC4", 4, 4),
                BitField("DDC3", 3, 3), BitField("DDC2", 2, 2), BitField("DDC1", 1, 1),
                BitField("DDC0", 0, 0)
            )),
            Register("PORTC", 0x28, "Port C Data Register", listOf(
                BitField("PORTC6", 6, 6), BitField("PORTC5", 5, 5), BitField("PORTC4", 4, 4),
                BitField("PORTC3", 3, 3), BitField("PORTC2", 2, 2), BitField("PORTC1", 1, 1),
                BitField("PORTC0", 0, 0)
            )),
            Register("PIND",  0x29, "Port D Input Pins", listOf(
                BitField("PIND7", 7, 7), BitField("PIND6", 6, 6), BitField("PIND5", 5, 5),
                BitField("PIND4", 4, 4), BitField("PIND3", 3, 3), BitField("PIND2", 2, 2),
                BitField("PIND1", 1, 1), BitField("PIND0", 0, 0)
            )),
            Register("DDRD",  0x2A, "Port D Data Direction Register", listOf(
                BitField("DDD7", 7, 7), BitField("DDD6", 6, 6), BitField("DDD5", 5, 5),
                BitField("DDD4", 4, 4), BitField("DDD3", 3, 3), BitField("DDD2", 2, 2),
                BitField("DDD1", 1, 1), BitField("DDD0", 0, 0)
            )),
            Register("PORTD", 0x2B, "Port D Data Register", listOf(
                BitField("PORTD7", 7, 7), BitField("PORTD6", 6, 6), BitField("PORTD5", 5, 5),
                BitField("PORTD4", 4, 4), BitField("PORTD3", 3, 3), BitField("PORTD2", 2, 2),
                BitField("PORTD1", 1, 1), BitField("PORTD0", 0, 0)
            )),
        )),

        Peripheral("Timer0", listOf(
            Register("TIFR0",  0x35, "Timer/Counter0 Interrupt Flag Register", listOf(
                BitField("TOV0", 0, 0, "Overflow Flag"), BitField("OCF0A", 1, 1, "Output Compare A Match Flag"),
                BitField("OCF0B", 2, 2, "Output Compare B Match Flag")
            )),
            Register("TCCR0A", 0x44, "Timer/Counter0 Control Register A", listOf(
                BitField("WGM00", 0, 0), BitField("WGM01", 1, 1),
                BitField("COM0B", 5, 4), BitField("COM0A", 7, 6)
            )),
            Register("TCCR0B", 0x45, "Timer/Counter0 Control Register B", listOf(
                BitField("CS0",  2, 0, "Clock Select"), BitField("WGM02", 3, 3),
                BitField("FOC0B", 6, 6), BitField("FOC0A", 7, 7)
            )),
            Register("TCNT0",  0x46, "Timer/Counter0 Value"),
            Register("OCR0A",  0x47, "Output Compare Register A"),
            Register("OCR0B",  0x48, "Output Compare Register B"),
            Register("TIMSK0", 0x6E, "Timer/Counter0 Interrupt Mask Register", listOf(
                BitField("TOIE0", 0, 0, "Overflow IE"), BitField("OCIE0A", 1, 1, "Output Compare A IE"),
                BitField("OCIE0B", 2, 2, "Output Compare B IE")
            )),
        )),

        Peripheral("Timer1", listOf(
            Register("TIFR1",  0x36, "Timer/Counter1 Interrupt Flag Register", listOf(
                BitField("TOV1", 0, 0), BitField("OCF1A", 1, 1), BitField("OCF1B", 2, 2),
                BitField("ICF1", 5, 5)
            )),
            Register("TCCR1A", 0x80, "Timer/Counter1 Control Register A", listOf(
                BitField("WGM1", 1, 0), BitField("COM1B", 5, 4), BitField("COM1A", 7, 6)
            )),
            Register("TCCR1B", 0x81, "Timer/Counter1 Control Register B", listOf(
                BitField("CS1",  2, 0), BitField("WGM12", 4, 3), BitField("ICES1", 6, 6), BitField("ICNC1", 7, 7)
            )),
            Register("TCCR1C", 0x82, "Timer/Counter1 Control Register C"),
            // TCNT1 is 16-bit: L at 0x84, H at 0x85
            Register("TCNT1L", 0x84, "Timer/Counter1 Low Byte"),
            Register("TCNT1H", 0x85, "Timer/Counter1 High Byte"),
            Register("ICR1L",  0x86, "Input Capture Register Low"),
            Register("ICR1H",  0x87, "Input Capture Register High"),
            Register("OCR1AL", 0x88, "Output Compare Register A Low"),
            Register("OCR1AH", 0x89, "Output Compare Register A High"),
            Register("OCR1BL", 0x8A, "Output Compare Register B Low"),
            Register("OCR1BH", 0x8B, "Output Compare Register B High"),
            Register("TIMSK1", 0x6F, "Timer/Counter1 Interrupt Mask Register", listOf(
                BitField("TOIE1", 0, 0), BitField("OCIE1A", 1, 1),
                BitField("OCIE1B", 2, 2), BitField("ICIE1", 5, 5)
            )),
        )),

        Peripheral("Timer2", listOf(
            Register("TIFR2",  0x37, "Timer/Counter2 Interrupt Flag Register"),
            Register("TCCR2A", 0xB0, "Timer/Counter2 Control Register A"),
            Register("TCCR2B", 0xB1, "Timer/Counter2 Control Register B", listOf(
                BitField("CS2", 2, 0), BitField("WGM22", 3, 3),
                BitField("FOC2B", 6, 6), BitField("FOC2A", 7, 7)
            )),
            Register("TCNT2",  0xB2, "Timer/Counter2 Value"),
            Register("OCR2A",  0xB3, "Output Compare Register A"),
            Register("OCR2B",  0xB4, "Output Compare Register B"),
            Register("TIMSK2", 0x70, "Timer/Counter2 Interrupt Mask Register"),
        )),

        Peripheral("ADC", listOf(
            Register("ADCL",   0x78, "ADC Data Register Low"),
            Register("ADCH",   0x79, "ADC Data Register High"),
            Register("ADCSRA", 0x7A, "ADC Control and Status Register A", listOf(
                BitField("ADPS",  2, 0, "Prescaler Select"),
                BitField("ADIE",  3, 3, "Interrupt Enable"),
                BitField("ADIF",  4, 4, "Interrupt Flag"),
                BitField("ADATE", 5, 5, "Auto Trigger Enable"),
                BitField("ADSC",  6, 6, "Start Conversion"),
                BitField("ADEN",  7, 7, "ADC Enable")
            )),
            Register("ADCSRB", 0x7B, "ADC Control and Status Register B", listOf(
                BitField("ADTS",  2, 0, "Auto Trigger Source"),
                BitField("ACME",  6, 6, "Analog Comparator Mux Enable")
            )),
            Register("ADMUX",  0x7C, "ADC Multiplexer Selection Register", listOf(
                BitField("MUX",   3, 0, "Analog Channel Selection"),
                BitField("ADLAR", 5, 5, "Left Adjust Result"),
                BitField("REFS",  7, 6, "Reference Selection")
            )),
        )),

        Peripheral("USART0", listOf(
            Register("UCSR0A", 0xC0, "USART Control and Status Register 0 A", listOf(
                BitField("MPCM0", 0, 0), BitField("U2X0", 1, 1), BitField("UPE0", 2, 2),
                BitField("DOR0", 3, 3), BitField("FE0", 4, 4), BitField("UDRE0", 5, 5),
                BitField("TXC0", 6, 6), BitField("RXC0", 7, 7)
            )),
            Register("UCSR0B", 0xC1, "USART Control and Status Register 0 B", listOf(
                BitField("TXB80", 0, 0), BitField("RXB80", 1, 1), BitField("UCSZ02", 2, 2),
                BitField("TXEN0", 3, 3), BitField("RXEN0", 4, 4),
                BitField("UDRIE0", 5, 5), BitField("TXCIE0", 6, 6), BitField("RXCIE0", 7, 7)
            )),
            Register("UCSR0C", 0xC2, "USART Control and Status Register 0 C", listOf(
                BitField("UCPOL0", 0, 0), BitField("UCSZ0", 2, 1), BitField("USBS0", 3, 3),
                BitField("UPM0", 5, 4), BitField("UMSEL0", 7, 6)
            )),
            Register("UBRR0L", 0xC4, "USART Baud Rate Register Low"),
            Register("UBRR0H", 0xC5, "USART Baud Rate Register High"),
            Register("UDR0",   0xC6, "USART I/O Data Register"),
        )),

        Peripheral("SPI", listOf(
            Register("SPCR0",  0x4C, "SPI Control Register", listOf(
                BitField("SPR",  1, 0, "Clock Rate Select"),
                BitField("CPHA", 2, 2, "Clock Phase"),
                BitField("CPOL", 3, 3, "Clock Polarity"),
                BitField("MSTR", 4, 4, "Master/Slave Select"),
                BitField("DORD", 5, 5, "Data Order"),
                BitField("SPE",  6, 6, "SPI Enable"),
                BitField("SPIE", 7, 7, "SPI Interrupt Enable")
            )),
            Register("SPSR0",  0x4D, "SPI Status Register", listOf(
                BitField("SPI2X", 0, 0, "Double SPI Speed Bit"),
                BitField("WCOL",  6, 6, "Write COLlision Flag"),
                BitField("SPIF",  7, 7, "SPI Interrupt Flag")
            )),
            Register("SPDR0",  0x4E, "SPI Data Register"),
        )),

        Peripheral("TWI", listOf(
            Register("TWBR",  0xB8, "TWI Bit Rate Register"),
            Register("TWSR",  0xB9, "TWI Status Register", listOf(
                BitField("TWPS", 1, 0, "Prescaler Bits"),
                BitField("TWS",  7, 3, "TWI Status")
            )),
            Register("TWAR",  0xBA, "TWI (Slave) Address Register", listOf(
                BitField("TWGCE", 0, 0, "General Call Recognition Enable Bit"),
                BitField("TWA",   7, 1, "TWI (Slave) Address")
            )),
            Register("TWDR",  0xBB, "TWI Data Register"),
            Register("TWCR",  0xBC, "TWI Control Register", listOf(
                BitField("TWIE",  0, 0, "TWI Interrupt Enable"),
                BitField("TWEN",  2, 2, "TWI Enable Bit"),
                BitField("TWWC",  3, 3, "TWI Write Collision Flag"),
                BitField("TWSTO", 4, 4, "TWI STOP Condition Bit"),
                BitField("TWSTA", 5, 5, "TWI START Condition Bit"),
                BitField("TWEA",  6, 6, "TWI Enable Acknowledge Bit"),
                BitField("TWINT", 7, 7, "TWI Interrupt Flag")
            )),
            Register("TWAMR", 0xBD, "TWI (Slave) Address Mask Register"),
        )),

        Peripheral("CPU", listOf(
            Register("SREG",   0x5F, "Status Register", listOf(
                BitField("C", 0, 0, "Carry Flag"),
                BitField("Z", 1, 1, "Zero Flag"),
                BitField("N", 2, 2, "Negative Flag"),
                BitField("V", 3, 3, "Two's Complement Overflow Flag"),
                BitField("S", 4, 4, "Sign Bit"),
                BitField("H", 5, 5, "Half Carry Flag"),
                BitField("T", 6, 6, "Bit Copy Storage"),
                BitField("I", 7, 7, "Global Interrupt Enable")
            )),
            Register("SPL",    0x5D, "Stack Pointer Low"),
            Register("SPH",    0x5E, "Stack Pointer High"),
            Register("SPMCSR", 0x57, "Store Program Memory Control and Status Register"),
            Register("MCUCR",  0x55, "MCU Control Register"),
            Register("MCUSR",  0x54, "MCU Status Register", listOf(
                BitField("PORF",  0, 0, "Power-on Reset Flag"),
                BitField("EXTRF", 1, 1, "External Reset Flag"),
                BitField("BORF",  2, 2, "Brown-out Reset Flag"),
                BitField("WDRF",  3, 3, "Watchdog Reset Flag")
            )),
            Register("CLKPR",  0x61, "Clock Prescale Register"),
        )),
    )

    // Minimum address we need to snapshot to cover all registers above.
    // The lowest register is PINB at 0x23; the highest is UDR0 at 0xC6.
    const val SNAPSHOT_BASE: Int = 0x20
    const val SNAPSHOT_SIZE: Int = 0xC7 - 0x20  // 167 bytes
}

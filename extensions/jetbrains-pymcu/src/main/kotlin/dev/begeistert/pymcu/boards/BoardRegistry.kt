// SPDX-License-Identifier: Apache-2.0
package dev.begeistert.pymcu.boards

/**
 * Catalog of all boards known to PyMCU, organized by manufacturer.
 * Flash sizes (flashKb) reflect available user flash after bootloader space,
 * matching the values displayed by PlatformIO.
 */
object BoardRegistry {

    val all: List<BoardEntry> = listOf(

        // ── Arduino ───────────────────────────────────────────────────────────

        BoardEntry("arduino_bt_mega168",        "Arduino BT ATmega168",                         "atmega168",   "Arduino", "avr",  16_000_000,  14, 1_024),
        BoardEntry("arduino_bt_mega328",        "Arduino BT ATmega328",                         "atmega328p",  "Arduino", "avr",  16_000_000,  28, 2_048),
        BoardEntry("arduino_duemil_mega168",    "Arduino Duemilanove / Diecimila ATmega168",    "atmega168",   "Arduino", "avr",  16_000_000,  14, 1_024),
        BoardEntry("arduino_duemil_mega328",    "Arduino Duemilanove / Diecimila ATmega328",    "atmega328p",  "Arduino", "avr",  16_000_000,  30, 2_048),
        BoardEntry("arduino_esplora",           "Arduino Esplora",                              "atmega32u4",  "Arduino", "avr",  16_000_000,  28, 2_048),
        BoardEntry("arduino_ethernet",          "Arduino Ethernet",                             "atmega328p",  "Arduino", "avr",  16_000_000,  31, 2_048),
        BoardEntry("arduino_fio",               "Arduino Fio",                                  "atmega328p",  "Arduino", "avr",   8_000_000,  30, 2_048),
        BoardEntry("arduino_industrial_101",    "Arduino Industrial 101",                       "atmega32u4",  "Arduino", "avr",  16_000_000,  28, 2_048),
        BoardEntry("arduino_leonardo",          "Arduino Leonardo",                             "atmega32u4",  "Arduino", "avr",  16_000_000,  28, 2_048),
        BoardEntry("arduino_lilypad_mega168",   "Arduino LilyPad ATmega168",                   "atmega168",   "Arduino", "avr",   8_000_000,  14, 1_024),
        BoardEntry("arduino_lilypad_mega328",   "Arduino LilyPad ATmega328",                   "atmega328p",  "Arduino", "avr",   8_000_000,  30, 2_048),
        BoardEntry("arduino_lilypad_usb",       "Arduino LilyPad USB",                         "atmega32u4",  "Arduino", "avr",   8_000_000,  28, 2_048),
        BoardEntry("arduino_mega",              "Arduino Mega",                                 "atmega2560",  "Arduino", "avr",  16_000_000, 248, 8_192),
        BoardEntry("arduino_mega_adk",          "Arduino Mega ADK",                             "atmega2560",  "Arduino", "avr",  16_000_000, 248, 8_192),
        BoardEntry("arduino_micro",             "Arduino Micro",                                "atmega32u4",  "Arduino", "avr",  16_000_000,  28, 2_048),
        BoardEntry("arduino_mini_mega168",      "Arduino Mini ATmega168",                       "atmega168",   "Arduino", "avr",  16_000_000,  14, 1_024),
        BoardEntry("arduino_mini_mega328",      "Arduino Mini ATmega328",                       "atmega328p",  "Arduino", "avr",  16_000_000,  28, 2_048),
        BoardEntry("arduino_nano_mega168",      "Arduino Nano ATmega168",                       "atmega168",   "Arduino", "avr",  16_000_000,  14, 1_024),
        BoardEntry("arduino_nano",              "Arduino Nano ATmega328",                       "atmega328p",  "Arduino", "avr",  16_000_000,  30, 2_048),
        BoardEntry("arduino_pro_mega168_3v3",   "Arduino Pro / Pro Mini ATmega168 (3.3V, 8MHz)","atmega168",  "Arduino", "avr",   8_000_000,  14, 1_024),
        BoardEntry("arduino_pro_mega168_5v",    "Arduino Pro / Pro Mini ATmega168 (5V, 16MHz)", "atmega168",  "Arduino", "avr",  16_000_000,  14, 1_024),
        BoardEntry("arduino_pro_mega328_3v3",   "Arduino Pro / Pro Mini ATmega328 (3.3V, 8MHz)","atmega328p", "Arduino", "avr",   8_000_000,  30, 2_048),
        BoardEntry("arduino_pro_mega328_5v",    "Arduino Pro / Pro Mini ATmega328 (5V, 16MHz)", "atmega328p", "Arduino", "avr",  16_000_000,  30, 2_048),
        BoardEntry("arduino_uno",               "Arduino Uno",                                  "atmega328p",  "Arduino", "avr",  16_000_000,  32, 2_048),
        BoardEntry("arduino_uno_r4_minima",     "Arduino Uno R4 Minima",                        "ra4m1",       "Arduino", "arm",  48_000_000, 256, 32_768),
        BoardEntry("arduino_uno_r4_wifi",       "Arduino Uno R4 WiFi",                          "ra4m1",       "Arduino", "arm",  48_000_000, 256, 32_768),
        BoardEntry("arduino_yun",               "Arduino Yun",                                  "atmega32u4",  "Arduino", "avr",  16_000_000,  28, 2_048),
        BoardEntry("arduino_zero",              "Arduino Zero",                                 "samd21g18a",  "Arduino", "arm",  48_000_000, 256, 32_768),

        // ── Atmel / Microchip — bare chips ────────────────────────────────────

        BoardEntry("atmega168_bare",   "ATmega168",   "atmega168",   "Atmel", "avr",  16_000_000,  16, 1_024),
        BoardEntry("atmega328p_bare",  "ATmega328P",  "atmega328p",  "Atmel", "avr",  16_000_000,  32, 2_048),
        BoardEntry("atmega32u4_bare",  "ATmega32U4",  "atmega32u4",  "Atmel", "avr",  16_000_000,  32, 2_560),
        BoardEntry("atmega88_bare",    "ATmega88",    "atmega88",    "Atmel", "avr",   8_000_000,   8, 1_024),
        BoardEntry("atmega2560_bare",  "ATmega2560",  "atmega2560",  "Atmel", "avr",  16_000_000, 256, 8_192),
        BoardEntry("attiny13_bare",    "ATtiny13",    "attiny13",    "Atmel", "avr",   9_600_000,   1,    64),
        BoardEntry("attiny44_bare",    "ATtiny44",    "attiny44",    "Atmel", "avr",   8_000_000,   4,   256),
        BoardEntry("attiny45_bare",    "ATtiny45",    "attiny45",    "Atmel", "avr",   8_000_000,   4,   256),
        BoardEntry("attiny84_bare",    "ATtiny84",    "attiny84",    "Atmel", "avr",   8_000_000,   8,   512),
        BoardEntry("attiny85_bare",    "ATtiny85",    "attiny85",    "Atmel", "avr",   8_000_000,   8,   512),
        BoardEntry("attiny2313_bare",  "ATtiny2313",  "attiny2313",  "Atmel", "avr",  20_000_000,   2,   128),
        BoardEntry("attiny4313_bare",  "ATtiny4313",  "attiny4313",  "Atmel", "avr",  20_000_000,   4,   256),

        // ── SparkFun ──────────────────────────────────────────────────────────

        BoardEntry("sparkfun_pro_micro_3v3",  "SparkFun Pro Micro 3.3V / 8MHz",  "atmega32u4", "SparkFun", "avr",  8_000_000,  28, 2_048),
        BoardEntry("sparkfun_pro_micro_5v",   "SparkFun Pro Micro 5V / 16MHz",   "atmega32u4", "SparkFun", "avr", 16_000_000,  28, 2_048),
        BoardEntry("sparkfun_mega_pro",       "SparkFun Mega Pro",               "atmega2560", "SparkFun", "avr", 16_000_000, 248, 8_192),
        BoardEntry("sparkfun_redboard",       "SparkFun RedBoard",               "atmega328p", "SparkFun", "avr", 16_000_000,  32, 2_048),

        // ── Adafruit ──────────────────────────────────────────────────────────

        BoardEntry("adafruit_trinket_3v3",  "Adafruit Trinket 3V / 8MHz",  "attiny85",  "Adafruit", "avr",  8_000_000,  8,  512),
        BoardEntry("adafruit_trinket_5v",   "Adafruit Trinket 5V / 16MHz", "attiny85",  "Adafruit", "avr", 16_000_000,  8,  512),
        BoardEntry("adafruit_gemma",        "Adafruit Gemma",              "attiny85",  "Adafruit", "avr",   8_000_000,  8,  512),
        BoardEntry("adafruit_flora",        "Adafruit Flora",              "atmega32u4","Adafruit", "avr",   8_000_000, 28, 2_048),
        BoardEntry("adafruit_metro_mega328","Adafruit Metro ATmega328",    "atmega328p","Adafruit", "avr",  16_000_000, 32, 2_048),
    )

    /** Boards grouped by manufacturer — drives the two-level tree. */
    val byManufacturer: Map<String, List<BoardEntry>> by lazy {
        all.groupBy { it.manufacturer }
            .toSortedMap()
    }

    fun findById(id: String): BoardEntry? = all.firstOrNull { it.id == id }

    fun findByChip(chip: String): List<BoardEntry> = all.filter { it.chip == chip }
}

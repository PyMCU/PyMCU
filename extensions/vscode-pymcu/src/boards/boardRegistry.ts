export interface BoardEntry {
    id: string;
    name: string;
    chip: string;
    manufacturer: string;
    arch: string;
    frequency: number;
    flashKb: number;
    ramBytes: number;
}

export const BOARDS: BoardEntry[] = [
    // ── Arduino ──────────────────────────────────────────────────────────────
    { id: 'arduino_bt_mega168',       name: 'Arduino BT ATmega168',                          chip: 'atmega168',   manufacturer: 'Arduino',   arch: 'avr',    frequency: 16_000_000, flashKb: 14,  ramBytes: 1_024 },
    { id: 'arduino_bt_mega328',       name: 'Arduino BT ATmega328',                          chip: 'atmega328p',  manufacturer: 'Arduino',   arch: 'avr',    frequency: 16_000_000, flashKb: 28,  ramBytes: 2_048 },
    { id: 'arduino_duemil_mega168',   name: 'Arduino Duemilanove / Diecimila ATmega168',     chip: 'atmega168',   manufacturer: 'Arduino',   arch: 'avr',    frequency: 16_000_000, flashKb: 14,  ramBytes: 1_024 },
    { id: 'arduino_duemil_mega328',   name: 'Arduino Duemilanove / Diecimila ATmega328',     chip: 'atmega328p',  manufacturer: 'Arduino',   arch: 'avr',    frequency: 16_000_000, flashKb: 30,  ramBytes: 2_048 },
    { id: 'arduino_esplora',          name: 'Arduino Esplora',                               chip: 'atmega32u4',  manufacturer: 'Arduino',   arch: 'avr',    frequency: 16_000_000, flashKb: 28,  ramBytes: 2_048 },
    { id: 'arduino_ethernet',         name: 'Arduino Ethernet',                              chip: 'atmega328p',  manufacturer: 'Arduino',   arch: 'avr',    frequency: 16_000_000, flashKb: 31,  ramBytes: 2_048 },
    { id: 'arduino_fio',              name: 'Arduino Fio',                                   chip: 'atmega328p',  manufacturer: 'Arduino',   arch: 'avr',    frequency:  8_000_000, flashKb: 30,  ramBytes: 2_048 },
    { id: 'arduino_industrial_101',   name: 'Arduino Industrial 101',                        chip: 'atmega32u4',  manufacturer: 'Arduino',   arch: 'avr',    frequency: 16_000_000, flashKb: 28,  ramBytes: 2_048 },
    { id: 'arduino_leonardo',         name: 'Arduino Leonardo',                              chip: 'atmega32u4',  manufacturer: 'Arduino',   arch: 'avr',    frequency: 16_000_000, flashKb: 28,  ramBytes: 2_048 },
    { id: 'arduino_lilypad_mega168',  name: 'Arduino LilyPad ATmega168',                    chip: 'atmega168',   manufacturer: 'Arduino',   arch: 'avr',    frequency:  8_000_000, flashKb: 14,  ramBytes: 1_024 },
    { id: 'arduino_lilypad_mega328',  name: 'Arduino LilyPad ATmega328',                    chip: 'atmega328p',  manufacturer: 'Arduino',   arch: 'avr',    frequency:  8_000_000, flashKb: 30,  ramBytes: 2_048 },
    { id: 'arduino_lilypad_usb',      name: 'Arduino LilyPad USB',                          chip: 'atmega32u4',  manufacturer: 'Arduino',   arch: 'avr',    frequency:  8_000_000, flashKb: 28,  ramBytes: 2_048 },
    { id: 'arduino_mega',             name: 'Arduino Mega',                                  chip: 'atmega2560',  manufacturer: 'Arduino',   arch: 'avr',    frequency: 16_000_000, flashKb: 248, ramBytes: 8_192 },
    { id: 'arduino_mega_adk',         name: 'Arduino Mega ADK',                              chip: 'atmega2560',  manufacturer: 'Arduino',   arch: 'avr',    frequency: 16_000_000, flashKb: 248, ramBytes: 8_192 },
    { id: 'arduino_micro',            name: 'Arduino Micro',                                 chip: 'atmega32u4',  manufacturer: 'Arduino',   arch: 'avr',    frequency: 16_000_000, flashKb: 28,  ramBytes: 2_048 },
    { id: 'arduino_mini_mega168',     name: 'Arduino Mini ATmega168',                        chip: 'atmega168',   manufacturer: 'Arduino',   arch: 'avr',    frequency: 16_000_000, flashKb: 14,  ramBytes: 1_024 },
    { id: 'arduino_mini_mega328',     name: 'Arduino Mini ATmega328',                        chip: 'atmega328p',  manufacturer: 'Arduino',   arch: 'avr',    frequency: 16_000_000, flashKb: 28,  ramBytes: 2_048 },
    { id: 'arduino_nano_mega168',     name: 'Arduino Nano ATmega168',                        chip: 'atmega168',   manufacturer: 'Arduino',   arch: 'avr',    frequency: 16_000_000, flashKb: 14,  ramBytes: 1_024 },
    { id: 'arduino_nano',             name: 'Arduino Nano ATmega328',                        chip: 'atmega328p',  manufacturer: 'Arduino',   arch: 'avr',    frequency: 16_000_000, flashKb: 30,  ramBytes: 2_048 },
    { id: 'arduino_pro_mega168_3v3',  name: 'Arduino Pro / Pro Mini ATmega168 (3.3V, 8MHz)', chip: 'atmega168',   manufacturer: 'Arduino',   arch: 'avr',    frequency:  8_000_000, flashKb: 14,  ramBytes: 1_024 },
    { id: 'arduino_pro_mega168_5v',   name: 'Arduino Pro / Pro Mini ATmega168 (5V, 16MHz)',  chip: 'atmega168',   manufacturer: 'Arduino',   arch: 'avr',    frequency: 16_000_000, flashKb: 14,  ramBytes: 1_024 },
    { id: 'arduino_pro_mega328_3v3',  name: 'Arduino Pro / Pro Mini ATmega328 (3.3V, 8MHz)', chip: 'atmega328p',  manufacturer: 'Arduino',   arch: 'avr',    frequency:  8_000_000, flashKb: 30,  ramBytes: 2_048 },
    { id: 'arduino_pro_mega328_5v',   name: 'Arduino Pro / Pro Mini ATmega328 (5V, 16MHz)',  chip: 'atmega328p',  manufacturer: 'Arduino',   arch: 'avr',    frequency: 16_000_000, flashKb: 30,  ramBytes: 2_048 },
    { id: 'arduino_uno',              name: 'Arduino Uno',                                   chip: 'atmega328p',  manufacturer: 'Arduino',   arch: 'avr',    frequency: 16_000_000, flashKb: 32,  ramBytes: 2_048 },
    { id: 'arduino_yun',              name: 'Arduino Yun',                                   chip: 'atmega32u4',  manufacturer: 'Arduino',   arch: 'avr',    frequency: 16_000_000, flashKb: 28,  ramBytes: 2_048 },
    { id: 'arduino_zero',             name: 'Arduino Zero',                                  chip: 'samd21g18a',  manufacturer: 'Arduino',   arch: 'arm',    frequency: 48_000_000, flashKb: 256, ramBytes: 32_768 },
    // ── Atmel / Microchip — bare chips ────────────────────────────────────────
    { id: 'atmega168_bare',   name: 'ATmega168',   chip: 'atmega168',   manufacturer: 'Atmel', arch: 'avr', frequency: 16_000_000, flashKb: 16,  ramBytes: 1_024 },
    { id: 'atmega328p_bare',  name: 'ATmega328P',  chip: 'atmega328p',  manufacturer: 'Atmel', arch: 'avr', frequency: 16_000_000, flashKb: 32,  ramBytes: 2_048 },
    { id: 'atmega32u4_bare',  name: 'ATmega32U4',  chip: 'atmega32u4',  manufacturer: 'Atmel', arch: 'avr', frequency: 16_000_000, flashKb: 32,  ramBytes: 2_560 },
    { id: 'atmega88_bare',    name: 'ATmega88',    chip: 'atmega88',    manufacturer: 'Atmel', arch: 'avr', frequency:  8_000_000, flashKb:  8,  ramBytes: 1_024 },
    { id: 'atmega2560_bare',  name: 'ATmega2560',  chip: 'atmega2560',  manufacturer: 'Atmel', arch: 'avr', frequency: 16_000_000, flashKb: 256, ramBytes: 8_192 },
    { id: 'attiny13_bare',    name: 'ATtiny13',    chip: 'attiny13',    manufacturer: 'Atmel', arch: 'avr', frequency:  9_600_000, flashKb:  1,  ramBytes:    64 },
    { id: 'attiny44_bare',    name: 'ATtiny44',    chip: 'attiny44',    manufacturer: 'Atmel', arch: 'avr', frequency:  8_000_000, flashKb:  4,  ramBytes:   256 },
    { id: 'attiny45_bare',    name: 'ATtiny45',    chip: 'attiny45',    manufacturer: 'Atmel', arch: 'avr', frequency:  8_000_000, flashKb:  4,  ramBytes:   256 },
    { id: 'attiny84_bare',    name: 'ATtiny84',    chip: 'attiny84',    manufacturer: 'Atmel', arch: 'avr', frequency:  8_000_000, flashKb:  8,  ramBytes:   512 },
    { id: 'attiny85_bare',    name: 'ATtiny85',    chip: 'attiny85',    manufacturer: 'Atmel', arch: 'avr', frequency:  8_000_000, flashKb:  8,  ramBytes:   512 },
    { id: 'attiny2313_bare',  name: 'ATtiny2313',  chip: 'attiny2313',  manufacturer: 'Atmel', arch: 'avr', frequency: 20_000_000, flashKb:  2,  ramBytes:   128 },
    { id: 'attiny4313_bare',  name: 'ATtiny4313',  chip: 'attiny4313',  manufacturer: 'Atmel', arch: 'avr', frequency: 20_000_000, flashKb:  4,  ramBytes:   256 },
    // ── SparkFun ─────────────────────────────────────────────────────────────
    { id: 'sparkfun_pro_micro_3v3', name: 'SparkFun Pro Micro 3.3V / 8MHz',  chip: 'atmega32u4', manufacturer: 'SparkFun', arch: 'avr', frequency:  8_000_000, flashKb: 28,  ramBytes: 2_048 },
    { id: 'sparkfun_pro_micro_5v',  name: 'SparkFun Pro Micro 5V / 16MHz',   chip: 'atmega32u4', manufacturer: 'SparkFun', arch: 'avr', frequency: 16_000_000, flashKb: 28,  ramBytes: 2_048 },
    { id: 'sparkfun_mega_pro',      name: 'SparkFun Mega Pro',               chip: 'atmega2560', manufacturer: 'SparkFun', arch: 'avr', frequency: 16_000_000, flashKb: 248, ramBytes: 8_192 },
    { id: 'sparkfun_redboard',      name: 'SparkFun RedBoard',               chip: 'atmega328p', manufacturer: 'SparkFun', arch: 'avr', frequency: 16_000_000, flashKb: 32,  ramBytes: 2_048 },
    // ── Adafruit ─────────────────────────────────────────────────────────────
    { id: 'adafruit_trinket_3v3',   name: 'Adafruit Trinket 3V / 8MHz',   chip: 'attiny85',   manufacturer: 'Adafruit', arch: 'avr', frequency:  8_000_000, flashKb:  8, ramBytes:   512 },
    { id: 'adafruit_trinket_5v',    name: 'Adafruit Trinket 5V / 16MHz',  chip: 'attiny85',   manufacturer: 'Adafruit', arch: 'avr', frequency: 16_000_000, flashKb:  8, ramBytes:   512 },
    { id: 'adafruit_gemma',         name: 'Adafruit Gemma',               chip: 'attiny85',   manufacturer: 'Adafruit', arch: 'avr', frequency:  8_000_000, flashKb:  8, ramBytes:   512 },
    { id: 'adafruit_flora',         name: 'Adafruit Flora',               chip: 'atmega32u4', manufacturer: 'Adafruit', arch: 'avr', frequency:  8_000_000, flashKb: 28, ramBytes: 2_048 },
    { id: 'adafruit_metro_mega328', name: 'Adafruit Metro ATmega328',     chip: 'atmega328p', manufacturer: 'Adafruit', arch: 'avr', frequency: 16_000_000, flashKb: 32, ramBytes: 2_048 },
];

const _byManufacturer: Map<string, BoardEntry[]> = new Map();
for (const b of BOARDS) {
    let list = _byManufacturer.get(b.manufacturer);
    if (!list) { list = []; _byManufacturer.set(b.manufacturer, list); }
    list.push(b);
}

export const byManufacturer: ReadonlyMap<string, BoardEntry[]> =
    new Map([..._byManufacturer.entries()].sort(([a], [b]) => a.localeCompare(b)));

export function findById(id: string): BoardEntry | undefined {
    return BOARDS.find(b => b.id === id);
}

export function findByChip(chip: string): BoardEntry[] {
    return BOARDS.filter(b => b.chip === chip);
}

export function formatFrequency(hz: number): string {
    return hz >= 1_000_000 ? `${hz / 1_000_000} MHz` : `${hz / 1_000} kHz`;
}

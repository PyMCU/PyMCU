# tone-buzzer -- simple melody on OC2A (PB3 / Arduino D11)
#
# Connects a passive buzzer between D11 and GND (no resistor needed for most
# 5V buzzers).  Plays a repeating C-major scale: C4, D4, E4, F4, G4, A4, B4, C5.
#
# Uses Timer2 CTC with hardware toggle -- zero CPU overhead during tone playback.
# The tone pin is hardwired to OC2A (PB3 / D11); no other pins can be used.
#
# See also: pymcu.hal.tone module for API details.
from pymcu.types import uint16
from pymcu.time import delay_ms
from pymcu.hal.tone import tone, noTone


# C-major scale: C4, D4, E4, F4, G4, A4, B4, C5  (Hz)
NOTE_C4: uint16 = 262
NOTE_D4: uint16 = 294
NOTE_E4: uint16 = 330
NOTE_F4: uint16 = 349
NOTE_G4: uint16 = 392
NOTE_A4: uint16 = 440
NOTE_B4: uint16 = 494
NOTE_C5: uint16 = 523

BEAT_MS: uint16 = 300


def play_note(freq: uint16, duration_ms: uint16):
    tone(freq)
    delay_ms(duration_ms)
    noTone()
    delay_ms(50)    # brief silence between notes


def main():
    while True:
        play_note(NOTE_C4, BEAT_MS)
        play_note(NOTE_D4, BEAT_MS)
        play_note(NOTE_E4, BEAT_MS)
        play_note(NOTE_F4, BEAT_MS)
        play_note(NOTE_G4, BEAT_MS)
        play_note(NOTE_A4, BEAT_MS)
        play_note(NOTE_B4, BEAT_MS)
        play_note(NOTE_C5, BEAT_MS)
        delay_ms(500)

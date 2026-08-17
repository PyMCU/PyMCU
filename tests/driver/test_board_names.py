# tests/driver/test_board_names.py
#
# `arduino_uno` is a key: it belongs in pyproject.toml and on the command line.
# The thing a person owns is an Arduino Uno Rev3, and that is what the UI, the
# board list and the library pages have to call it.

from src.driver.core.boards import (
    BOARD_CHIPS,
    board_label,
    chip_label,
)
from src.driver.core.project_config import available_boards


class TestBoardLabels:
    def test_the_name_on_the_box(self):
        assert board_label("arduino_uno") == "Arduino Uno Rev3"
        assert board_label("arduino_mega") == "Arduino Mega 2560 Rev3"
        assert board_label("raspberry_pi_pico2") == "Raspberry Pi Pico 2"

    def test_an_unknown_board_keeps_its_key(self):
        """Better a bare identifier than a wrong product name."""
        assert board_label("some_new_board") == "some_new_board"

    def test_every_grouped_board_has_a_label_or_is_a_bare_chip(self):
        """
        A board with no label is only acceptable when the key *is* the part
        number -- `attiny85` is a chip you wire up, not a product.
        """
        for board in BOARD_CHIPS:
            label = board_label(board)
            assert label != "" and (label != board or board.startswith(("attiny", "atmega", "pic", "rp")))


class TestChipLabels:
    def test_silkscreen_capitalisation(self):
        assert chip_label("atmega328p") == "ATmega328P"
        assert chip_label("atmega32u4") == "ATmega32U4"
        assert chip_label("attiny13a") == "ATtiny13A"
        assert chip_label("rp2040") == "RP2040"
        assert chip_label("pic16f877a") == "PIC16F877A"

    def test_case_does_not_matter_going_in(self):
        assert chip_label("ATMEGA328P") == "ATmega328P"

    def test_an_unknown_chip_is_returned_unchanged(self):
        assert chip_label("stm32f411") == "stm32f411"

    def test_every_known_chip_is_labelled(self):
        missing = [chip for chip in set(BOARD_CHIPS.values()) if chip_label(chip) == chip]
        assert missing == [], f"chips without a label: {missing}"


class TestBoardsPayload:
    def test_the_picker_carries_both_the_key_and_the_name(self):
        groups = available_boards([])
        uno = next(b for g in groups for b in g["boards"] if b["name"] == "arduino_uno")
        assert uno["label"] == "Arduino Uno Rev3"
        assert uno["chip_label"] == "ATmega328P"
        # The key stays: it is what gets written to pyproject.toml.
        assert uno["name"] == "arduino_uno"

# tests/driver/test_board_names.py
#
# `arduino_uno` is a key: it belongs in pyproject.toml and on the command line.
# The thing a person owns is an Arduino Uno Rev3, and that is what the UI, the
# board list and the library pages have to call it.

from src.driver.core.boards import (
    BOARD_CHIPS,
    board_label,
    chip_label,
    suggest_boards,
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


class TestBoardSuggestions:
    """
    What a message says after refusing a board name (issue #198). The short form of the real
    name is what people write, and it is the case difflib alone does not cover: `uno` against
    `arduino_uno` scores 0.43, well under any cutoff worth having.
    """

    def test_a_short_form_finds_the_full_name(self):
        assert suggest_boards("uno") == ["arduino_uno"]
        assert suggest_boards("mega") == ["arduino_mega"]

    def test_a_slip_difflib_can_see_is_still_found(self):
        # No substring relation either way, so this is the half the substring rule cannot do.
        assert "rp2040" in suggest_boards("rp2400")

    def test_a_prefix_shared_by_several_offers_the_shortest_completions(self):
        # Alphabetical order would put arduino_uno fourth and the cap would drop it, which is
        # the one name a person typing `arduino` is most likely to have meant.
        assert suggest_boards("arduino") == ["arduino_uno", "arduino_mega", "arduino_nano"]

    def test_nothing_close_suggests_nothing(self):
        assert suggest_boards("banana_pi_zz99") == []

    def test_an_empty_name_suggests_nothing(self):
        # `board = ""` is a real thing to write, and `"" in name` is true of every board.
        assert suggest_boards("") == []

    def test_an_extension_board_is_offered_like_any_other(self):
        assert suggest_boards("feather", {"adafruit_feather": "rp2040"}) == ["adafruit_feather"]

    def test_the_list_is_capped(self):
        assert len(suggest_boards("attiny")) <= 3

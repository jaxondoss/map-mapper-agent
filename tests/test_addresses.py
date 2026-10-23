from pathlib import Path

import pytest

from madmapper_agent.addresses import AddressBook, AddressError


def test_comment_keys_are_not_aliases(book):
    assert "_comment" not in book.surfaces
    assert "_comment" not in book.triggers


def test_opacity_address_from_friendly_name(book):
    assert book.opacity_address("front wall") == "/surfaces/FrontWall/opacity"
    assert book.opacity_address("  FRONT WALL ") == "/surfaces/FrontWall/opacity"


def test_opacity_address_full_address_and_fallback(book):
    assert book.opacity_address("floor") == "/custom/floor/opacity"
    assert book.opacity_address("Quad1") == "/surfaces/Quad1/opacity"


def test_unknown_trigger_lists_known_names(book):
    with pytest.raises(AddressError, match="intro scene"):
        book.trigger_address("finale")


def test_bundled_example_file_loads():
    path = Path(__file__).resolve().parent.parent / "addresses.json"
    book = AddressBook.load(path)
    assert book.surfaces and book.triggers
    assert not any(k.startswith("_") for k in [*book.surfaces, *book.triggers])

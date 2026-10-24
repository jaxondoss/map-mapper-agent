from pathlib import Path

import pytest

from madmapper_agent import actions
from madmapper_agent.addresses import AddressBook
from madmapper_agent.materials import Catalog
from madmapper_agent.presets import PresetError, PresetLibrary

ROOT = Path(__file__).resolve().parent.parent
HS = "HandStringVisualizer"


@pytest.fixture
def library():
    return PresetLibrary.load(ROOT / "presets.json")


@pytest.fixture
def catalog():
    return Catalog.load(ROOT / "materials")


@pytest.fixture
def pbook():
    return AddressBook.from_dict({
        "parameters": {f"{HS}.glow": "/m/glow", f"{HS}.colorMode": "/m/colorMode", f"{HS}.sensitivity": "/m/sens"},
    })


def test_every_preset_uses_real_inputs_in_range(library, catalog):
    assert library.material == HS
    for preset in library.presets.values():
        for name, value in preset.values.items():
            spec = catalog.spec(HS, name)
            assert spec is not None, f"{preset.name}: unknown input {name}"
            assert spec.clamp(value) == value, f"{preset.name}: {name}={value} out of range"


def test_apply_preset_sends_mapped_and_reports_skipped(osc, pbook, catalog, library):
    result = actions.apply_preset(osc, pbook, catalog, library, "RAVE")
    sent = {a: v[0] for a, v in osc.sent}
    assert sent == {"/m/glow": 2.0, "/m/colorMode": 0, "/m/sens": 2.4}
    assert "skipped" in result and "pullSpeed" in result


def test_apply_preset_uses_template(osc, catalog, library):
    book = AddressBook.from_dict({"parameter_address_template": "/x/{material}/{param}"})
    actions.apply_preset(osc, book, catalog, library, "chill")
    assert ("/x/HandStringVisualizer/glow", [0.8]) in osc.sent


def test_apply_preset_with_no_mapped_params_is_an_error(osc, catalog, library):
    with pytest.raises(ValueError, match="sent nothing"):
        actions.apply_preset(osc, AddressBook(), catalog, library, "chill")


def test_unknown_preset(osc, pbook, catalog, library):
    with pytest.raises(PresetError, match="rave"):
        actions.apply_preset(osc, pbook, catalog, library, "disco")


def test_set_parameter_clamps_and_normalizes(osc, catalog):
    book = AddressBook.from_dict({"parameters": {f"{HS}.sensitivity": "/m/sens", f"{HS}.colorMode": "/m/cm"}})
    actions.set_parameter(osc, book, catalog, f"{HS}.sensitivity", 9)
    book.parameter_scale = "normalized"
    actions.set_parameter(osc, book, catalog, "sensitivity", 1.0, default_material=HS)
    actions.set_parameter(osc, book, catalog, f"{HS}.colorMode", 2)
    assert [v[0] for _, v in osc.sent] == [4.0, 0.25, pytest.approx(2 / 3)]


def test_unknown_parameter_on_known_material(osc, catalog):
    book = AddressBook.from_dict({"parameter_address_template": "/x/{material}/{param}"})
    with pytest.raises(ValueError, match="no parameter 'wobble'"):
        actions.set_parameter(osc, book, catalog, "GridSweep.wobble", 1)

from pathlib import Path

import pytest

from madmapper_agent import actions
from madmapper_agent.addresses import AddressBook
from madmapper_agent.presets import PresetError, PresetLibrary, read_isf_inputs

ROOT = Path(__file__).resolve().parent.parent


@pytest.fixture
def library():
    return PresetLibrary.load(ROOT / "presets.json")


@pytest.fixture
def pbook():
    return AddressBook.from_dict({
        "parameters": {"_comment": "x", "glow": "/m/glow", "colorMode": "/m/colorMode", "sensitivity": "/m/sens"},
    })


def test_every_preset_uses_real_inputs_in_range(library):
    assert library.inputs, "shader header should parse"
    for preset in library.presets.values():
        for name, value in preset.values.items():
            spec = library.input(name)
            assert spec is not None, f"{preset.name}: unknown input {name}"
            assert spec.clamp(value) == value, f"{preset.name}: {name}={value} out of range"


def test_shader_header_reads_ranges():
    inputs = read_isf_inputs(ROOT / "materials" / "HandStringVisualizer.fs")
    assert inputs["sensitivity"].max == 4.0
    assert inputs["colorMode"].values == [0, 1, 2, 3]
    assert "waveform" not in inputs  # audio inputs are fed by MadMapper, not OSC


def test_apply_preset_sends_mapped_and_reports_skipped(osc, pbook, library):
    result = actions.apply_preset(osc, pbook, library, "RAVE")
    sent = dict((a, v[0]) for a, v in osc.sent)
    assert sent == {"/m/glow": 2.0, "/m/colorMode": 0, "/m/sens": 2.4}
    assert "skipped" in result and "pullSpeed" in result


def test_apply_preset_with_no_mapped_params_is_an_error(osc, library):
    with pytest.raises(ValueError, match="sent nothing"):
        actions.apply_preset(osc, AddressBook(), library, "chill")


def test_unknown_preset(osc, pbook, library):
    with pytest.raises(PresetError, match="rave"):
        actions.apply_preset(osc, pbook, library, "disco")


def test_set_parameter_clamps_and_normalizes(osc, library):
    book = AddressBook.from_dict({"parameters": {"sensitivity": "/m/sens", "colorMode": "/m/cm"}})
    actions.set_parameter(osc, book, library, "sensitivity", 9)
    book.parameter_scale = "normalized"
    actions.set_parameter(osc, book, library, "sensitivity", 1.0)
    actions.set_parameter(osc, book, library, "colorMode", 2)
    assert [v[0] for _, v in osc.sent] == [4.0, 0.25, pytest.approx(2 / 3)]

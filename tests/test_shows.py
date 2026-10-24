import json
from pathlib import Path

import pytest

from madmapper_agent.addresses import AddressBook
from madmapper_agent.materials import Catalog
from madmapper_agent.osc import OscSender
from madmapper_agent.shows import Show, ShowError, ShowLibrary, run_show

ROOT = Path(__file__).resolve().parent.parent


@pytest.fixture
def book():
    return AddressBook.from_dict({
        "surfaces": {"grid": "Grid"},
        "triggers": {"go": "/cue/go"},
        "parameter_address_template": "/m/{material}/{param}",
    })


@pytest.fixture
def catalog():
    return Catalog.load(ROOT / "materials")


def make(steps):
    return Show("t", steps=steps)


def test_bundled_shows_compile_against_example_config(catalog):
    book = AddressBook.load(ROOT / "addresses.json")
    shows = ShowLibrary.load(ROOT / "shows")
    assert {"koi_frame", "grid_reveal", "holo_hands", "chain_hang"} <= set(shows.shows)
    for show in shows.shows.values():
        events = show.compile(book, catalog)
        assert events and events == sorted(events, key=lambda e: e.time)
        for s in show.surfaces.values():
            assert catalog.material_name(s["material"]), f"{show.name}: unknown material {s['material']}"


def test_ramp_expands_to_frames(book, catalog):
    events = make([{"at": 1.0, "set": "GridSweep.progress", "from": 0, "to": 1, "seconds": 1.0}]).compile(book, catalog)
    assert len(events) == 31
    assert events[0].time == 1.0 and events[0].value == 0.0
    assert events[-1].time == 2.0 and events[-1].value == 1.0
    assert {e.address for e in events} == {"/m/GridSweep/progress"}


def test_ramp_continues_from_last_value(book, catalog):
    events = make([
        {"at": 0, "opacity": "grid", "to": 1},
        {"at": 2, "opacity": "grid", "to": 0, "seconds": 0.5},
    ]).compile(book, catalog)
    assert events[1].value == 1.0 and events[-1].value == 0.0


def test_param_ramp_without_start_is_rejected(book, catalog):
    with pytest.raises(ShowError, match="needs 'from'"):
        make([{"at": 0, "set": "HoloHands.reach", "to": 1, "seconds": 2}]).compile(book, catalog)


def test_bad_steps_name_the_step(book, catalog):
    with pytest.raises(ShowError, match="step 1 .*trigger"):
        make([{"at": 0, "trigger": "nope"}]).compile(book, catalog)
    with pytest.raises(ShowError, match="exactly one"):
        make([{"at": 0, "opacity": "grid", "set": "x", "to": 1}]).compile(book, catalog)
    with pytest.raises(ShowError, match="no parameter"):
        make([{"at": 0, "set": "GridSweep.wobble", "to": 1}]).compile(book, catalog)


def test_values_are_clamped(book, catalog):
    events = make([{"at": 0, "set": "GridSweep.progress", "to": 5}]).compile(book, catalog)
    assert events[0].value == 1.0


def test_run_show_sends_in_time_order(book, catalog):
    slept = []
    osc = OscSender(dry_run=True, sleep=slept.append)
    fake_now = iter([0.0] + [0.0] * 100)
    show = make([
        {"at": 1.0, "trigger": "go"},
        {"at": 0.0, "opacity": "grid", "to": 1},
        {"at": 0.5, "osc": "/raw", "args": [1, "a"]},
    ])
    result = run_show(osc, book, show, catalog, clock=lambda: next(fake_now))
    assert osc.sent == [("/surfaces/Grid/opacity", [1.0]), ("/raw", [1, "a"]), ("/cue/go", [1.0])]
    assert slept == [0.5, 1.0]
    assert "3 messages" in result


def test_show_file_round_trip(tmp_path, book, catalog):
    (tmp_path / "Intro.json").write_text(json.dumps({"description": "d", "steps": [{"at": 0, "opacity": "grid", "to": 1}]}))
    lib = ShowLibrary.load(tmp_path)
    assert lib.get("intro").description == "d"
    with pytest.raises(ShowError, match="Known shows: Intro"):
        lib.get("outro")

import pytest

from madmapper_agent import actions


def test_set_opacity_clamps(osc, book):
    actions.set_opacity(osc, book, "front wall", 1.7)
    assert osc.sent == [("/surfaces/FrontWall/opacity", [1.0])]


def test_trigger_sends_one(osc, book):
    actions.trigger(osc, book, "Intro Scene")
    assert osc.sent == [("/scenes/Intro/select", [1.0])]


def test_fade_up_steps_and_timing(book):
    from madmapper_agent.osc import OscSender

    slept = []
    osc = OscSender(dry_run=True, sleep=slept.append)
    actions.fade(osc, book, "front wall", 1.0, 2.0)
    values = [args[0] for _, args in osc.sent]
    assert values[0] == 0.0 and values[-1] == pytest.approx(1.0)
    assert values == sorted(values)
    assert len(values) == 2 * actions.FADE_RATE_HZ + 1
    assert sum(slept) == pytest.approx(2.0)


def test_fade_zero_seconds_jumps(osc, book):
    actions.fade(osc, book, "front wall", 0.0, 0, from_value=0.8)
    assert [a[0] for _, a in osc.sent] == [0.8, 0.0]


def test_raw_rejects_bad_address(osc):
    with pytest.raises(ValueError):
        actions.send_raw(osc, "no-slash", [1])

"""Agent loop tests with a scripted fake client (no network, no API key)."""

from types import SimpleNamespace

from madmapper_agent.agent import MadMapperAgent


def text(t):
    return SimpleNamespace(type="text", text=t)


def tool_use(id_, name, inp):
    return SimpleNamespace(type="tool_use", id=id_, name=name, input=inp)


def reply(stop_reason, *content):
    return SimpleNamespace(stop_reason=stop_reason, content=list(content))


class FakeClient:
    def __init__(self, responses):
        self.responses = list(responses)
        self.calls = []
        self.beta = SimpleNamespace(messages=SimpleNamespace(create=self._create))

    def _create(self, **kwargs):
        self.calls.append({**kwargs, "messages": list(kwargs["messages"])})
        return self.responses.pop(0)


def test_runs_tools_then_returns_text(osc, book):
    client = FakeClient([
        reply("tool_use",
              tool_use("t1", "set_opacity", {"surface": "front wall", "value": 1}),
              tool_use("t2", "trigger", {"name": "intro scene"})),
        reply("end_turn", text("Front wall up, intro triggered.")),
    ])
    agent = MadMapperAgent(osc, book, client=client)

    assert agent.run("show the front wall and start the intro") == "Front wall up, intro triggered."
    assert osc.sent == [("/surfaces/FrontWall/opacity", [1.0]), ("/scenes/Intro/select", [1.0])]

    # Both results returned together in one user message, matching tool ids.
    results = client.calls[1]["messages"][-1]
    assert results["role"] == "user"
    assert [r["tool_use_id"] for r in results["content"]] == ["t1", "t2"]
    assert not any(r["is_error"] for r in results["content"])
    assert "front wall" in client.calls[0]["system"]


def test_bad_tool_input_becomes_error_result(osc, book):
    client = FakeClient([
        reply("tool_use", tool_use("t1", "trigger", {"name": "finale"})),
        reply("end_turn", text("There's no 'finale' trigger configured.")),
    ])
    agent = MadMapperAgent(osc, book, client=client)
    agent.run("run the finale")

    result = client.calls[1]["messages"][-1]["content"][0]
    assert result["is_error"] is True
    assert "Unknown trigger" in result["content"]
    assert osc.sent == []


def test_refusal_stops_without_sending(osc, book):
    client = FakeClient([reply("refusal")])
    agent = MadMapperAgent(osc, book, client=client)
    assert "declined" in agent.run("anything")
    assert osc.sent == []


def test_preset_tool(osc):
    from pathlib import Path

    from madmapper_agent.addresses import AddressBook
    from madmapper_agent.presets import PresetLibrary

    from madmapper_agent.materials import Catalog
    from madmapper_agent.shows import ShowLibrary

    root = Path(__file__).resolve().parent.parent
    library = PresetLibrary.load(root / "presets.json")
    book = AddressBook.from_dict({"parameters": {"HandStringVisualizer.glow": "/m/glow"}})
    client = FakeClient([
        reply("tool_use", tool_use("t1", "apply_preset", {"name": "neon"})),
        reply("end_turn", text("Switched to neon.")),
    ])
    agent = MadMapperAgent(
        osc, book, client=client, library=library,
        catalog=Catalog.load(root / "materials"), shows=ShowLibrary.load(root / "shows"),
    )
    assert agent.run("go neon") == "Switched to neon."
    assert osc.sent == [("/m/glow", [2.4])]
    system = client.calls[0]["system"]
    assert "neon" in system and "stringWidth" in system
    assert "koi_frame" in system and "HoloHands" in system


def test_run_show_tool(book):
    from types import SimpleNamespace

    from madmapper_agent.osc import OscSender
    from madmapper_agent.shows import Show, ShowLibrary

    osc = OscSender(dry_run=True, sleep=lambda s: None)
    shows = ShowLibrary({"intro": Show("intro", steps=[{"at": 0, "trigger": "intro scene"}])})
    client = FakeClient([
        reply("tool_use", tool_use("t1", "run_show", {"name": "Intro"})),
        reply("end_turn", text("Played the intro.")),
    ])
    agent = MadMapperAgent(osc, book, client=client, shows=shows)
    assert agent.run("play the intro") == "Played the intro."
    assert osc.sent == [("/scenes/Intro/select", [1.0])]

import pytest

from madmapper_agent.addresses import AddressBook
from madmapper_agent.osc import OscSender


@pytest.fixture
def book():
    return AddressBook.from_dict({
        "_comment": "ignored",
        "surfaces": {"_comment": "ignored", "Front Wall": "FrontWall", "floor": "/custom/floor/opacity"},
        "triggers": {"_comment": "ignored", "intro scene": "/scenes/Intro/select"},
    })


@pytest.fixture
def osc():
    return OscSender(dry_run=True, sleep=lambda s: None)

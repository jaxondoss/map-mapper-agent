"""Control MadMapper over OSC with plain-English instructions, driven by Claude."""

from .addresses import AddressBook, AddressError
from .osc import OscSender

__all__ = ["AddressBook", "AddressError", "OscSender"]

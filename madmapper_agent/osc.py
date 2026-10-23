"""Thin OSC-over-UDP sender with a dry-run mode."""

from __future__ import annotations

import logging
import time
from typing import Callable, Sequence

from pythonosc.udp_client import SimpleUDPClient

log = logging.getLogger(__name__)

# MadMapper's OSC input port is set in Preferences > OSC. Sources disagree on
# the default (8010 vs 8000), so check yours and pass --port if it differs.
DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 8010

OscArg = float | int | str | bool


class OscSender:
    def __init__(
        self,
        host: str = DEFAULT_HOST,
        port: int = DEFAULT_PORT,
        dry_run: bool = False,
        sleep: Callable[[float], None] = time.sleep,
    ):
        self.host = host
        self.port = port
        self.dry_run = dry_run
        self.sleep = sleep
        self.sent: list[tuple[str, list[OscArg]]] = []
        self._client = None if dry_run else SimpleUDPClient(host, port)

    def send(self, address: str, args: OscArg | Sequence[OscArg] = ()) -> None:
        if not address.startswith("/"):
            raise ValueError(f"OSC address must start with '/': {address!r}")
        values = list(args) if isinstance(args, (list, tuple)) else [args]
        self.sent.append((address, values))
        if self.dry_run:
            log.info("[dry-run] %s %s", address, values)
            return
        log.debug("-> %s:%s %s %s", self.host, self.port, address, values)
        self._client.send_message(address, values)

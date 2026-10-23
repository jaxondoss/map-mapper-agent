"""Claude tool-use loop: plain-English instructions in, OSC messages out."""

from __future__ import annotations

import json
import logging
from typing import Any, Callable

import anthropic

from . import actions
from .addresses import AddressBook, AddressError
from .osc import OscSender

log = logging.getLogger(__name__)

DEFAULT_MODEL = "claude-opus-5-5"
MAX_TOOL_ROUNDS = 20

TOOLS: list[dict[str, Any]] = [
    {
        "name": "set_opacity",
        "description": (
            "Immediately set a MadMapper surface's opacity. 0 is fully hidden, "
            "1 is fully visible. Use for instant changes ('show', 'hide', 'set to half')."
        ),
        "input_schema": {
            "type": "object",
            "properties": {
                "surface": {"type": "string", "description": "Friendly surface name from the configured list."},
                "value": {"type": "number", "minimum": 0, "maximum": 1},
            },
            "required": ["surface", "value"],
            "additionalProperties": False,
        },
    },
    {
        "name": "fade",
        "description": (
            "Fade a surface's opacity linearly over a number of seconds. Blocks until the "
            "fade finishes. MadMapper can't report the current opacity, so pass from_value "
            "when you know where the surface starts; otherwise it starts from the opposite end."
        ),
        "input_schema": {
            "type": "object",
            "properties": {
                "surface": {"type": "string"},
                "to_value": {"type": "number", "minimum": 0, "maximum": 1},
                "seconds": {"type": "number", "minimum": 0, "maximum": actions.MAX_FADE_SECONDS},
                "from_value": {"type": "number", "minimum": 0, "maximum": 1},
            },
            "required": ["surface", "to_value", "seconds"],
            "additionalProperties": False,
        },
    },
    {
        "name": "trigger",
        "description": "Press a configured button, cue or scene control by its friendly name.",
        "input_schema": {
            "type": "object",
            "properties": {
                "name": {"type": "string", "description": "Friendly trigger name from the configured list."},
            },
            "required": ["name"],
            "additionalProperties": False,
        },
    },
    {
        "name": "send_raw_osc",
        "description": (
            "Send an arbitrary OSC message. Only use this when the user gives an explicit "
            "OSC address; prefer the other tools for named surfaces and triggers."
        ),
        "input_schema": {
            "type": "object",
            "properties": {
                "address": {"type": "string", "description": "OSC address starting with '/'."},
                "args": {
                    "type": "array",
                    "items": {"anyOf": [{"type": "number"}, {"type": "string"}, {"type": "boolean"}]},
                },
            },
            "required": ["address"],
            "additionalProperties": False,
        },
    },
]

SYSTEM_PROMPT = """You operate a MadMapper projection-mapping show by sending OSC messages through the tools provided.

Turn the operator's instruction into tool calls. Use the friendly names configured below. If an instruction names a surface or trigger that isn't listed, say so instead of guessing, unless the operator gives an explicit OSC address. Run steps that must happen in order one at a time; independent instant changes can go in one turn.

When you're done, reply with one or two short sentences saying what you sent.

Configured names:
{targets}"""


class ToolInputError(ValueError):
    pass


def _require(inp: dict, key: str, kind: type | tuple[type, ...]) -> Any:
    if key not in inp:
        raise ToolInputError(f"missing required field '{key}'")
    value = inp[key]
    if not isinstance(value, kind) or isinstance(value, bool) and kind is not bool:
        raise ToolInputError(f"field '{key}' has the wrong type")
    return value


def execute_tool(osc: OscSender, book: AddressBook, name: str, inp: dict) -> str:
    """Run one tool call. Raises on bad input; the loop turns that into is_error."""
    number = (int, float)
    if name == "set_opacity":
        return actions.set_opacity(osc, book, _require(inp, "surface", str), _require(inp, "value", number))
    if name == "fade":
        from_value = inp.get("from_value")
        if from_value is not None and not isinstance(from_value, number):
            raise ToolInputError("field 'from_value' has the wrong type")
        return actions.fade(
            osc, book,
            _require(inp, "surface", str),
            _require(inp, "to_value", number),
            _require(inp, "seconds", number),
            from_value,
        )
    if name == "trigger":
        return actions.trigger(osc, book, _require(inp, "name", str))
    if name == "send_raw_osc":
        args = inp.get("args") or []
        if not isinstance(args, list):
            raise ToolInputError("field 'args' must be a list")
        return actions.send_raw(osc, _require(inp, "address", str), args)
    raise ToolInputError(f"unknown tool '{name}'")


class MadMapperAgent:
    """Keeps one conversation so follow-ups ("now fade it back out") have context."""

    def __init__(
        self,
        osc: OscSender,
        book: AddressBook,
        client: anthropic.Anthropic | None = None,
        model: str = DEFAULT_MODEL,
        on_tool: Callable[[str, dict, str, bool], None] | None = None,
    ):
        self.osc = osc
        self.book = book
        self.client = client or anthropic.Anthropic()
        self.model = model
        self.on_tool = on_tool
        self.system = SYSTEM_PROMPT.format(targets=book.describe())
        self.messages: list[dict[str, Any]] = []

    def _create(self):
        return self.client.beta.messages.create(
            model=self.model,
            max_tokens=16000,
            system=self.system,
            tools=TOOLS,
            messages=self.messages,
            thinking={"type": "adaptive"},
            output_config={"effort": "medium"},
            # If a safety classifier declines, the API re-runs the request on a
            # fallback model inside the same call.
            betas=["server-side-fallback-2026-07-01"],
            fallbacks="default",
        )

    def run(self, instruction: str) -> str:
        self.messages.append({"role": "user", "content": instruction})

        for _ in range(MAX_TOOL_ROUNDS):
            response = self._create()
            # Append the full content (thinking blocks included), never just text.
            self.messages.append({"role": "assistant", "content": response.content})

            if response.stop_reason == "refusal":
                return "Claude declined this instruction; nothing further was sent."
            if response.stop_reason == "max_tokens":
                return "Response was cut off before finishing; nothing further was sent."

            tool_uses = [b for b in response.content if b.type == "tool_use"]
            if response.stop_reason != "tool_use" or not tool_uses:
                return "".join(b.text for b in response.content if b.type == "text").strip()

            results = []
            for block in tool_uses:
                try:
                    output = execute_tool(self.osc, self.book, block.name, block.input)
                    is_error = False
                except (ToolInputError, AddressError, ValueError, OSError) as exc:
                    output = f"Error: {exc}"
                    is_error = True
                log.info("%s %s -> %s", block.name, json.dumps(block.input), output)
                if self.on_tool:
                    self.on_tool(block.name, block.input, output, is_error)
                results.append({
                    "type": "tool_result",
                    "tool_use_id": block.id,
                    "content": output,
                    "is_error": is_error,
                })
            # All results go back in one user message.
            self.messages.append({"role": "user", "content": results})

        return f"Stopped after {MAX_TOOL_ROUNDS} tool rounds without finishing."

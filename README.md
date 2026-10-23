# madmapper-agent

Control [MadMapper](https://madmapper.com) with plain-English instructions. Claude turns an instruction like *"fade the front wall in over 4 seconds"* into OSC messages and sends them to MadMapper over UDP.

```
$ madmapper-agent "fade the front wall in over 4 seconds, then start the intro scene"
  → Faded /surfaces/FrontWall/opacity from 0.00 to 1.00 over 4s
  → Triggered /scenes/Intro/select (1.0)
Faded the front wall up over 4 seconds and triggered the intro scene.
```

## What it can do

| Tool | What it sends |
|---|---|
| `set_opacity` | One value (0–1) to a surface's opacity address |
| `fade` | A linear ramp at 30 steps per second. It blocks until the fade finishes. |
| `trigger` | `1.0` to a button, cue or scene address set in `addresses.json` |
| `apply_preset` | Every parameter of a named visualizer look (see below) |
| `set_parameter` | One visualizer shader parameter |
| `send_raw_osc` | Any address and arguments you give it explicitly |

MadMapper doesn't send values back over this one-way link, so a fade starts from the opposite end (0 when fading up, 1 when fading down) unless you say where it starts, e.g. *"fade the floor from 0.5 down to 0 over 3 seconds"*.

## Setup

1. **Install** (Python 3.10+):
   ```
   pip install -e ".[dev]"
   ```
2. **API key:**
   ```
   export ANTHROPIC_API_KEY=sk-ant-...
   ```
3. **Turn on OSC in MadMapper.** Open Preferences → OSC, enable OSC input, and note the port. Sources disagree on the default (8010 or 8000). This tool uses 8010, so pass `--port` if yours differs.
4. **Fill in `addresses.json`.** MadMapper only responds to addresses that have a matching OSC control. Right-click a parameter in MadMapper, choose **Copy OSC Address**, and paste the address in.
   - `surfaces`: friendly name → surface name (becomes `/surfaces/<Name>/opacity`), or a full opacity address that starts with `/`.
   - `triggers`: friendly name → full OSC address of a button, cue or scene control.
   - Keys that start with `_` are comments and are ignored.
5. **Check your setup:**
   ```
   python scripts/check_setup.py
   ```
   Failures mean the tool won't run. Warnings point at things to fix before a show, such as a missing API key or addresses still at their example values.

## Usage

```
madmapper-agent "hide the left wall"            # one instruction
madmapper-agent                                 # interactive prompt; follow-ups keep context
madmapper-agent --dry-run "blackout"            # log OSC instead of sending
madmapper-agent --host 192.168.1.20 --port 8000 # MadMapper on another machine
```

`--dry-run` still calls Claude (so it needs the API key). It only skips the UDP send.

Options: `--addresses PATH`, `--presets PATH`, `--model` (default `claude-opus-5-5`), `-v` to log every OSC message.

Requests to Claude turn on server-side refusal fallback: if a safety classifier declines a request, the API runs it again on a fallback model within the same call. To turn this off, remove `betas` and `fallbacks` from `MadMapperAgent._create` in `madmapper_agent/agent.py` and switch the call to `client.messages.create`.

## Music visualizer: hand pulling a string

`materials/HandStringVisualizer.fs` is a shader (ISF format, which MadMapper uses for generated visuals). It shows a colorful 3D fist gripping a glowing string:

- **The string is the visualizer.** Each harmonic of the string is driven by one band of the music's spectrum (bass makes long, slow bends; treble makes tight ripples). The raw waveform also ripples along it, and its color brightens where its frequency band is loud.
- **The hand pulls.** It does a slow pulling motion and jerks upward on bass hits.
- **Background.** A spectrum "aurora" rises from the bottom, and rings ripple out from the hand on the beat.

![The six presets](preview/presets.png)

### Load it in MadMapper
1. Drag `materials/HandStringVisualizer.fs` into MadMapper's media list and put it on a surface.
2. Choose your audio input in MadMapper's audio settings. The shader's `waveform` and `spectrum` inputs read from it.
3. Its sliders (Sensitivity, Palette, Glow and so on) appear in MadMapper. To control them with the agent, right-click each slider, choose **Copy OSC Address**, and paste the address into the `parameters` section of `addresses.json`. The addresses there now are guesses.

### Presets
`presets.json` holds six named looks:

| Preset | Look |
|---|---|
| `chill` | Warm sunset colors, a lazy pull and soft glow |
| `rave` | Fast rainbow cycling, hard bass kicks, a thick bright string |
| `neon` | Magenta and cyan club neon with strong glow |
| `ice` | Cool blues and whites, gentle motion, a thin crisp string |
| `heartbeat` | Barely moves until a bass hit yanks the string |
| `minimal` | Dark background, a single pale string |

Ask for one in plain English (*"go rave"*, *"something calmer"*, *"neon but more glow"*). The agent uses the `apply_preset` and `set_parameter` tools. To add your own preset, copy a block in `presets.json` and change its values. `python scripts/check_setup.py` flags any value outside the shader's range.

Values are sent in the shader's own range (for example, sensitivity 0–4). If a MadMapper slider jumps straight to its maximum, set `"parameter_scale": "normalized"` in `addresses.json` to send 0–1 instead.

### Preview it in a browser
```
python3 -m http.server 8000
```
Then open <http://localhost:8000/preview/>. You can play a demo beat, use your microphone, or play an audio file, and switch presets or move sliders live. Add `?preset=rave` to the address to open on a preset.

## Development

```
pytest
```

The tests cover address lookup, every action, the presets (each value checked against the shader's ranges), a real UDP round trip to a local OSC server, and the agent loop with a scripted fake client. They need no network access or API key.

## Layout

```
madmapper_agent/
  addresses.py   friendly-name lookup (addresses.json)
  osc.py         UDP sender with dry-run mode
  actions.py     set_opacity, fade, trigger, send_raw, presets
  presets.py     presets.json and shader input ranges
  agent.py       tool definitions and the Claude tool-use loop
  cli.py         command-line entry point
materials/HandStringVisualizer.fs   the visualizer shader
preview/         browser preview for the shader
scripts/check_setup.py
tests/
addresses.json   example mappings, replace with your own
presets.json     named looks for the visualizer
```

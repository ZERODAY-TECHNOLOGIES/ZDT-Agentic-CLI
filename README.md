# zdtllmcli

> **CLI LLM Agent, backed by LiteLLM.** — a [zer0day.ro](https://zer0day.ro) project.

```
                  __ __
   ____  ____ _  / / / /___ ___  _____   ___  ___  _________   ____/ /___ ___  __
  /_  / / __ `/ / / / / __ `__ \/ ___/  /_  )/ -_)/ ___/ __ \-/ __  // _ `/ // /
 / /_/ / /_/ / / / / / / / / / / /__   / __// -_)/ /   / /_/ // /_/ // /_/ \   /
/____/ \__, /_/ /_/_/ /_/ /_/_/\___/  /____/\__/_/    \____/ \__,_/  \___/_/_/
        /_/
```

`zdtllmcli` is an open-source agentic CLI for engineering work, written in C# / .NET 9.
It talks **directly** to a LiteLLM endpoint (OpenAI-compatible `/v1/chat/completions`)
over plain `HttpClient` — no Anthropic SDK, no OpenAI SDK, no LangChain.NET. Bring
your own LiteLLM proxy and your own models.

The binary is `zdt`.

## Install

Self-contained binaries — no .NET runtime required.

**Linux / macOS:**

```bash
curl -fsSL https://raw.githubusercontent.com/ZERODAY-TECHNOLOGIES/ZDT-Agentic-CLI/main/install.sh | bash
```

**Windows (PowerShell):**

```powershell
irm https://raw.githubusercontent.com/ZERODAY-TECHNOLOGIES/ZDT-Agentic-CLI/main/install.ps1 | iex
```

The installer downloads the binary for your OS + arch, verifies its SHA256, drops
it into `~/.zdtllm/bin` (Linux/macOS) or `%LOCALAPPDATA%\zdtllm\bin` (Windows), and
adds the directory to your shell PATH. After install:

- Open a new terminal *or* re-source your shell rc (`source ~/.zshrc`, `source ~/.bashrc`,
  or on PowerShell `$env:Path = [Environment]::GetEnvironmentVariable('Path','User')`).
- Run `zdt` for the first-run setup wizard, or `zdt --help` for all flags.

Pin a specific version: append `-s -- --version v0.1.0` (bash) or `-Version v0.1.0`
(PowerShell). Uninstall: `--uninstall` / `-Uninstall`.

Supported targets: `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64`,
`win-arm64`.

### Antivirus / SmartScreen note (Windows)

The release binaries are **self-contained and unsigned**, so Windows SmartScreen or some
antivirus engines may show an "unknown publisher" warning or a heuristic false positive on
first run. This is not a detection of actual malicious behaviour — zdt is open source, builds
reproducibly from this repo via the tagged GitHub Actions release workflow, and the binaries
are published uncompressed with full publisher metadata to minimise these false positives. If
your AV quarantines it, verify the SHA256 against `checksums.txt` on the release, allow-list it,
and (optionally) report the false positive to your AV vendor. Building from source
(`dotnet publish`) avoids the download-reputation heuristic entirely.

## Configure

Settings cascade across three scopes (low → high precedence):

- `~/.zdtllm/settings.json` — user-global
- `.zdtllm/settings.json` — project, committed
- `.zdtllm/settings.local.json` — project, gitignored

Arrays merge (concat + dedup), scalars override. `${VAR}` tokens expand from
the environment at load time.

```json
{
  "model": "heavy",
  "permissions": {
    "allow": ["Read", "Bash(git status *)", "Bash(git diff *)"],
    "deny":  ["Read(./.env)", "Read(./.env.*)", "Read(./secrets/**)"]
  },
  "litellm": {
    "baseUrl": "http://localhost:4000",
    "apiKey":  "${ZDTLLM_API_KEY}",
    "models": {
      "light":  "qwen3-coder-flash",
      "medium": "qwen3-coder",
      "heavy":  "qwen3-max"
    },
    "subagentModels": {
      "code-reviewer": "light",
      "explore":       "light"
    }
  }
}
```

`subagentModels` is optional — it routes a given `subagent_type` (the one passed to the
Agent tool) to a different model than the parent. The defaults are `code-reviewer` →
`light` and `explore` → `light` (read-only profiles run on the cheap tier); other types,
including `general-purpose`, inherit the parent's current model. Override with an alias
from `models` or with a literal model id.

### Env vars

Six env vars override settings.json at runtime — same roles as claude-cli's
`ANTHROPIC_BASE_URL` / `ANTHROPIC_AUTH_TOKEN` / `ANTHROPIC_*_MODEL` knobs, but the var
names use zdt's canonical vocabulary (`light`/`medium`/`heavy`) so there's one naming
convention across settings, CLI flags, and env:

| env var                    | effect                                                                         |
| -------------------------- | ------------------------------------------------------------------------------ |
| `ZDT_BASE_URL`             | overrides `litellm.baseUrl` (the LiteLLM proxy URL)                             |
| `ZDT_API_KEY`              | overrides `litellm.apiKey` (the LiteLLM proxy bearer token)                     |
| `ZDT_DEFAULT_HEAVY_MODEL`  | overrides `litellm.models.heavy`                                                |
| `ZDT_DEFAULT_MEDIUM_MODEL` | overrides `litellm.models.medium`                                               |
| `ZDT_DEFAULT_LIGHT_MODEL`  | overrides `litellm.models.light`                                                |
| `ZDT_SMALL_FAST_MODEL`     | default model for read-only subagents (`code-reviewer`, `explore`)              |

The env layer wins over committed settings.json so a runtime `export` always pins the
value. With `ZDT_BASE_URL` + `ZDT_API_KEY` set you can run zdt without a settings file
at all — the first-run wizard is skipped because `baseUrl` is already populated.
`ZDT_SMALL_FAST_MODEL` only applies when `subagentModels` hasn't already pinned the
relevant subagent type explicitly.

### Tool-calling mode

zdt supports two transports for tool calls:

- `native` — OpenAI-shaped `tool_calls` array on the chat completion. Best for proprietary
  models (GPT-4, Claude, Gemini) and any model whose chat template renders `tools` natively.
- `xml` — embeds calls as `<function_calls><invoke name="...">...</invoke></function_calls>`
  inside the assistant text. Required for most open-weights chat templates (Qwen3, GLM,
  DeepSeek-V3, Hermes, Kimi, Yi, Mistral-Nemo) — they don't reliably wire up native
  tool-calling on LiteLLM.

When neither `--tool-calling` nor `litellm.toolCallingMode` is set, zdt auto-selects `xml`
for any model whose name matches a known XML-only family and prints a one-line stderr note;
otherwise it defaults to `native`. Override anytime with `--tool-calling native` or by
setting `toolCallingMode` in settings.json — the explicit choice always wins.

When XML mode is active and the upstream proxy / chat template corrupts the open tag
(close tag without matching open, stray `<invoke>` markers), zdt's parser runs a recovery
pass to extract calls anyway, prints a stderr warning, and emits a structured signal in
`stream-json`:

- a `{"type":"warning","subtype":"format_breakdown","details":"..."}` event the moment it's
  detected;
- a `format_breakdown: true` flag on the terminal `result` event.

That lets downstream consumers detect the case without pattern-matching on `result.text`.

## Run

After install:

```bash
zdt                                                  # interactive REPL (first run launches the setup wizard)
zdt -p "Read README.md and tell me what it contains" # one-shot, non-interactive
zdt --help                                           # full flag list
```

From source (`dotnet` SDK 9.0+ required):

```bash
dotnet build
dotnet run --project src/Zdtllm.Cli -- -p "Read README.md and tell me what it contains"
```

### Interactive features

These three claude-cli behaviours work with **any** LiteLLM-served model — nothing here is
Anthropic-specific, and they degrade gracefully to a no-op when there's no real terminal
(print mode, piped stdin, subagents).

**Resume a past conversation with a picker.** `-r` / `--resume` now takes an *optional*
session id:

```bash
zdt -r                # arrow-key list of this project's recent conversations, newest first
zdt -r <uuid>         # resume a specific session directly (unchanged)
```

The picker shows each session's first message, how long ago it was touched, its turn count,
and its model. Sessions live under `.zdtllm/sessions/`. When stdin is redirected it falls
back to the most-recent session instead of prompting.

**Queue messages while the model works.** You no longer have to wait for a turn to finish
before typing the next thing. Keep typing while the model streams / calls tools; each line
you enter is queued and folded into the *same* task at the next tool-round boundary (or run
as a follow-up turn if the model already finished). zdt prints `↳ picked up your queued
message` when it consumes one.

**Let the model offer you choices.** The model can call the `AskUserQuestion` tool to present
a short list of options; you pick with ↑/↓ and Enter (Space toggles for multi-select). It's
registered only in interactive mode, so in `-p` / subagent runs the model is told to decide
for itself instead of blocking on input that will never arrive.

**Plan mode.** Start with `--plan`, or toggle it any time with `/plan`. In plan mode the agent
is read-only — `Write`, `Edit`, `NotebookEdit` and `Bash` are blocked — so it investigates and
drafts a step-by-step plan, then calls `ExitPlanMode` to show it to you. Approve it (arrow keys)
and edits unlock on the next turn; decline and it keeps refining. Great for "look before you leap"
on unfamiliar code.

**Multi-line paste & drag-and-drop.** The interactive prompt is a real line editor: pasting a
multi-line block no longer submits at the first newline — the whole thing lands as one message
(large pastes collapse to a compact `[pasted N lines]` chip). Dragging a file onto the terminal
drops its path into the prompt, cleaned up (quotes / `file://` stripped) so the agent can read it.
Editing keys — cursor moves, Home/End, Backspace/Delete, Ctrl+A/E/U/K, Ctrl+D to exit on an empty
line — all work. Set `ZDT_BASIC_INPUT=1` to fall back to plain line input on a terminal where the
editor misbehaves.

**Graceful exit.** On `/exit`, EOF, or Ctrl+C, zdt tells you which session just closed and how to
resume it (`zdt -r <id>`). During a turn, one Ctrl+C interrupts just that turn; at the prompt, one
Ctrl+C shows "press again to exit" and a second one exits — exactly like claude-cli.

## License

MIT — see [LICENSE](LICENSE).

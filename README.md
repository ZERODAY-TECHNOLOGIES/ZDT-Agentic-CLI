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

Four env vars override settings.json at runtime — same role as claude-cli's
`ANTHROPIC_*_MODEL` knobs, but the var names use zdt's canonical tier vocabulary
(`light`/`medium`/`heavy`) so there's one naming convention across settings, CLI flags,
and env:

| env var                    | effect                                                                         |
| -------------------------- | ------------------------------------------------------------------------------ |
| `ZDT_DEFAULT_HEAVY_MODEL`  | overrides `litellm.models.heavy`                                                |
| `ZDT_DEFAULT_MEDIUM_MODEL` | overrides `litellm.models.medium`                                               |
| `ZDT_DEFAULT_LIGHT_MODEL`  | overrides `litellm.models.light`                                                |
| `ZDT_SMALL_FAST_MODEL`     | default model for read-only subagents (`code-reviewer`, `explore`)              |

The env layer wins over committed settings.json so a runtime `export` always pins the
model. `ZDT_SMALL_FAST_MODEL` only applies when `subagentModels` hasn't already pinned
the relevant subagent type explicitly.

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

## License

MIT — see [LICENSE](LICENSE).

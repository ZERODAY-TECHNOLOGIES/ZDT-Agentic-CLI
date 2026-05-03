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

## Status

This repository is in **Phase 1**: scaffold and end-to-end agent loop. Today the
CLI exposes a single mode — `zdt -p "<query>"` — wired to `Read` and `Bash` tools.
Sessions, the interactive REPL, slash commands, skills, the rest of the tool
catalog, and context compaction all land in subsequent phases.

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
    }
  }
}
```

## Run

```bash
dotnet build
dotnet run --project src/Zdtllm.Cli -- -p "Read README.md and tell me what it contains"
```

Or after `dotnet publish`:

```bash
zdt -p "Read README.md and tell me what it contains"
```

## License

MIT — see [LICENSE](LICENSE).

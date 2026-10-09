# AGENTS.md

Operating manual for AI agents working in this repo. Technical details (architecture, project layout, driver choice, API reference) live in `.github/copilot-instructions.md`. Read that before touching code. Task skills: `skills/build-install/SKILL.md` for building and installing, `skills/use-relay/SKILL.md` for driving the relay API.

## What this is

A .NET 11 service that exposes a key-authenticated HTTP API to drive a real browser through Playwright for .NET. Self-contained publish, no .NET runtime needed on the box. It exists so automated browsing egresses from a residential IP instead of a datacenter range.

## Build

```
dotnet publish -c Release -o publish
```

That is the only supported build. `dotnet build` is fine for a quick compile check, but the shipped artifact is always the publish: `publish/agent-edge-bridge` (apphost) plus its runtime files, linux-arm64, self-contained.

Requirements: .NET 11 SDK (11.0.100-rc.1 or later). The `net11.0` target is deliberate.

Playwright needs its browser: run the `install` command from a scratch console project referencing `Microsoft.Playwright` (see README). Browsers land in `~/.cache/ms-playwright`. Without it, session creation fails at launch.

## Test

The service needs a browser and an API key. Generate one if `.api-key` does not exist:

```
head -c 32 /dev/urandom | base64 | tr -d '\n+/=' | head -c 48 > .api-key
chmod 600 .api-key
```

Never commit `.api-key`. It is in `.gitignore`. If you see it staged, stop and unstage it.

Smoke test against a local run:

```
export AGENT_EDGE_BRIDGE_API_KEY=$(cat .api-key)
./publish/agent-edge-bridge &
curl -s localhost:8899/health
# create session, goto https://example.com, check title, delete session
```

The full sequence is in the README. Run it after any change to `Playwright/` or `Program.cs`.

## Conventions

- Minimal API via `WebApplication.CreateBuilder`. No controllers, no MVC.
- JSON is camelCase, configured once in `Program.cs` via `ConfigureHttpJsonOptions`. Do not hand-format JSON responses.
- Code comments are terse and explain why, not what. The code already says what.
- The service binds `127.0.0.1:8899` only. Do not change the bind address in code. Exposure is done at the tunnel layer.
- One active session max. This is intentional (memory, bot behavior), not a limitation to "fix."
- Commit early and often. This box runs several things at once and has crashed mid-work before. Push to GitHub periodically.
- Never add co-author trailers to commits. Ever.

## What not to do

- Do not reintroduce Native AOT. Playwright's driver model needs runtime reflection; the trim story was verified broken (IL2104/IL3053/IL3000). See copilot instructions.
- Do not put real hostnames, IPs, API keys, or personal infrastructure in docs, comments, or the service file. Placeholders only.
- Do not "simplify" the key auth. It is the only thing between the internet and a remote-controlled browser.

## Deploy

`deploy/agent-edge-bridge.service` is the sample systemd unit. It expects the key at `/etc/agent-edge-bridge/api-key` as `AGENT_EDGE_BRIDGE_API_KEY=<key>`, mode 600.

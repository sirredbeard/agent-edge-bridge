# Copilot instructions: agent-edge-bridge

## What it is

A key-authenticated HTTP relay that drives a real Chromium browser through Playwright for .NET. A remote client gets programmatic browsing (navigate, fill, click, read rendered HTML, screenshots, cookies) with full JavaScript, egressing from the machine's local network. Published self-contained for linux-arm64, no .NET runtime needed on the box.

## Repo layout

Repo directory: `/home/arduino/agent-edge-bridge` (also published to GitHub as `sirredbeard/agent-edge-bridge`).

```
AgentEdgeBridge.csproj          net11.0, self-contained, linux-arm64, PlaywrightPlatform=linux-arm64
Program.cs                      minimal API, key auth, session endpoints
Playwright/PlaywrightSession.cs browser lifecycle + page automation via Playwright
Api/Contracts.cs                request/response DTOs (camelCase JSON configured in Program.cs)
deploy/agent-edge-bridge.service  sample systemd unit (127.0.0.1:8899)
.github/copilot-instructions.md this file
AGENTS.md                       agent operating manual (build, test, conventions)
README.md                       user-facing docs
LICENSE                         MIT
```

## Architecture

One process, two layers:

1. **HTTP API** (`Program.cs`): `WebApplication.CreateBuilder`, localhost-only on port 8899. A middleware checks the `X-Api-Key` header on everything except `/health`. Session state is a single guarded slot, 409 when busy. JSON is camelCase via `ConfigureHttpJsonOptions`.
2. **Browser session** (`PlaywrightSession.cs`): Playwright `IBrowserContext` plus one `IPage`. Two launch paths: `LaunchPersistentContextAsync` when the caller passes `userDataDir` (the directory itself is the persistent profile), otherwise `LaunchAsync` + `NewContextAsync` with an optional storage-state file loaded from `profiles/<name>.json`.

Fill uses Playwright's `FillAsync` (auto-waits, fires the input events React needs). Click is `ClickAsync`. Press is `PressAsync`. Wait is `WaitForSelectorAsync` (throws Playwright's `TimeoutException`, mapped to 504) or `WaitForTimeoutAsync` for a plain sleep. Screenshot is `Page.ScreenshotAsync` (PNG). Cookies are `Context.CookiesAsync`, wrapped as `{"cookies": [...]}`.

## Browser driver decision

The driver is Playwright for .NET 1.63.0. The first build used a hand-rolled CDP client to chase a Native AOT single binary. Playwright is not AOT-safe (IL2104/IL3053, and its driver lookup breaks in single-file apps), and AOT turned out to be a nice-to-have, so the requirement was dropped in favor of the better tool. Do not reintroduce AOT without re-verifying the Playwright trim story.

Playwright's native ARM64 Chromium: Chrome for Testing 153.0.8010.12, linux-arm64, from `~/.cache/ms-playwright`. The NuGet footprint is trimmed to one platform via `<PlaywrightPlatform>linux-arm64</PlaywrightPlatform>`. A `browserPath` session parameter is passed through to Playwright's `ExecutablePath` for other Chromium-based binaries, but the bundled build is the tested path.

Do not try to drive Edge under FEX-Emu from this service. That was explored and dropped: even with the `--browser-subprocess-path` child-process workaround and a D-Bus session, Playwright's launch handshake times out under emulation. The working approach for authenticated sessions is a copied Edge profile via `userDataDir` (see below).

**Real browser profiles via `userDataDir`.** Copy `~/.config/microsoft-edge` (whole directory, including `Local State`) and pass the copy as `userDataDir`. On this path the launch ignores Playwright's `--password-store=basic` and `--use-mock-keychain` defaults so Chromium uses the login keyring; without that, the profile's cookies are wiped on startup (decrypt failure). Requires `DBUS_SESSION_BUS_ADDRESS` in the service environment pointed at an unlocked login session. Verified: Edge 155 profile cookies (Amazon `at-main`, Target tokens) decrypt and both sites render logged in. Never point at the live profile; copy it.

Bot hardening: `--disable-blink-features=AutomationControlled` on the command line, plus a current Chrome user-agent string on the context. Profiles are the real defense: `userDataDir` persists the whole profile, and `/storage/save` snapshots storage state to `profiles/<name>.json` for later `{"profile": name}` loads. Warm session cookies beat every header trick.

## API reference

Auth: `X-Api-Key` header on all endpoints except `GET /health`. Wrong or missing key is 401.

```
GET  /health
  -> 200 {"ok":true}

POST /sessions
  body: {"browserPath": "...", "userDataDir": "...", "headless": true, "profile": "amazon"}
  All fields optional. Defaults: Playwright's Chromium, throwaway temp profile, headless.
  -> 200 {"sessionId": "..."} | 409 when one is already active

POST /sessions/{id}/goto
  body: {"url": "..."}
  -> 200 {"ok": true, "title": "...", "url": "..."}

POST /sessions/{id}/fill
  body: {"selector": "<css>", "text": "..."}
  -> 200 {"ok": true} | 502 on no-match / Playwright error

POST /sessions/{id}/click
  body: {"selector": "<css>"}
  -> 200 {"ok": true} | 502

POST /sessions/{id}/press
  body: {"selector": "<css>", "key": "Enter"}
  -> 200 {"ok": true} | 502

POST /sessions/{id}/wait
  body: {"selector": "<css>", "timeoutMs": 10000}  (selector optional: plain sleep)
  -> 200 {"ok": true} | 504 on timeout

GET  /sessions/{id}/html
  -> 200 text/html, rendered page content

GET  /sessions/{id}/screenshot
  -> 200 image/png

GET  /sessions/{id}/cookies
  -> 200 application/json, {"cookies": [...]}

POST /sessions/{id}/storage/save
  body: {"name": "amazon"}
  -> 200 {"ok": true}, writes profiles/<name>.json

DELETE /sessions/{id}
  -> closes the browser context, 200 {"ok": true} | 404
```

Error shape on failures is the ASP.NET Core problem response (`Results.Problem`), status 502 for browser errors, 504 for wait timeouts.

## Conventions for changes

- Keep the API contract stable. The relay has a remote client; endpoint shapes are the contract.
- Timeouts: Playwright's defaults (30s) plus explicit `timeoutMs` on wait. One session max, serialize at the API layer.
- Temp profiles live under the OS temp dir and are deleted on session close. Named profiles in `profiles/` are never auto-deleted.

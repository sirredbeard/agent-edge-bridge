# agent-edge-bridge

A key-authenticated HTTP relay that drives a real browser on a small ARM64 box. The browser egresses via the local network, so a remote client gets programmatic browsing with full JavaScript from a residential IP.

## Why

Datacenter IP ranges get flagged by bot defenses on retail sites. This box sits on a home connection. Run the relay on it and automated browsing comes from a residential IP, in a real browser, with the JavaScript actually running. That combination clears challenges that headless datacenter scraping does not.

## How it works

A .NET 11 minimal API, published self-contained for linux-arm64. It drives the browser through Playwright for .NET (1.63.0). One session at a time, browser torn down on delete.

Playwright was picked over a hand-rolled CDP client for three reasons: its selectors auto-wait (no polling loops), its API covers the whole relay surface without protocol plumbing, and Microsoft ships it monthly. The first build used raw CDP to chase a Native AOT single binary. AOT turned out to be a nice-to-have, and Playwright's driver model needs runtime reflection, so the AOT requirement was dropped.

## Browser

Playwright's native ARM64 Chromium (Chrome for Testing 153.0.8010.12, in `~/.cache/ms-playwright`). No system browser needed. Headless unless you ask otherwise. `browserPath` can point at another Chromium-based binary, but the bundled build is the tested path.

For authenticated sessions, pass `userDataDir` pointing at a real browser profile. Edge profiles work directly: copy `~/.config/microsoft-edge` (the whole directory, not just `Default`) somewhere and hand the copy to the relay. The bridge uses the login keyring instead of Playwright's mock keychain on this path, so the profile's cookies decrypt. The service needs `DBUS_SESSION_BUS_ADDRESS` pointed at an unlocked login session for that (set in the systemd unit). Copy, don't use the live profile: two writers will corrupt it.

## Requirements

- .NET 11 SDK (11.0.100-rc.1 or later)
- linux-arm64
- A browser: Microsoft Edge installed (preferred), or Playwright's Chromium via the one-time fetch below

## Build

```
dotnet publish -c Release -o publish
```

Output is `publish/agent-edge-bridge` plus its runtime files. Self-contained, no .NET runtime needed on the box.

Only needed for the fallback Chromium. The .NET Playwright package has no standalone CLI on Linux, so fetch the browser with a throwaway console project:

```
mkdir /tmp/pwinstall && cd /tmp/pwinstall
# csproj referencing Microsoft.Playwright, Program.cs with:
# return Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
dotnet run
```

## Run

Generate a key once and stash it where the service can read it:

```
head -c 32 /dev/urandom | base64 | tr -d '\n+/=' | head -c 48 > .api-key
chmod 600 .api-key
```

Then:

```
export AGENT_EDGE_BRIDGE_API_KEY=$(cat .api-key)
./publish/agent-edge-bridge
```

Listens on `http://127.0.0.1:8899`. Bind is localhost only by design, expose it via a tunnel, not by listening on `0.0.0.0`. Every endpoint except `/health` requires the `X-Api-Key` header, 401 otherwise.

## API

```
GET  /health                      no auth, {"ok":true}
POST /sessions                    {browserPath?, userDataDir?, headless?, profile?} -> {sessionId}, 409 if one is already active
POST /sessions/{id}/goto          {url} -> {ok, title, url}
POST /sessions/{id}/fill          {selector, text}
POST /sessions/{id}/click         {selector}
POST /sessions/{id}/press         {selector, key}       e.g. "Enter"
POST /sessions/{id}/wait          {selector?, timeoutMs?}  504 on timeout
GET  /sessions/{id}/html          rendered HTML of the current page
GET  /sessions/{id}/screenshot    PNG bytes
GET  /sessions/{id}/cookies       {"cookies": [...]}
POST /sessions/{id}/storage/save  {name}                persist cookies/storage under a profile name
DELETE /sessions/{id}             kills the browser, frees the slot
```

Two ways to keep a login. Pass `userDataDir` and the browser profile itself persists on disk. Or start with `profile: "amazon"` to load a saved storage state, and hit `/storage/save` when you want to snapshot the current one. Warm session cookies are the single best anti-bot measure there is.

Quick smoke test:

```
KEY=$(cat .api-key)
SID=$(curl -s -X POST localhost:8899/sessions -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' -d '{}' | python3 -c "import json,sys; print(json.load(sys.stdin)['sessionId'])")
curl -s -X POST localhost:8899/sessions/$SID/goto -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' -d '{"url":"https://example.com"}'
curl -s -X DELETE localhost:8899/sessions/$SID -H "X-Api-Key: $KEY"
```


## Install as a service

```
sudo cp deploy/agent-edge-bridge.service /etc/systemd/system/
sudo mkdir -p /etc/agent-edge-bridge
printf 'AGENT_EDGE_BRIDGE_API_KEY=%s\n' "$(cat .api-key)" | sudo tee /etc/agent-edge-bridge/api-key > /dev/null
sudo chmod 600 /etc/agent-edge-bridge/api-key
sudo systemctl daemon-reload
sudo systemctl enable --now agent-edge-bridge
```

Check it:

```
systemctl status agent-edge-bridge
curl -s localhost:8899/health
```

The unit expects the published binary at `/home/arduino/agent-edge-bridge/publish/agent-edge-bridge` and the key at `/etc/agent-edge-bridge/api-key`. If yours live elsewhere, edit the unit, `daemon-reload`, restart.

## Notes

- The browser launches with `--disable-blink-features=AutomationControlled` and a current Chrome user agent string. It is still automation, just polite automation.
- If you tunnel this, put Cloudflare Access (service token, not the interactive login) in front of it. An open relay is an open proxy.
- `AGENTS.md` is the operating manual for AI agents working in this repo. Technical details live in `.github/copilot-instructions.md`.

## License

[MIT](LICENSE).

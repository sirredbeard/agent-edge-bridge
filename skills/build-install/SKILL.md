---
name: "agent-edge-bridge-build-install"
description: "Build the agent-edge-bridge .NET service from source, fetch its browser, and install it as a systemd service on the ARM64 board. Use when setting up the relay, rebuilding after code changes, or reinstalling the service."
---

# Build and install agent-edge-bridge

## Purpose

Turn the repo at `/home/arduino/agent-edge-bridge` into a running systemd service. Three phases: publish the .NET binary, make sure a browser exists, install the unit.

## Requirements

- .NET 11 SDK (11.0.100-rc.1 or later). The `net11.0` target is deliberate.
- linux-arm64. The publish is self-contained; no runtime needed on the box.

## Workflow

### 1. Publish

```
cd /home/arduino/agent-edge-bridge
dotnet publish -c Release -o publish
```

That is the only supported build. `dotnet build` is fine for a quick compile check, but the shipped artifact is always the publish. Run the smoke test in AGENTS.md after any change to `Playwright/` or `Program.cs`.

### 2. Browser

Preferred: installed Microsoft Edge at `/usr/local/bin/microsoft-edge` (x86_64 under FEX-Emu). Nothing to fetch. If it is not there, say so and stop, do not silently substitute Chromium.

Fallback: Playwright's own ARM64 Chromium. The .NET package has no standalone CLI on Linux, so:

```
mkdir /tmp/pwinstall && cd /tmp/pwinstall
# csproj referencing Microsoft.Playwright, Program.cs with:
# return Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
dotnet run
```

Browsers land in `~/.cache/ms-playwright`. Without a browser, session creation fails at launch.

### 3. Install the service

Generate a key if `.api-key` does not exist in the repo root:

```
head -c 32 /dev/urandom | base64 | tr -d '\n+/=' | head -c 48 > .api-key
chmod 600 .api-key
```

Then install:

```
sudo cp deploy/agent-edge-bridge.service /etc/systemd/system/
sudo mkdir -p /etc/agent-edge-bridge
printf 'AGENT_EDGE_BRIDGE_API_KEY=%s\n' "$(cat .api-key)" | sudo tee /etc/agent-edge-bridge/api-key > /dev/null
sudo chmod 600 /etc/agent-edge-bridge/api-key
sudo systemctl daemon-reload
sudo systemctl enable --now agent-edge-bridge
```

Verify:

```
systemctl status agent-edge-bridge
curl -s localhost:8899/health
```

Expect `{"ok":true}`. The unit binds `127.0.0.1:8899` only. Exposure is done at the tunnel layer, never by changing the bind address.

## Operating rules

- Never commit `.api-key`. It is in `.gitignore`. If you see it staged, stop and unstage it.
- Do not put real hostnames, IPs, API keys, or personal infrastructure in docs, comments, or the service file. Placeholders only.
- Do not reintroduce Native AOT. Playwright's driver model needs runtime reflection; the trim story was verified broken (IL2104/IL3053/IL3000).
- Commit early and often, push to GitHub periodically. Never add co-author trailers.
- Do not "simplify" the key auth. It is the only thing between the internet and a remote-controlled browser.

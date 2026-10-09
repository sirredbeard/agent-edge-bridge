---
name: "agent-edge-bridge-use-relay"
description: "Drive the agent-edge-bridge browser relay over HTTP: open sessions, navigate, fill forms, click, read rendered HTML, take screenshots, manage cookies and saved profiles. Use when automating a real browser from a residential IP."
---

# Use the agent-edge-bridge relay

## Purpose

Programmatic browsing with full JavaScript, egressing from the board's residential IP. One session at a time. Every endpoint except `/health` needs the API key.

## Auth

Two layers when the relay sits behind Cloudflare Access (the normal deployment):

- `CF-Access-Client-Id` and `CF-Access-Client-Secret`: the Access service token.
- `X-Api-Key`: the relay's own key.

Direct on the board (`localhost:8899`), only `X-Api-Key` applies. Wrong or missing key is 401. All request bodies are JSON, camelCase.

## Workflow

### 1. Open a session

```
curl -s -X POST localhost:8899/sessions \
  -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' \
  -d '{}'
# -> {"sessionId": "..."}
```

Body fields, all optional:

- `browserPath`: path to a Chromium-based binary. `"/usr/local/bin/microsoft-edge"` is the preferred browser (installed Edge under FEX). Omit it for the fallback: Playwright's own headless ARM64 Chromium.
- `userDataDir`: persistent profile directory. Cookies and storage survive restarts. To reuse a real browser profile (e.g. Edge's), copy `~/.config/microsoft-edge` (whole directory, including `Local State`) and pass the copy. The relay uses the login keyring on this path so the profile's cookies decrypt; without it they are wiped on startup. Never use the live profile directly.
- `headless`: default true.
- `profile`: name of a saved storage state (`profiles/<name>.json`) to load.

409 means a session is already active. One at a time, by design.

### 2. Drive the page

```
# navigate
curl -s -X POST localhost:8899/sessions/$SID/goto \
  -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' \
  -d '{"url":"https://example.com"}'
# -> {"ok":true,"title":"...","url":"..."}

# fill a field (auto-waits, fires the input events React needs)
curl -s -X POST localhost:8899/sessions/$SID/fill \
  -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' \
  -d '{"selector":"#search","text":"wireless headphones"}'

# click
curl -s -X POST localhost:8899/sessions/$SID/click \
  -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' \
  -d '{"selector":"button[type=submit]"}'

# key press, e.g. Enter
curl -s -X POST localhost:8899/sessions/$SID/press \
  -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' \
  -d '{"selector":"#search","key":"Enter"}'

# wait for a selector (504 on timeout) or a plain sleep
curl -s -X POST localhost:8899/sessions/$SID/wait \
  -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' \
  -d '{"selector":".results","timeoutMs":15000}'
```

### 3. Read state back

```
curl -s localhost:8899/sessions/$SID/html -H "X-Api-Key: $KEY"        # rendered HTML
curl -s localhost:8899/sessions/$SID/screenshot -H "X-Api-Key: $KEY" # PNG bytes
curl -s localhost:8899/sessions/$SID/cookies -H "X-Api-Key: $KEY"    # {"cookies": [...]}
```

### 4. Keep a login

Two mechanisms. `userDataDir` persists the whole profile on disk with no extra work. Or snapshot storage state under a name and reload it later:

```
curl -s -X POST localhost:8899/sessions/$SID/storage/save \
  -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' \
  -d '{"name":"amazon"}'
# later sessions: -d '{"profile":"amazon"}'
```

Warm session cookies are the single best anti-bot measure there is.

### 5. Close the session

```
curl -s -X DELETE localhost:8899/sessions/$SID -H "X-Api-Key: $KEY"
```

This kills the browser and frees the slot. Always close what you open; a wedged session blocks everything until it is deleted.

## Operating rules

- Never script the API key into a repo, a log, or a chat transcript. Read it from the key file at call time.
- If a site serves a bot challenge, prefer a warm profile (`userDataDir` or `profile`) over hammering the challenge. Back off before retrying.
- `GET /health` is the only unauthenticated endpoint. Use it to check the relay is up before opening a session.

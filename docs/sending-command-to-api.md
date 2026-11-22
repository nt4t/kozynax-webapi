# How to Send Commands to the Kozynax Web API

Practical guide for driving the headless Web API (`src/Kozynax.WebApi`). Full endpoint
reference is in [`api.md`](./api.md); this doc focuses on *how to format requests correctly*.

Base URL is `http://localhost:5080` by default (override with env `KOZYNAX_API_URLS`).
All examples use `curl`; the same calls work from any HTTP client.

```bash
BASE=http://localhost:5080
```

## Two request body formats

The API takes exactly two kinds of request bodies. Pick the right one per endpoint — mixing
them up is the most common source of failures.

### 1. JSON (simple values)

Used by `/step`, `/mem/{addr}` (write), `/breakpoints` (add), and snapshot-by-URL.

- Set `Content-Type: application/json`.
- Body is a plain JSON object; no trailing newline required, but curl `-d` tolerates one.

```bash
curl -s -X POST "$BASE/step"  -H 'Content-Type: application/json' -d '{"count":1000}'
curl -s -X POST "$BASE/mem/0x6000" -H 'Content-Type: application/json' -d '{"hex":"A7"}'
curl -s -X POST "$BASE/breakpoints" -H 'Content-Type: application/json' -d '{"address":64000}'
```

### 2. Multipart file upload (files)

Used by `/snapshot` and `/config`. This is where most mistakes happen.

**Correct syntax:** `-F "field=@/path/to/file;type=mimetype"`. The filename part comes from the
file path, and its **extension drives format detection**, so keep it.

```bash
# /snapshot — upload a snapshot file (extension decides the loader)
curl -s -F "file=@tests/Fixtures/Exolon.z80;type=application/octet-stream" "$BASE/snapshot"

# /config — upload a config file
curl -s -F "file=@machines.config;type=text/plain" "$BASE/config"
```

**Common multipart mistakes (avoid these):**

| Wrong | Why it fails |
|-------|--------------|
| `-F "file=@path@application/octet-stream"` | `@path@type` is **not valid curl syntax** → curl exits `26` ("failed to read file") and sends nothing. |
| `-d @path` (data) instead of `-F` (form) | Sends a raw body, not multipart; the server rejects it with `UnsupportedFormat`. |
| Stripping the extension (`file=@/tmp/snap`) | Format detection relies on the extension → load fails silently. |

## Addresses are hex, but write them as you like

- In **URLs and JSON**, addresses may be decimal or `0x`-prefixed hex; both parse.
  `/mem/{addr}` accepts any int that fits in a `ushort` (0–65535).
- Hex strings for memory **values** are uppercase/lowercase-insensitive, no `0x` needed: `"A7"`, `"a7"` both work.

```bash
curl -s "$BASE/mem/0xF800?len=16"      # read 16 bytes at 0xF800 (hex)
curl -s "$BASE/mem/64512?len=16"        # same address, decimal — equivalent
```

## Reading data vs. writing data

- **Read** with `GET` (no body): `/status`, `/cpu`, `/mem/{addr}` (read), `/memory/dump`,
  `/breakpoints`, `/screen`.
- **Write** with `POST`: `/run`, `/stop`, `/reset`, `/step`, `/snapshot`, `/config`,
  POST `/mem/{addr}`, POST/DELETE `/breakpoints/*`.

A frequent mistake is using `GET` for a write action (e.g. `GET /run`) — the server ignores it
and returns nothing useful. Match method to intent.

## Inspecting responses and errors

- Add `-i` or `-w '\nHTTP %{http_code}\n'` to see status codes and bodies explicitly.
- Errors come back as JSON with an enum `code`, e.g.:

  | code | meaning | fix |
  |------|---------|-----|
  | `NoImageLoaded` | ran/stopped/stepped before a snapshot loaded, or load failed | `/snapshot` first |
  | `UnsupportedFormat` | bad multipart body or missing file | use `-F "…;type=…"` with an extension |
  | (400) malformed request | wrong content-type / invalid JSON | set `Content-Type: application/json` for JSON bodies |

```bash
curl -s -w '\nHTTP %{http_code}\n' "$BASE/status"
```

## Worked session (load → run → screenshot → stop)

```bash
BASE=http://localhost:5080

# 1. Load a snapshot (multipart; keep the .z80 extension)
curl -s -F "file=@tests/Fixtures/Exolon.z80;type=application/octet-stream" "$BASE/snapshot"
# -> {"status":"loaded","machine":"Exolon.z80"}

# 2. Start emulation
curl -s -X POST "$BASE/run"
# -> {"status":"running"}

# 3. Wait a moment for frames, then capture the screen (PPM P6, 320x256)
sleep 5
curl -s "$BASE/screen" -o frame.ppm

# 4. Stop (returns promptly even if the emulator is stuck)
curl -s -X POST "$BASE/stop"
```

Convert the captured PPM to PNG for a normal image viewer:

```bash
python3 -c "from PIL import Image; Image.open('frame.ppm').save('frame.png')"
# or with ImageMagick:  convert frame.ppm frame.png
```

## WebSocket control (`/ws`)

- Connect at `ws://localhost:5080/ws`.
- Send `{wantScreen:true}` to start receiving pushed frames.
- Server pushes `{type:"frame", tact, cpu, screenB64?}`.
- Control commands (run/stop/load) still go over **HTTP** in v1; there is no client control
  frame on the socket yet.

```bash
# using websocat if available:
echo '{"wantScreen":true}' | websocat ws://localhost:5080/ws
```

## Quick checklist before a command "doesn't work"

1. Is the server actually up? `curl -s "$BASE/status"` should return JSON, not hang.
2. For file uploads: did you use `-F` with `@path;type=…` (not `@path@type`, not `-d @path`)?
3. Did you keep the file extension so format detection works?
4. Is it a read or write action — is the HTTP method correct (`GET` vs `POST`)?
5. Did you load a snapshot before calling `/run`, `/step`, or `/screen`?

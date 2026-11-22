# Kozynax Web API

Headless HTTP + WebSocket control surface over the Kozynax emulation/debug engine
(`src/Kozynax.WebApi`). Base URL below is `http://localhost:5080` by default.

Everything runs headless (offscreen SDL); no display/window required.

Need to know *how* to format requests (JSON vs multipart, curl pitfalls)? See
[How to send commands to the API](./sending-command-to-api.md).

## Quick start

```bash
# Start the server (headless)
dotnet run --project src/Kozynax.WebApi -c Release

# or publish a single-file Linux binary (optional P7 flag):
dotnet publish src/Kozynax.WebApi/Kozynax.WebApi.csproj -c Release \
  -p:WebApiSingleFile=true -o dist/webapi
./dist/webapi/Kozynax.WebApi
```

> **`dotnet` may not be on your `PATH`.** On this machine the CLI lives at
> `/home/can/.dotnet/dotnet`, so call it with the full path, e.g.
> `/home/can/.dotnet/dotnet run --project src/Kozynax.WebApi -c Release`.
> Set `DOTNET_CLI_TELEMETRY_OPTOUT=1` to silence telemetry prompts.

## HTTP endpoints

### Status / CPU
| Method | Path | Body | Returns |
|--------|------|------|---------|
| GET  | `/status`  | –        | emulator status object |
| GET  | `/cpu`     | –        | live CPU registers/state |

```bash
curl -s localhost:5080/status
curl -s localhost:5080/cpu
```

### Lifecycle
| Method | Path | Body | Returns |
|--------|------|------|---------|
| POST | `/run`   | –              | `{status:"running"}` |
| POST | `/stop`  | –              | `{status:"stopped"}` (off-thread; returns promptly) |
| POST | `/reset` | –              | `{status:"reset"}` |
| POST | `/step`  | `{count?}`*    | `{stepped:n}` |

\* `count` is clamped to `1..1_000_000`; defaults to `1`.

```bash
curl -s -X POST localhost:5080/run
curl -s -X POST localhost:5080/step -H 'Content-Type: application/json' -d '{"count":1000}'
curl -s -X POST localhost:5080/stop
```

### Loading snapshots / config
| Method | Path | Body | Returns |
|--------|------|------|---------|
| POST | `/snapshot` | multipart **or** `{url}` | `{status:"loaded", machine}` |
| POST | `/config`   | multipart config file | `{status:"config_loaded"}` |

`/snapshot` accepts either a file upload or a JSON body with a downloadable URL:

```bash
# upload a snapshot (filename extension drives format detection)
# NOTE: use @path;type=… — the @path@type form is invalid and curl will send nothing.
curl -s -F "file=@Exolon_1987...z80;type=application/octet-stream" localhost:5080/snapshot

# or load by URL
curl -s -X POST localhost:5080/snapshot -H 'Content-Type: application/json' \
  -d '{"url":"https://example.com/game.z80"}'
```

### Selecting / changing the machine
There is **no dedicated "set machine" endpoint**. The emulator boots with the bus
marked `isDefault="true"` in `machines.config`, which currently defaults to
**Pentagon 128K** (`modelId:4`). To run on a different machine (e.g. ZX Spectrum 48),
POST that machine's bus configuration via `/config` **before** loading a snapshot.

A config file is an XML document whose root is `<VirtualMachine>` and which contains
one `<Bus modelId="…">` node. Extract the desired block from
[`src/ZXMAK2/machines.config`](../src/ZXMAK2/machines.config) and wrap it:

```xml
<VirtualMachine>
  <Bus name="ZX Spectrum 48" modelId="Sinclair48">
    <Device type="ZXMAK2.Hardware.Spectrum.UlaSpectrum48" />
    <Device type="ZXMAK2.Hardware.General.BetaDiskInterface" />
    <Device type="ZXMAK2.Hardware.General.AY8910" frequency="1773400" maskAddr="0xC002" maskData="0xC002" portAddr="0xFFFD" portData="0xBFFD" />
    <Device type="ZXMAK2.Hardware.General.BeeperDevice" bitEar="4" bitMic="3" />
    <Device type="ZXMAK2.Hardware.General.TapeDevice" />
    <Device type="ZXMAK2.Hardware.General.KeyboardDevice" />
    <Device type="ZXMAK2.Hardware.General.KempstonJoystick" noDos="true" mask="0xe0" port="0x1f" />
    <Device type="ZXMAK2.Hardware.General.Debugger" />
    <Device type="ZXMAK2.Hardware.Spectrum.MemorySpectrum48" />
  </Bus>
</VirtualMachine>
```

A ready-made copy lives at `src/Kozynax.WebApi/spectrum48.config`:

```bash
curl -s -F "file=@src/Kozynax.WebApi/spectrum48.config;type=text/xml" localhost:5080/config
# -> {"status":"config_loaded"}
curl -s localhost:5080/status   # machineName:"ZX Spectrum 48", modelId:1
```

`modelId` maps to `ModelId` in
[`src/ZXMAK2.Engine/Entities/ModelId.cs`](../src/ZXMAK2.Engine/Entities/ModelId.cs)
(`Sinclair48`, `Sinclair128`, `SinclairPlus3`, `Pentagon128`, …).

> Per-device creation failures are logged and skipped (not fatal). E.g. the Spectrum 48
> bus's `AY8910` chip needs `IPsgChip` registered, so it loads **without AY audio** —
> video/CPU/memory still work.

`/config` accepts a multipart config-file upload only.

### Memory
| Method | Path | Body | Returns |
|--------|------|------|---------|
| GET  | `/mem/{addr}?len=256` | –           | `{addr, len, hex}` |
| POST | `/mem/{addr}`         | `{hex:".."}` or `{bytes:[…]}` | `{addr, written:n}` |
| GET  | `/memory/dump?addr=&len=256` | – | raw binary dump (`application/octet-stream`) |

`len` is clamped to `1..0x10000`.

> **Addresses must be decimal — hex returns 404.** The route constraint is
> `{addr:int}`, so `/mem/0xF800` (or any `0x…` address) yields `HTTP 404`. Pass the
> address as a plain base-10 integer (`0xF800` → `63488`). The same applies to
> `/breakpoints/{addr}` and the `{addr}` in POST `/mem/{addr}`.

```bash
curl -s 'localhost:5080/mem/63488?len=16'     # 0xF800, decimal
curl -s -X POST localhost:5080/mem/61440 -H 'Content-Type: application/json' -d '{"hex":"A7"}'   # 0x6000
curl -s 'localhost:5080/memory/dump?addr=0&len=256' -o dump.bin
```

### Breakpoints
| Method | Path | Body | Returns |
|--------|------|------|---------|
| GET    | `/breakpoints`           | –            | list of breakpoints |
| POST   | `/breakpoints`           | `{address}`  | `{ok:true}` |
| DELETE | `/breakpoints/{addr}`    | –            | `{ok:true}` |
| POST   | `/breakpoints/clear`     | –            | `{ok:true}` |

```bash
curl -s localhost:5080/breakpoints
curl -s -X POST localhost:5080/breakpoints -H 'Content-Type: application/json' -d '{"address":64000}'
curl -s -X DELETE localhost:5080/breakpoints/64000
curl -s -X POST localhost:5080/breakpoints/clear
```

### Screen
| Method | Path | Body | Returns |
|--------|------|------|---------|
| GET  | `/screen` | – | 320×256 RGB **PPM (P6)** image (`image/pnm`) |

```bash
curl -s localhost:5080/screen -o frame.ppm      # view with most image tools
# convert to PNG if needed:  convert frame.ppm frame.png   (ImageMagick)
```

## WebSocket — `/ws`

Persistent connection at `ws://localhost:5080/ws`.

- **Client → server:** send `{wantScreen:true}` to start receiving pushed frames.
- **Server → client:** `{type:"frame", tact, cpu, screenB64?}` messages.
- Control after the initial frame is still done over HTTP (v1); bidirectional WS control is a future item.

## Errors

Failures return a structured JSON error via `ApiErrorMiddleware` carrying an enum `code`
(e.g. `UnsupportedFormat`, `NoImageLoaded`). See `src/Kozynax.WebApi/Middleware/ApiErrorMiddleware.cs`.

## Not implemented yet (per task plan)

- `/checkpoint` and `/restore` (RZX checkpoint/restore) — planned but not wired to endpoints.
- Bidirectional WS control frames (control currently goes over HTTP).

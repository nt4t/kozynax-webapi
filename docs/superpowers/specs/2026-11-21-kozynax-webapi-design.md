# Kozynax Web API — Design Spec

**Status:** Approved design, pending implementation plan
**Date:** 2026-11-21
**Scope:** New ASP.NET Core web service that exposes the existing Kozynax emulation/debug engine over HTTP + WebSocket. UI-less server; does not touch WinForms/DX hosts.

---

## 1. Purpose

Add a headless web API to Kozynax (cross-platform ZX Spectrum emulator) that can **load and run** snapshot/ROM/tape files, **control the debugger**, perform **memory dumps/writes**, and stream live CPU state and screen frames over WebSocket. This enables remote debugging, tooling, and scripted automation against a running machine.

## 2. Context & Findings

- The emulation engine (`ZXMAK2.Engine.Cpu`, `ZXMAK2.Hardware`) is UI-agnostic; rendering/sound plug in through host interfaces (`IHostVideo`, `IHostSound`, …).
- **`IDebuggable`** (`ZXMAK2.Engine.Interfaces`) is the clean control surface the API will wrap:
  - Run/stop/control: `DoRun()`, `DoStop()`, `DoReset()`, `DoStepInto()`, `DoStepOver()`
  - Memory: `ReadMemory(addr)`, `WriteMemory(addr,val)`, bulk `ReadMemory`/`WriteMemory(addr, buf, off, len)`
  - Breakpoints: `AddBreakpoint`, `RemoveBreakpoint`, `GetBreakpointList`, `ClearBreakpoints`
  - State/streaming: `CPU` (CpuUnit), `Bus` (IBus), `IsRunning`, `GetFrameTact`, `FrameTactCount`, `UpdateState` event, `RaiseUpdateState()`
  - Checkpoints: `RzxState` (RZX snapshot save/restore)
- A GDB server (`ZXMAK2.Hardware.GdbServer`) already consumes `IDebuggable`, proving it as the abstraction. No emulation/debug logic needs reimplementing.
- The SDL host runs **headless** (`SDL video driver: offscreen`), so the emulator can run with no window/display — the natural runtime for a server.
- **No HTTP layer exists today.** Web API is greenfield.

## 3. Decisions

| # | Decision | Choice |
|---|----------|--------|
| 1 | Runtime host | **SDL headless** (reuses engine + offscreen video, minimal new glue) |
| 2 | Concurrency model | **Persistent session** (one running emulator served by all clients; state lives across calls) |
| 3 | Live transport | **WebSocket** (bidirectional: push ticks + accept control on one socket); SSE noted as an optional pure-pull alternative for dashboards |

## 4. Architecture & Layering

New project **`src/Kozynax.WebApi`** (`net10.0`, `OutputType=Exe`), ASP.NET Core Minimal APIs + Kestrel. References `Kozynax.Sdl` and `ZXMAK2.Engine`; excludes WinForms/DX projects the same way the SDL csproj does. Runs in offscreen SDL mode.

Bounded units:

- **`WebApiHost`** — bootstraps the SDL offscreen host, builds one `IDebuggable`, starts Kestrel, registers DI. Owns startup/shutdown.
- **`EmulatorSession`** (singleton) — wraps the persistent `IDebuggable`. Exposes typed methods: load/stop/reset/step wrappers, memory read/write helpers, breakpoint management, and a **frame-pump** tied to `GetFrameTact`/`FrameTactCount` that advances the emulator at a capped rate. Thread-safe (single-writer lock for run/step/memory writes; readers snapshot under a read lock).
- **`StreamHub`** — manages connected WebSocket clients; broadcasts frame/register snapshots and decodes inbound control frames to forward to `EmulatorSession`. Bounded send channel (drop-oldest or block) so a slow client can't stall emulation or OOM.
- **Endpoints** — thin Minimal API handlers over `EmulatorSession`; one `/ws` live channel plus HTTP for one-shot actions and file upload.

## 5. Endpoints

### HTTP
- `POST /snapshot` — multipart upload (`file`) or JSON `{ "url": "..." }`; loads ROM/snapshot/tape into the session. `200 { status, machine }`.
- `POST /run`, `POST /stop`, `POST /reset`, `POST /step` (body `{ "count"? }`).
- `GET /cpu` — Z80 registers + state from `CPU`.
- `GET /mem/{addr}`?len=N — dump N bytes as hex (`len` default e.g. 256).
- `POST /mem/{addr}` — write raw/hex bytes at address.
- `GET /memory/dump`?len=N — raw binary download (`application/octet-stream`) for disk-image style dumps.
- `GET /screen` — current frame buffer as PNG (via existing StbImageWriteSharp).
- `GET /breakpoints`, `POST /breakpoints`, `DELETE /breakpoints/{id}`.
- `POST /checkpoint`, `POST /restore/{id}` via `RzxState`.
- `GET /status` — `{ running, fps, frameTact, cpuClock, loaded }`.

### WebSocket `/ws`
- **Server → client** (JSON): `{"type":"frame","fps":50,"registers":{...},"cpu":{"im","ei","iff",...},"tact":N,"screenB64":"..."}` pushed at a capped rate (default ~20–30 Hz; cadence derived from `FrameTactCount`).
- **Client → server** (JSON): `{"cmd":"step"|"run"|"stop"|"reset"|"writeMem","addr":..,"value":..,"bytes":[..],"bpAdd":..,"bpRemove":..}`.

## 6. Streaming Cadence & Backpressure

The frame-pump advances the emulator and publishes ticks; WS send is serialized through a bounded channel so slow clients are throttled rather than blocking emulation or exhausting memory. Frame rendering uses the offscreen SDL buffer via `IHostVideo`.

## 7. Error Handling & Safety

- Load failures → `400` with machine-readable `error.code`: `INVALID_FILE`, `UNSUPPORTED_FORMAT`, `MACHINE_BUSY`.
- Memory writes outside addressable space, or while locked, → `400`; invalid address ranges rejected client- and server-side.
- Single-writer lock around run/step/memory writes; concurrent readers snapshot under a read lock. No unsynchronized shared emulator access.

## 8. Testing

- Unit tests on `EmulatorSession` using a **mock `IDebuggable`** (no SDL required): load, step, mem read/write, breakpoints, checkpoint round-trip.
- Integration tests with `WebApplicationFactory` hitting endpoints and a WebSocket client exercising the stream protocol.
- Extend existing `tests/Kozynax.Tests.csproj`.

## 9. Build & Packaging

Publish alongside the SDL build; an optional flag bundles it into the single-file Linux release (`dist/linux-x64`) so one container/image serves both app and API. Add a CI matrix row for web-api publish + test in `.github/workflows/package.yml`.

## 10. Out of Scope (future)

- SSE-only pure-pull streaming endpoint (noted in §3).
- Multi-machine pooling / horizontal scaling beyond the persistent single session.
- Auth — not required for a local dev/debug surface; revisit if exposed publicly.

# Task Plan — Kozynax Web API

**Goal:** Add a headless web API (`src/Kozynax.WebApi`) exposing the existing emulation/debug engine over HTTP + WebSocket. Design spec: `docs/superpowers/specs/2026-11-21-kozynax-webapi-design.md`.

**Locked decisions:** SDL headless host · persistent session · WebSocket live streaming (bidirectional control + push).

## Phases

- [x] **P0. Scaffold project** — created `src/Kozynax.WebApi/Kozynax.WebApi.csproj` (net10.0, Exe, ASP.NET Core); builds clean in Release.
- [ ] **P1. Headless emulator bootstrap** — `WebApiHost`: init Silk.NET SDL offscreen, build SDL video/sound host services, construct `VirtualMachine` (IDebuggable) directly via Locator/`ICommandManager`, skip IMainView/UI layer. Verify it boots and stops without a display.
- [ ] **P2. EmulatorSession service** — singleton wrapping `IDebuggable`: load(image/url), run/stop/reset/step, memory read/write (bulk), breakpoints, RZX checkpoint/restore, status. Thread-safe single-writer / multi-reader. Frame-pump tied to `GetFrameTact`/`FrameTactCount`.
- [ ] **P3. HTTP endpoints** — Minimal API: `/snapshot`, `/run`,`/stop`,`/reset`,`/step`, `/cpu`, `/mem/{addr}`, `/memory/dump`, `/screen`(PNG), `/breakpoints`, `/checkpoint`,`/restore`, `/status`. Typed error codes.
- [ ] **P4. WebSocket streaming** — `/ws`: `StreamHub` manages clients, bounded send channel (backpressure), server→client frame JSON (registers/cpu/tact/screenB64), client→server control frames.
- [x] **P5. Tests** — `WebApplicationFactory` integration + WS client test harness (`tests/WebSockets/Kozynax.WebApi.Tests.csproj`). Both tests pass.
  - Fixed compile errors: dropped `using var` on non-disposable `WebSocketClient`; replaced protected `_factory.Client` with public `_factory.Server.CreateClient()`.
  - Fixed a real server crash unblocking the tests: `HeadlessEmulatorFactory` never initialized the engine Locator, so `BusManager`'s catch dereferenced null `Locator.Resolve<IUserMessage>()` during `VirtualMachine.Init()`. Registering silent `IUserMessage -> UserMessage` before boot fixes it.
- [ ] **P6. Build, run, verify** — `dotnet build`, `dotnet test`; launch server headless, exercise endpoints/WS with curl/websocket client.
- [ ] **P7. Packaging hook** — optional flag to bundle WebApi into single-file Linux release; CI matrix row.

## Errors Encountered
| Error | Cause | Resolution |
|-------|-------|-----------|
| `AcceptAsync` missing | net10 renamed the WebSocket accept API | use `AcceptWebSocketAsync()` |
| `IsMultipartContentType` / `ReadFormAsync` missing | net10 removed form-binding helpers | read `Request.Body` + content-type check; pass no `FormOptions` |
| Channels `BoundedChannelFillMode`/`.FillMode` | this SDK build uses legacy names | `BoundedChannelFullMode.FullMode` |
| NETSDK1151 (self-contained exe) | WebApi can't reference Kozynax.Sdl | keep the plain recompiled `Kozynax.Engine` as the engine ref |
| CS0433 duplicate types | Engine + Sdl both referenced in one project | isolate WS tests in a separate project referencing only WebApi |

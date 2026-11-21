# Progress — Kozynax Web API

## Session log
- [x] Explored codebase; confirmed `IDebuggable`/`VirtualMachine` seam, SDL offscreen host, no existing HTTP layer.
- [x] Wrote design spec → committed (`09badbd`). Added `dist` to `.gitignore` (`7b33c35`).
- [x] Created planning files (task_plan.md, findings.md, progress.md).
- [x] **P0 Scaffold project** — `src/Kozynax.WebApi` builds clean in Release.
  - Fixed `StreamHub.cs`: channels API (`BoundedChannelFullMode.FullMode`, `.Writer.TryWrite`), `ctx.RequestServices`;
    switched to `AcceptWebSocketAsync()` (net10 renamed `AcceptAsync`).
  - Fixed `WebEndpoints.cs`: form binding via `Request.Body.CopyToAsync` + content-type check (net10 dropped the
    `IsMultipartContentType`/`ReadFormAsync` extension helpers); `/screen` reads `frame.Buffer`/`Width`/`Height`.
  - Fixed `ApiErrorMiddleware.cs`: passes enum code, not `.Code.ToString()`.
  - Rewrote csproj to drop broken nested ProjectResource paths (icon/config/roms) that only resolve under `src/`.
- [x] **P4/P5 WebSocket test harness** — added in-process integration tests via `WebApplicationFactory<Program>`.
- [x] **P5 Tests pass** — both `StreamHubWebSocketTests` green. Fixed compile errors (`using var` on non-disposable `WebSocketClient`; protected `_factory.Client` -> public `_factory.Server.CreateClient()`). Also fixed a real server crash blocking the tests: `HeadlessEmulatorFactory` never initialized the engine Locator, so `BusManager`'s device-load catch dereferenced null `Locator.Resolve<IUserMessage>()` and threw NRE during `VirtualMachine.Init()`. Registering a silent `IUserMessage -> UserMessage` before boot fixes both.

## Build/test commands
```bash
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1
# WebApi builds standalone (no RID needed):
dotnet build src/Kozynax.WebApi -c Release
# WebSocket integration tests (isolated project, references only Kozynax.WebApi):
dotnet test tests/WebSockets/Kozynax.WebApi.Tests.csproj -c Release \
  --filter "FullyQualifiedName~StreamHubWebSocketTests"
```

## Resolved / remaining
Both WebSocket compile errors are fixed and both tests pass (`dotnet test ... --filter FullyQualifiedName~StreamHubWebSocketTests`).
The WebApi builds clean in Release. Next phase is P6: launch the server headless and exercise the HTTP + WS endpoints.

## Notes
- SDL host uses custom Locator DI; ASP.NET DI only for web layer.
- `Kozynax.Engine` is a plain recompile of `Kozynax.Sdl`'s sources so ASP.NET can reference the engine without pulling
  the self-contained SDL exe (NETSDK1151). Do not reference both in one project — duplicate types cause CS0433.

# Progress — Kozynax Web API

## Session log
- [x] Explored codebase; confirmed `IDebuggable`/`VirtualMachine` seam, SDL offscreen host, no existing HTTP layer.
- [x] Wrote design spec → committed (`09badbd`). Added `dist` to `.gitignore` (`7b33c35`).
- [x] Created planning files (task_plan.md, findings.md, progress.md).
- [x] **P0 Scaffold project** — `src/Kozynax.WebApi` builds clean in Release.
  - Fixed `StreamHub.cs`: channels API (`BoundedChannelFullMode.FullMode`, `.Writer.TryWrite`), `ctx.RequestServices`;
    switched to `AcceptWebSocketAsync()` (net10 renamed `AcceptAsync`).
  - Fixed `WebEndpoints.cs`: multipart form parsing via `HttpRequest.ReadFormAsync()` + `IFormFile` (net10 still ships these;
    the earlier raw-body approach never parsed the multipart envelope, so real uploads could not work); `/screen` reads
    `frame.Buffer`/`Width`/`Height`. Later refined in P6 to preserve the uploaded filename's extension for format detection.
  - Fixed `ApiErrorMiddleware.cs`: passes enum code, not `.Code.ToString()`.
  - Rewrote csproj to drop broken nested ProjectResource paths (icon/config/roms) that only resolve under `src/`.
- [x] **P4/P5 WebSocket test harness** — added in-process integration tests via `WebApplicationFactory<Program>`.
- [x] **P5 Tests pass** — both `StreamHubWebSocketTests` green. Fixed compile errors (`using var` on non-disposable `WebSocketClient`; protected `_factory.Client` -> public `_factory.Server.CreateClient()`). Also fixed a real server crash blocking the tests: `HeadlessEmulatorFactory` never initialized the engine Locator, so `BusManager`'s device-load catch dereferenced null `Locator.Resolve<IUserMessage>()` and threw NRE during `VirtualMachine.Init()`. Registering a silent `IUserMessage -> UserMessage` before boot fixes both.
- [x] **P7. Packaging hook** — optional self-contained single-file Web API release.
  - `WebApiSingleFile` MSBuild property (csproj, off by default): linux-x64 RID + --self-contained + PublishSingleFile;
    verified ~106 MB one-exe output that runs without a runtime; default build stays multi-file (16 dll sidecars).
  - `packaging/webapi/make-linux-singlefile.sh` publishes via the flag and reuses make-zip.sh (strips pdbs, packs roms).
  - Added `linux-webapi` job to package.yml gated behind a new `publish_webapi` workflow_dispatch input; added to release.
- [x] **P6. Build, run, verify** — launched headless; exercised every HTTP endpoint + WS end-to-end (curl + Node global WebSocket).
  - Live-passed: `/config`→Spectrum 48K, `/snapshot` loads a real game (`Exolon_1987...z80`), `/run`, `/cpu` shows live registers,
    `/screen` streams a valid 320×256 P6 image, `/stop`; plus `/mem`, `/memory/dump`, breakpoints CRUD. WS handshake/close covered by
    the in-process `StreamHubWebSocketTests`.
  - Fixed upload bug found by verify: `/snapshot` & `/config` gated on `"multipart"` but read the raw body — real uploads never worked.
    Rewrote to `ReadFormAsync()`/`IFormFile`; kept JSON `{url}` path; stopped stripping the filename extension (LoadManager needs it).
  - Fixed server-wedge found by verify: `DoStop()` joins the render thread, which blocks when the emulator is stuck (e.g. a tape game
    waiting for media headless); holding the writer lock across it froze every endpoint. `EmulatorSession.Stop()` now runs `DoStop`
    off-thread with a 10s bounded wait → `/stop` returns promptly, server stays responsive, clean stop still attempted.

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
The WebApi builds clean in Release. P6–P7 are complete (headless launch + full endpoint/WS verification, plus optional
single-file Linux packaging hook wired into CI). All planned phases through P7 are done; no outstanding work remains.

## Notes
- SDL host uses custom Locator DI; ASP.NET DI only for web layer.
- `Kozynax.Engine` is a plain recompile of `Kozynax.Sdl`'s sources so ASP.NET can reference the engine without pulling
  the self-contained SDL exe (NETSDK1151). Do not reference both in one project — duplicate types cause CS0433.

# Findings — Kozynax Web API

## Architecture seams (verified)
- **`VirtualMachine : IVirtualMachine, IDebuggable`** (`src/ZXMAK2.Engine/VirtualMachine.cs`).
  - Ctor: `VirtualMachine(IHostService host, ICommandManager commandManager)`; sets up `Spectrum`, bus manager, sample rate from host.
  - Lifecycle: `.Init()`, `.DoRun()`, `.DoStop()`, `.DoReset()`, `.DoStepInto()`, `.DoStepOver()`, `.Dispose()` (calls DoStop + host.CancelPush()).
- **`IDebuggable`** (`ZXMAK2.Engine.Interfaces`) — the control surface to wrap:
  - Run/stop: `DoRun/DoStop/DoReset/DoStepInto/DoStepOver`; `RaiseUpdateState()`.
  - Memory: `ReadMemory(ushort)`, `WriteMemory(ushort,byte)`, bulk `ReadMemory(addr,buf,off,len)` / `WriteMemory(...)`.
  - Breakpoints: `AddBreakpoint/RemoveBreakpoint/ClearBreakpoints/GetBreakpointList`.
  - State: `CPU` (CpuUnit), `Bus` (IBus), `IsRunning`, `GetFrameTact`, `FrameTactCount`, `RzxState` (IRZXState).
- **SDL host services** (`src/ZXMAK2.Host.Sdl/`): `SdlVideo : IHostVideo`, `SdlSound : IHostSound`. Wired through custom **`Locator` / `ResolverSimple`** DI (not ASP.NET DI); need Silk.NET SDL API instance + `SdlRuntimeContext` registered.
- **GDB server** (`ZXMAK2.Hardware.GdbServer`) already consumes `IDebuggable` → proves the abstraction; can be a reference for register/memory access patterns.

## Bootstrap reality (risk to watch)
- Full app bootstraps via `Locator.Init(resolver)` then `ILauncher.Run(args)` which **requires an `IMainView`** (`SdlMainView`) — needs SDL window init. For headless we must **skip the view/Launcher** and construct `VirtualMachine` directly, reusing only the SDL video/sound host services in offscreen mode.
- Need to confirm how `SdlVideo`/`SdlSound` get their Silk.NET SDL instance (registered as instance in Program.cs) and whether they can run with `SDL_VIDEODRIVER=offscreen` / no window.

## Conventions
- Engine uses custom resolver (`ZXMAK2.Dependency`), not Microsoft DI — WebApiHost will use Locator for host services, ASP.NET DI only for web layer.
- Excludes WinForms/DX via `<Compile Remove>` (see `src/Kozynax.Sdl.csproj`) — replicate in new csproj.
- Build/test uses .NET 10 SDK at `~/.dotnet` (installed 10.0.404). Export `DOTNET_ROOT`/`PATH`.

## Out of scope / future
- SSE-only pull endpoint; multi-machine pooling; auth.

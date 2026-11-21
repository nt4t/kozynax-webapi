using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Kozynax.WebApi.Host;
using ZXMAK2.Engine;
using ZXMAK2.Engine.Entities;
using ZXMAK2.Engine.Interfaces;

namespace Kozynax.WebApi.Services;

/// <summary>
/// Singleton that owns the running emulator and exposes it to the web layer. All mutating
/// operations are serialized through a single writer lock; reads snapshot under the same lock.
/// </summary>
public sealed class EmulatorSession
{
    private readonly object _lock = new();
    private readonly HeadlessEmulator _emulator;
    private bool _loaded;
    private bool _disposed;

    public EmulatorSession()
    {
        _emulator = HeadlessEmulatorFactory.Create();
        _emulator.Video.NewFrame += OnNewFrame;
    }

    public IDebuggable Machine => _emulator.Machine;
    public CapturingVideo Video => _emulator.Video;

    /// <summary>Raised on each rendered frame with a marshalled snapshot for WS streaming.</summary>
    public event Action<SessionFrame>? Frame;

    private void OnNewFrame(FrameSnapshot s)
    {
        IDebuggable vm;
        int[] pixels; int w, h; float ratio;
        lock (_lock)
        {
            vm = _emulator.Machine;
            pixels = (int[])s.Buffer.Clone();
            w = s.Width; h = s.Height; ratio = s.Ratio;
        }
        var cpu = CpuStatus.Take(vm);
        Frame?.Invoke(new SessionFrame(cpu, s.StartTact, pixels, w, h, ratio));
    }

    // ---- Lifecycle ---------------------------------------------------------

    /// <summary>Load an image/snapshot/tape/disk file into the current machine.</summary>
    public string LoadFromFile(string path)
    {
        ThrowIfDisposed();
        if (!File.Exists(path)) throw new ApiError(ApiErrorCode.FileNotFound, path);
        var full = Path.GetFullPath(path);

        lock (_lock)
        {
            if (_emulator.Machine.IsRunning) throw new ApiError(ApiErrorCode.MachineBusy);
            var bus = _emulator.Machine.Bus as BusManager;
            var lm = bus?.LoadManager;
            if (lm == null) throw new ApiError(ApiErrorCode.UnsupportedFormat, full);
            using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!lm.CheckCanOpenFileStream(full, fs))
                throw new ApiError(ApiErrorCode.UnsupportedFormat, full);
            var name = lm.OpenFileStream(full, fs) ?? Path.GetFileName(full);
            _loaded = true;
            return name;
        }
    }

    /// <summary>Load bytes under a logical filename (e.g. from an upload).</summary>
    public string LoadFromBytes(string fileName, ReadOnlySpan<byte> data)
    {
        ThrowIfDisposed();
        lock (_lock)
        {
            if (_emulator.Machine.IsRunning) throw new ApiError(ApiErrorCode.MachineBusy);
            var bus = _emulator.Machine.Bus as BusManager;
            var lm = bus?.LoadManager;
            if (lm == null) throw new ApiError(ApiErrorCode.UnsupportedFormat, fileName);
            using var ms = new MemoryStream(data.ToArray(), writable: false);
            if (!lm.CheckCanOpenFileStream(fileName, ms))
                throw new ApiError(ApiErrorCode.UnsupportedFormat, fileName);
            ms.Position = 0;
            var name = lm.OpenFileStream(fileName, ms) ?? fileName;
            _loaded = true;
            return name;
        }
    }

    /// <summary>Load a machine configuration XML (sets up bus/devices for specialized clones).</summary>
    public void LoadConfig(string path)
    {
        ThrowIfDisposed();
        if (!File.Exists(path)) throw new ApiError(ApiErrorCode.FileNotFound, path);
        lock (_lock)
        {
            if (_emulator.Machine.IsRunning) throw new ApiError(ApiErrorCode.MachineBusy);
            _emulator.Vm.OpenConfig(path);
            _loaded = true;
        }
    }

    public void Run()
    {
        ThrowIfDisposed();
        lock (_lock)
        {
            if (!_loaded) throw new ApiError(ApiErrorCode.NoImageLoaded);
            if (!_emulator.Machine.IsRunning) _emulator.Machine.DoRun();
        }
    }

    public void Stop()
    {
        ThrowIfDisposed();
        if (!_emulator.Machine.IsRunning) return;
        // DoStop joins the render thread, which can block indefinitely when the emulator is stuck
        // (e.g. a tape-based game waiting for media in headless mode). Joining on the request
        // thread while holding the writer lock wedges every endpoint, so run it off-thread with a
        // bounded wait: /stop returns promptly and the server stays responsive. The engine still
        // attempts a clean stop; we never force-abort a stuck thread.
        try
        {
            var stop = Task.Run(() => _emulator.Machine.DoStop());
            stop.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        catch
        {
            // Leave the emulator running if it cannot be stopped cleanly; a wedged server is worse.
        }
    }

    public void Reset()
    {
        ThrowIfDisposed();
        lock (_lock) _emulator.Machine.DoReset();
    }

    public int Step(int count = 1)
    {
        ThrowIfDisposed();
        if (count <= 0) throw new ArgumentException("step count must be > 0", nameof(count));
        lock (_lock)
        {
            for (int i = 0; i < count; i++)
            {
                if (_emulator.Machine.IsRunning) _emulator.Machine.DoStop();
                _emulator.Machine.DoStepInto();
            }
            return count;
        }
    }

    // ---- Memory ------------------------------------------------------------

    public byte[] ReadMemory(ushort addr, int length)
    {
        ThrowIfDisposed();
        if (length <= 0) throw new ArgumentException("length must be > 0", nameof(length));
        lock (_lock)
        {
            var buf = new byte[length];
            _emulator.Machine.ReadMemory(addr, buf, 0, length);
            return buf;
        }
    }

    public void WriteMemory(ushort addr, byte[] data)
    {
        ThrowIfDisposed();
        if (data.Length == 0) throw new ArgumentException("no bytes to write", nameof(data));
        lock (_lock) _emulator.Machine.WriteMemory(addr, data, 0, data.Length);
    }

    // ---- Breakpoints -------------------------------------------------------

    public void AddBreakpoint(ushort addr)
    {
        ThrowIfDisposed();
        lock (_lock) _emulator.Machine.AddBreakpoint(new Breakpoint(addr));
    }

    public void RemoveBreakpoint(ushort addr)
    {
        ThrowIfDisposed();
        lock (_lock)
        {
            foreach (var bp in _emulator.Machine.GetBreakpointList())
                if (bp.Address == addr) { _emulator.Machine.RemoveBreakpoint(bp); return; }
        }
    }

    public void ClearBreakpoints()
    {
        ThrowIfDisposed();
        lock (_lock) _emulator.Machine.ClearBreakpoints();
    }

    // ---- Status ------------------------------------------------------------

    public CpuStatus CpuState()
    {
        lock (_lock) return CpuStatus.Take(_emulator.Machine);
    }

    public IEnumerable<BreakpointInfo> BreakpointList()
    {
        lock (_lock)
            return _emulator.Machine.GetBreakpointList()
                .Select(b => new BreakpointInfo(b.Address ?? 0, b.Label))
                .ToArray();
    }

    public FrameSnapshot? LatestFrame()
    {
        return _emulator.Video.Latest();
    }

    public Status Status()
    {
        lock (_lock)
        {
            var vm = _emulator.Machine;
            var bus = vm.Bus as BusManager;
            return new Status(
                vm.IsRunning, _loaded, bus?.Name ?? "",
                bus != null ? (int)bus.ModelId : 0,
                vm.GetFrameTact(), vm.FrameTactCount);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _emulator.Machine.DoStop(); } catch { /* ignore */ }
        _emulator.Video.NewFrame -= OnNewFrame;
        _emulator.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(EmulatorSession));
        GC.SuppressFinalize(this);
    }
}

// ---- DTOs -----------------------------------------------------------------

public enum ApiErrorCode { FileNotFound, UnsupportedFormat, MachineBusy, NoImageLoaded }

public sealed class ApiError : Exception
{
    public ApiErrorCode Code { get; }
    public string MessageDetail { get; }
    public ApiError(ApiErrorCode code, string? detail = null) : base(detail ?? code.ToString())
    {
        Code = code; MessageDetail = detail ?? string.Empty;
    }
}

public sealed record Status(bool Running, bool Loaded, string MachineName, int ModelId, int FrameTact, int FrameTactCount);

public sealed record BreakpointInfo(ushort Address, string Label);

public sealed record SessionFrame(CpuStatus Cpu, int StartTact, int[] Pixels, int Width, int Height, float Ratio);

public sealed record CpuStatus(
    ushort PC, ushort AF, ushort BC, ushort DE, ushort HL, ushort IX, ushort IY, ushort IR, ushort SP, ushort MEMPTR,
    byte A, byte F, byte B, byte C, byte D, byte E, byte H, byte L,
    bool HALTED, bool INT, bool NMI, bool RST, byte IM)
{
    public static CpuStatus Take(IDebuggable vm)
    {
        var c = vm.CPU;
        var r = c.regs;
        return new CpuStatus(
            r.PC, r.AF, r.BC, r.DE, r.HL, r.IX, r.IY, r.IR, r.SP, r.MW,
            r.A, r.F, r.B, r.C, r.D, r.E, r.H, r.L,
            c.HALTED, c.INT, c.NMI, c.RST, c.IM);
    }
}

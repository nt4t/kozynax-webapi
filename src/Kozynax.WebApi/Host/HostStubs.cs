using System;
using ZXMAK2.Engine.Interfaces;
using ZXMAK2.Host.Entities;
using ZXMAK2.Host.Interfaces;
using ZXMAK2.Mvvm;

namespace Kozynax.WebApi.Host;

/// <summary>
/// Managed video host that buffers the latest emulator frame so /screen can dump it.
/// No native SDL window is required — this keeps the Web API assembly free of Silk.NET.
/// </summary>
public sealed class CapturingVideo : IHostVideo
{
    private readonly object _sync = new();
    private IFrameVideo? _latest;

    /// <summary>Raised on every PushFrame with a snapshot of CPU state + the buffered frame.</summary>
    public event Action<FrameSnapshot>? NewFrame;

    public bool IsSyncSupported => false;
    public bool IsSynchronized { get; set; } = false;

    public void PushFrame(IFrameInfo info, IFrameVideo frame)
    {
        if (frame == null) return;
        lock (_sync)
        {
            _latest = frame;
        }
        NewFrame?.Invoke(new FrameSnapshot(info.StartTact, info.UpdateTime, frame.Buffer, frame.Size.Width, frame.Size.Height, frame.Ratio));
    }

    public void CancelWait() { }

    public IFrameVideo? Snapshot()
    {
        lock (_sync) return _latest;
    }

    /// <summary>Snapshot of the latest frame as an immutable value, safe off the video thread.</summary>
    public FrameSnapshot? Latest()
    {
        lock (_sync)
        {
            if (_latest is null) return null;
            return new FrameSnapshot(0, 0.0, _latest.Buffer, _latest.Size.Width, _latest.Size.Height, _latest.Ratio);
        }
    }

    public void Dispose() => NewFrame = null!;
}

/// <summary>Immutable snapshot of a single rendered frame, safe to marshal off the video thread.</summary>
public readonly record struct FrameSnapshot(int StartTact, double UpdateTime, int[] Buffer, int Width, int Height, float Ratio);

internal sealed class NoOpSound : IHostSound
{
    public int SampleRate => 44100;
    public bool IsSyncSupported => false;
    public bool IsSynchronized { get; set; } = false;
    public void PushFrame(IFrameInfo info, IFrameSound frame) { }
    public void CancelWait() { }
    public void Dispose() { }
}

internal sealed class NoOpKeyboard : IHostKeyboard
{
    public void Scan() { }
    public IKeyboardState State => NoOpKeyboardState.Instance;
    public void Dispose() { }
}

internal sealed class NoOpKeyboardState : IKeyboardState
{
    public static readonly NoOpKeyboardState Instance = new();
    public bool this[Key key] => false;
}

internal sealed class NoOpMouse : IHostMouse
{
    public IMouseState MouseState => NoOpMouseState.Instance;
    public bool IsCaptured => false;
    public void Scan() { }
    public void Capture() { }
    public void Uncapture() { }
    public void Dispose() { }
}

internal sealed class NoOpMouseState : IMouseState
{
    public static readonly NoOpMouseState Instance = new();
    public int X => 0;
    public int Y => 0;
    public int Buttons => 0;
}

internal sealed class NoOpJoystick : IHostJoystick
{
    public void CaptureHostDevice(string hostId) { }
    public void ReleaseHostDevice(string hostId) { }
    public void Scan() { }
    public IJoystickState GetState(string hostId) => NoOpJoystickState.Instance;
    public IKeyboardState KeyboardState { set { } }
    public bool IsKeyboardStateRequired => false;
    public IEnumerable<IHostDeviceInfo> GetAvailableJoysticks() => Array.Empty<IHostDeviceInfo>();
    public void Dispose() { }
}

internal sealed class NoOpJoystickState : IJoystickState
{
    public static readonly NoOpJoystickState Instance = new();
    public bool IsLeft => false;
    public bool IsRight => false;
    public bool IsUp => false;
    public bool IsDown => false;
    public bool IsFire => false;
}

internal sealed class NoOpCommandManager : ICommandManager
{
    public void Clear() { }
    public void Add(ICommand command) { }
}

using ZXMAK2.Dependency;
using ZXMAK2.Engine;
using ZXMAK2.Engine.Interfaces;
using ZXMAK2.Host.Interfaces;
using ZXMAK2.Host.Services;

namespace Kozynax.WebApi.Host;

/// <summary>Container returned by <see cref="HeadlessEmulatorFactory"/>.</summary>
public sealed class HeadlessEmulator : IDisposable
{
    public IDebuggable Machine { get; }
    public VirtualMachine Vm { get; }
    public CapturingVideo Video { get; }

    public HeadlessEmulator(VirtualMachine machine, CapturingVideo video)
    {
        Machine = machine;
        Vm = machine;
        Video = video;
    }

    public void Dispose()
    {
        (Machine as IDisposable)?.Dispose();
        Video.Dispose();
    }
}

/// <summary>
/// Builds a headless emulator: a VirtualMachine (IDebuggable) driven by managed, window-less
/// host services. No SDL window or native audio is required.
/// </summary>
public static class HeadlessEmulatorFactory
{
    public static HeadlessEmulator Create()
    {
        // The engine's BusManager reports device-load failures through
        // Locator.Resolve<IUserMessage>(). The headless API has no UI, so register a silent
        // UserMessage (no IUserQuery registered => Show() returns early). Without this the
        // catch block dereferences a null Locator service and crashes VirtualMachine.Init().
        var resolver = new ResolverSimple();
        resolver.RegisterType<IUserMessage, UserMessage>();
        Locator.Init(resolver);

        var video = new CapturingVideo();
        var sound = new NoOpSound();
        var keyboard = new NoOpKeyboard();
        var mouse = new NoOpMouse();
        var joystick = new NoOpJoystick();

        var host = new HostService(video, sound, keyboard, mouse, joystick);
        var machine = new VirtualMachine(host, new NoOpCommandManager());
        machine.Init();

        return new HeadlessEmulator(machine, video);
    }
}

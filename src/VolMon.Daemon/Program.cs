using VolMon.Core.Audio;
using VolMon.Core.Audio.Backends;
using VolMon.Core.Config;
using VolMon.Core.Platform;
using VolMon.Daemon;

var builder = Host.CreateApplicationBuilder(args);

// Register services
builder.Services.AddSingleton<ConfigManager>();
builder.Services.AddSingleton<IAudioBackend>(sp =>
{
    if (OperatingSystem.IsLinux())
        return new LinuxPulseBackend();
    if (OperatingSystem.IsWindows())
        return new WindowsBackend();
    if (OperatingSystem.IsMacOS())
        return new MacOsBackend();

    throw new PlatformNotSupportedException(
        $"No audio backend available for {Environment.OSVersion.Platform}");
});

builder.Services.AddSingleton<IServiceManager>(sp =>
{
    if (OperatingSystem.IsLinux())
        return new LinuxServiceManager();
    if (OperatingSystem.IsMacOS())
        return new MacOsServiceManager();
    if (OperatingSystem.IsWindows())
        return new WindowsServiceManager();

    throw new PlatformNotSupportedException(
        $"No service manager available for {Environment.OSVersion.Platform}");
});

builder.Services.AddHostedService<DaemonService>();

var host = builder.Build();
host.Run();

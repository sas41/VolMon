using VolMon.Core.Audio;
using VolMon.Core.Audio.Backends;
using VolMon.Core.Config;
using VolMon.Daemon;

var builder = Host.CreateApplicationBuilder(args);

// Register services
builder.Services.AddSingleton<ConfigManager>();
builder.Services.AddSingleton<IAudioBackend>(sp =>
{
    if (OperatingSystem.IsLinux())
        return new PulseAudioBackend();
    if (OperatingSystem.IsWindows())
        return new WindowsAudioBackend();
    if (OperatingSystem.IsMacOS())
        return new MacOsAudioBackend();

    throw new PlatformNotSupportedException(
        $"No audio backend available for {Environment.OSVersion.Platform}");
});

builder.Services.AddHostedService<DaemonService>();

var host = builder.Build();
host.Run();

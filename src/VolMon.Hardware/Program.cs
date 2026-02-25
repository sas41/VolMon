using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VolMon.Hardware;
using VolMon.Hardware.Beacn.Mix;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "HH:mm:ss ";
    options.SingleLine = true;
});

// Register device drivers — add new drivers here as hardware support is added
builder.Services.AddSingleton<IDeviceDriver, BeacnMixDriver>();

// Register the bridge service that manages all device sessions
builder.Services.AddHostedService<HardwareBridgeService>();

var host = builder.Build();
await host.RunAsync();

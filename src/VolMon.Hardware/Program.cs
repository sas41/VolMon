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

// Register the Beacn Mix controller
builder.Services.AddSingleton<IHardwareController, BeacnMixController>();

// Register the bridge service that connects hardware events to the daemon via IPC
builder.Services.AddHostedService<HardwareBridgeService>();

var host = builder.Build();
await host.RunAsync();

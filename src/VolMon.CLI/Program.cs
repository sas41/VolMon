using VolMon.Core.Audio;
using VolMon.Core.Ipc;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

await using var client = new IpcDuplexClient();

try
{
    await client.ConnectAsync();

    var response = args[0] switch
    {
        "status" => await client.SendAsync(new IpcRequest { Command = "status" }),
        "list-groups" or "groups" => await client.SendAsync(new IpcRequest { Command = "list-groups" }),
        "list-streams" or "streams" => await client.SendAsync(new IpcRequest { Command = "list-streams" }),
        "list-devices" or "devices" => await client.SendAsync(new IpcRequest { Command = "list-devices" }),
        "set-volume" => await HandleSetVolume(args, client),
        "mute" => await HandleMute(args, client, true),
        "unmute" => await HandleMute(args, client, false),
        "add-group" => await HandleAddGroup(args, client),
        "remove-group" => await HandleRemoveGroup(args, client),
        "add-program" => await HandleAddProgram(args, client),
        "remove-program" => await HandleRemoveProgram(args, client),
        "add-device" => await HandleAddDevice(args, client),
        "remove-device" => await HandleRemoveDevice(args, client),
        "set-default" => await HandleSetDefault(args, client),
        "reload" => await client.SendAsync(new IpcRequest { Command = "reload" }),
        "help" or "--help" or "-h" => null!,
        _ => throw new ArgumentException($"Unknown command: {args[0]}")
    };

    if (args[0] is "help" or "--help" or "-h")
    {
        PrintUsage();
        return 0;
    }

    if (!response.Success)
    {
        Console.Error.WriteLine($"Error: {response.Error}");
        return 1;
    }

    PrintResponse(args[0], response);
    return 0;
}
catch (TimeoutException)
{
    Console.Error.WriteLine("Error: Could not connect to the VolMon daemon. Is it running?");
    Console.Error.WriteLine("Start it with: systemctl --user start volmon");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""
        VolMon CLI - Audio group volume controller

        Usage: volmon <command> [arguments]

        Commands:
          status                            Show daemon status
          groups                            List configured groups
          streams                           List active audio streams
          devices                           List audio devices (sinks/sources)
          set-volume <group> <0-100>        Set group volume
          mute <group>                      Mute a group
          unmute <group>                    Unmute a group
          add-group <name> [volume]         Add a new group
          add-group <name> --default        Add as default group
          remove-group <name>               Remove a group
          set-default <name>                Set a group as the default
          add-program <group> <binary>      Add a program to a group
          remove-program <group> <binary>   Remove a program from a group
          add-device <group> <device-name>  Add a device to a group
          remove-device <group> <device-name> Remove a device from a group
          reload                            Reload config from disk
          help                              Show this help
        """);
}

static void PrintResponse(string command, IpcResponse response)
{
    switch (command)
    {
        case "status":
            if (response.Status is { } status)
            {
                Console.WriteLine($"Daemon:    running");
                Console.WriteLine($"Uptime:    since {status.StartedAt:u}");
                Console.WriteLine($"Streams:   {status.ActiveStreams} active");
                Console.WriteLine($"Devices:   {status.ActiveDevices} detected");
                Console.WriteLine($"Groups:    {status.ConfiguredGroups} configured");
            }
            break;

        case "list-groups" or "groups":
            if (response.Groups is { Count: > 0 } groups)
            {
                foreach (var g in groups)
                {
                    var flags = new List<string>();
                    if (g.IsDefault) flags.Add("DEFAULT");
                    if (g.IsIgnored) flags.Add("IGNORED");
                    if (g.Muted) flags.Add("MUTED");
                    var flagStr = flags.Count > 0 ? $" [{string.Join(", ", flags)}]" : "";

                    var volStr = g.IsIgnored ? "--" : $"{g.Volume}%";
                    Console.WriteLine($"  {g.Name}: {volStr}{flagStr}");

                    if (g.Programs.Count > 0)
                        Console.WriteLine($"    Programs: {string.Join(", ", g.Programs)}");

                    if (g.Devices.Count > 0)
                        Console.WriteLine($"    Devices:  {string.Join(", ", g.Devices)}");
                }
            }
            else
            {
                Console.WriteLine("No groups configured.");
            }
            break;

        case "list-streams" or "streams":
            if (response.Streams is { Count: > 0 } streams)
            {
                Console.WriteLine($"{"ID",-6} {"Binary",-20} {"Vol",-5} {"Muted",-6} {"Group",-15}");
                Console.WriteLine(new string('-', 52));
                foreach (var s in streams)
                {
                    var groupName = s.AssignedGroup.HasValue ? s.AssignedGroup.Value.ToString() : "-";
                    Console.WriteLine(
                        $"{s.Id,-6} {s.BinaryName,-20} {s.Volume + "%",-5} " +
                        $"{(s.Muted ? "yes" : "no"),-6} {groupName,-15}");
                }
            }
            else
            {
                Console.WriteLine("No active audio streams.");
            }
            break;

        case "list-devices" or "devices":
            if (response.Devices is { Count: > 0 } devices)
            {
                Console.WriteLine($"{"Type",-7} {"Name",-45} {"Vol",-5} {"Group",-15}");
                Console.WriteLine(new string('-', 72));
                foreach (var d in devices)
                {
                    var name = d.Description ?? d.Name;
                    if (name.Length > 44) name = name[..41] + "...";
                    var devGroup = d.AssignedGroup.HasValue ? d.AssignedGroup.Value.ToString() : "-";
                    Console.WriteLine(
                        $"{d.Type,-7} {name,-45} {d.Volume + "%",-5} {devGroup,-15}");
                }

                Console.WriteLine();
                Console.WriteLine("(Use the full device Name from config for add-device commands)");
            }
            else
            {
                Console.WriteLine("No audio devices detected.");
            }
            break;

        default:
            Console.WriteLine("OK");
            break;
    }
}

static async Task<IpcResponse> HandleSetVolume(string[] args, IpcDuplexClient client)
{
    if (args.Length < 3)
        throw new ArgumentException("Usage: volmon set-volume <group> <0-100>");

    if (!int.TryParse(args[2], out var volume) || volume < 0 || volume > 100)
        throw new ArgumentException("Volume must be a number between 0 and 100");

    return await client.SendAsync(new IpcRequest
    {
        Command = "set-group-volume",
        GroupName = args[1],
        Volume = volume
    });
}

static async Task<IpcResponse> HandleMute(string[] args, IpcDuplexClient client, bool mute)
{
    if (args.Length < 2)
        throw new ArgumentException($"Usage: volmon {(mute ? "mute" : "unmute")} <group>");

    return await client.SendAsync(new IpcRequest
    {
        Command = mute ? "mute-group" : "unmute-group",
        GroupName = args[1]
    });
}

static async Task<IpcResponse> HandleAddGroup(string[] args, IpcDuplexClient client)
{
    if (args.Length < 2)
        throw new ArgumentException("Usage: volmon add-group <name> [volume] [--default]");

    var isDefault = args.Any(a => a == "--default");
    var volume = 100;

    // Look for a numeric arg for volume
    for (var i = 2; i < args.Length; i++)
    {
        if (int.TryParse(args[i], out var v))
        {
            volume = Math.Clamp(v, 0, 100);
            break;
        }
    }

    return await client.SendAsync(new IpcRequest
    {
        Command = "add-group",
        Group = new AudioGroup { Name = args[1], Volume = volume, IsDefault = isDefault }
    });
}

static async Task<IpcResponse> HandleRemoveGroup(string[] args, IpcDuplexClient client)
{
    if (args.Length < 2)
        throw new ArgumentException("Usage: volmon remove-group <name>");

    return await client.SendAsync(new IpcRequest
    {
        Command = "remove-group",
        GroupName = args[1]
    });
}

static async Task<IpcResponse> HandleAddProgram(string[] args, IpcDuplexClient client)
{
    if (args.Length < 3)
        throw new ArgumentException("Usage: volmon add-program <group> <binary-name>");

    return await client.SendAsync(new IpcRequest
    {
        Command = "add-program",
        GroupName = args[1],
        ProgramName = args[2]
    });
}

static async Task<IpcResponse> HandleRemoveProgram(string[] args, IpcDuplexClient client)
{
    if (args.Length < 3)
        throw new ArgumentException("Usage: volmon remove-program <group> <binary-name>");

    return await client.SendAsync(new IpcRequest
    {
        Command = "remove-program",
        GroupName = args[1],
        ProgramName = args[2]
    });
}

static async Task<IpcResponse> HandleAddDevice(string[] args, IpcDuplexClient client)
{
    if (args.Length < 3)
        throw new ArgumentException("Usage: volmon add-device <group> <device-name>");

    return await client.SendAsync(new IpcRequest
    {
        Command = "add-device",
        GroupName = args[1],
        DeviceName = args[2]
    });
}

static async Task<IpcResponse> HandleRemoveDevice(string[] args, IpcDuplexClient client)
{
    if (args.Length < 3)
        throw new ArgumentException("Usage: volmon remove-device <group> <device-name>");

    return await client.SendAsync(new IpcRequest
    {
        Command = "remove-device",
        GroupName = args[1],
        DeviceName = args[2]
    });
}

static async Task<IpcResponse> HandleSetDefault(string[] args, IpcDuplexClient client)
{
    if (args.Length < 2)
        throw new ArgumentException("Usage: volmon set-default <group-name>");

    return await client.SendAsync(new IpcRequest
    {
        Command = "set-default-group",
        GroupName = args[1]
    });
}

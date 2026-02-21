using System.Text.Json;
using System.Text.Json.Serialization;
using VolMon.Core.Audio;

namespace VolMon.Core.Ipc;

/// <summary>
/// Named pipe name used for IPC between daemon and clients.
/// </summary>
public static class IpcConstants
{
    public const string PipeName = "volmon-daemon";
}

/// <summary>
/// An IPC request sent from CLI/GUI to the daemon.
/// </summary>
public sealed class IpcRequest
{
    public required string Command { get; init; }

    /// <summary>Group GUID (preferred — used by GUI). Takes priority over GroupName.</summary>
    public Guid? GroupId { get; init; }

    /// <summary>Group name (used by CLI for human-friendly lookups). Fallback when GroupId is null.</summary>
    public string? GroupName { get; init; }

    public int? Volume { get; init; }

    /// <summary>Program binary name (for add-program / remove-program).</summary>
    public string? ProgramName { get; init; }

    /// <summary>Device name (for add-device / remove-device).</summary>
    public string? DeviceName { get; init; }

    /// <summary>Full group object (for add-group).</summary>
    public AudioGroup? Group { get; init; }

    /// <summary>Direction for move-group: "up" or "down" (left/right in the UI).</summary>
    public string? Direction { get; init; }

    /// <summary>Color hex string for set-group-color (e.g. "#FF9500").</summary>
    public string? Color { get; init; }

    /// <summary>New name for rename-group.</summary>
    public string? NewName { get; init; }

    /// <summary>Ordered list of group names (for reorder-groups).</summary>
    public List<string>? GroupOrder { get; init; }
}

/// <summary>
/// An IPC response sent from the daemon to CLI/GUI.
/// </summary>
public sealed class IpcResponse
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public List<AudioGroup>? Groups { get; init; }
    public List<AudioStreamInfo>? Streams { get; init; }
    public List<AudioDeviceInfo>? Devices { get; init; }
    public DaemonStatus? Status { get; init; }
}

/// <summary>
/// Simplified stream info for IPC.
/// </summary>
public sealed class AudioStreamInfo
{
    public required string Id { get; init; }
    public required string BinaryName { get; init; }
    public string? ApplicationClass { get; init; }
    public int Volume { get; init; }
    public bool Muted { get; init; }
    public Guid? AssignedGroup { get; init; }
}

/// <summary>
/// Simplified device info for IPC.
/// </summary>
public sealed class AudioDeviceInfo
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string Type { get; init; }
    public int Volume { get; init; }
    public bool Muted { get; init; }
    public Guid? AssignedGroup { get; init; }
}

/// <summary>
/// Daemon health/status information.
/// </summary>
public sealed class DaemonStatus
{
    public bool Running { get; init; }
    public int ActiveStreams { get; init; }
    public int ActiveDevices { get; init; }
    public int ConfiguredGroups { get; init; }
    public DateTime StartedAt { get; init; }
}

/// <summary>
/// Serialization helpers for IPC messages.
/// </summary>
public static class IpcSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options);
}

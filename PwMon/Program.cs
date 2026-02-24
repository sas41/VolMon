using System.Diagnostics;
using System.Text.Json.Nodes;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
Console.Clear();
Console.CursorVisible = false;
while (!cts.Token.IsCancellationRequested)
{
    Console.SetCursorPosition(0,0);
    await RenderSinksAndStreamsAsync(Console.Out);
    Console.Out.Flush();
    try
    {
        await Task.Delay(500, cts.Token);
    }
    catch (TaskCanceledException)
    {
        break;
    }
}

static async Task RenderSinksAndStreamsAsync(TextWriter output)
{
    var (stdout, _) = await RunPwDumpAsync();
    if (string.IsNullOrWhiteSpace(stdout))
    {
        Console.Clear();
        output.WriteLine("Error: pw-dump returned no output".PadRight(Console.WindowWidth));
        return;
    }

    var nodes = ParsePwDump(stdout);
    var allNodes = nodes.Select(n => new PwNode(n)).ToList();
    var sinks = allNodes
        .Where(n => n.Type?.Contains("Node") == true
            && n.Properties?["media.class"]?.GetValue<string>() == "Audio/Sink")
        .ToList();

    foreach (var sink in sinks)
    {
        var name = sink.Properties?["node.name"]?.GetValue<string>()
            ?? sink.Properties?["node.description"]?.GetValue<string>()
            ?? $"Sink {sink.Id}";
        var volPct = (int)Math.Round(sink.Volume * 100);

        output.WriteLine($"●({volPct}%) {name}".PadRight(Console.WindowWidth));

        var linkedStreams = GetStreamsLinkedToSink(allNodes, sink.Id);
        if (linkedStreams.Count == 0)
        {
            output.WriteLine("".PadRight(Console.WindowWidth));
        }
        else
        {
            foreach (var stream in linkedStreams)
            {
                var streamName = stream.Properties?["node.name"]?.GetValue<string>()
                    ?? stream.Properties?["application.name"]?.GetValue<string>()
                    ?? $"Stream {stream.Id}";
                var streamVol = (int)Math.Round(stream.Volume * 100);
                output.WriteLine($"  ├─ ({streamVol}%) {streamName}".PadRight(Console.WindowWidth));
            }
        }
        output.WriteLine("".PadRight(Console.WindowWidth));
    }
    output.WriteLine("".PadRight(Console.WindowWidth));
    output.WriteLine("".PadRight(Console.WindowWidth));
    output.WriteLine("".PadRight(Console.WindowWidth));
}

static async Task<(string stdout, string stderr)> RunPwDumpAsync()
{
    var psi = new ProcessStartInfo
    {
        FileName = "pw-dump",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    using var proc = Process.Start(psi);
    if (proc == null) return ("", "Failed to start pw-dump");
    var stdout = await proc.StandardOutput.ReadToEndAsync();
    var stderr = await proc.StandardError.ReadToEndAsync();
    await proc.WaitForExitAsync();
    return (stdout, stderr);
}

static JsonNode[] ParsePwDump(string json)
{
    try
    {
        var array = JsonNode.Parse(json);
        return array?.AsArray().Select(n => n!).ToArray() ?? [];
    }
    catch
    {
        return [];
    }
}

static List<PwNode> GetStreamsLinkedToSink(List<PwNode> allNodes, uint sinkId)
{
    var links = allNodes
        .Where(n => n.Type?.Contains("Link") == true)
        .ToList();

    var streamIds = new HashSet<uint>();
    foreach (var link in links)
    {
        var inputNodeId = link.Properties?["link.input.node"]?.GetValue<uint>();
        var outputNodeId = link.Properties?["link.output.node"]?.GetValue<uint>();
        if (inputNodeId == sinkId && outputNodeId.HasValue)
        {
            streamIds.Add(outputNodeId.Value);
        }
    }

    return allNodes
        .Where(n => streamIds.Contains(n.Id))
        .ToList();
}

record PwNode
{
    public uint Id { get; init; }
    public string? Type { get; init; }
    public JsonObject? Properties { get; init; }
    public JsonArray? Params { get; init; }
    public double Volume { get; init; }

    public PwNode(JsonNode? node)
    {
        if (node == null) return;
        Id = node["id"]?.GetValue<uint>() ?? 0;
        Type = node["type"]?.GetValue<string>();
        var info = node["info"]?["props"];
        Properties = info as JsonObject;
        Params = node["info"]?["params"]?["Props"] as JsonArray;
        if (Params?.Count > 0 && Params[0] is JsonObject props)
        {
            // channelVolumes holds the actual per-channel volume levels.
            // The scalar "volume" is a separate master multiplier that's
            // typically left at 1.0 and does NOT reflect the user-visible volume.
            //
            // The raw float values use PulseAudio's cubic scale:
            //   raw = (fraction)^3   →   fraction = cbrt(raw)
            // so we apply cbrt() to get the perceptual 0.0–1.0 value that
            // matches what pavucontrol / wpctl display.
            if (props["channelVolumes"] is JsonArray channelVols && channelVols.Count > 0)
            {
                Volume = channelVols.Average(v => Math.Cbrt(v?.GetValue<double>() ?? 0));
            }
            else
            {
                Volume = Math.Cbrt(props["volume"]?.GetValue<double>() ?? 0);
            }
        }
    }
}

using System.IO.Pipes;

namespace VolMon.Core.Ipc;

/// <summary>
/// Named pipe client used by CLI and GUI to communicate with the daemon.
/// </summary>
public sealed class IpcClient : IDisposable
{
    private readonly string _pipeName;

    public IpcClient(string pipeName = IpcConstants.PipeName)
    {
        _pipeName = pipeName;
    }

    /// <summary>
    /// Sends a request to the daemon and returns the response.
    /// Opens a new connection for each request (short-lived).
    /// </summary>
    public async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken ct = default)
    {
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        await pipe.ConnectAsync(TimeSpan.FromSeconds(5), ct);

        // leaveOpen: true — let the pipe own its own lifecycle, not the reader/writer
        var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        var reader = new StreamReader(pipe, leaveOpen: true);

        var requestJson = IpcSerializer.Serialize(request);
        await writer.WriteLineAsync(requestJson.AsMemory(), ct);

        var responseJson = await reader.ReadLineAsync(ct);
        if (responseJson is null)
            return new IpcResponse { Success = false, Error = "No response from daemon" };

        return IpcSerializer.Deserialize<IpcResponse>(responseJson)
            ?? new IpcResponse { Success = false, Error = "Failed to parse daemon response" };
    }

    /// <summary>
    /// Checks whether the daemon is running by attempting a connection.
    /// </summary>
    public async Task<bool> IsDaemonRunningAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await SendAsync(new IpcRequest { Command = "status" }, ct);
            return response.Success;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        // No persistent resources; connections are per-request
    }
}

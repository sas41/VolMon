using System.IO.Pipes;

namespace VolMon.Core.Ipc;

/// <summary>
/// Named pipe server hosted by the daemon. Accepts one client at a time,
/// reads a JSON request, invokes a handler, and writes the JSON response.
/// </summary>
public sealed class IpcServer : IDisposable
{
    private readonly string _pipeName;
    private readonly Func<IpcRequest, CancellationToken, Task<IpcResponse>> _handler;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public IpcServer(
        Func<IpcRequest, CancellationToken, Task<IpcResponse>> handler,
        string pipeName = IpcConstants.PipeName)
    {
        _handler = handler;
        _pipeName = pipeName;
    }

    /// <summary>
    /// Starts listening for IPC connections in the background.
    /// </summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listenTask = ListenLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Stops the IPC server.
    /// </summary>
    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_listenTask is not null)
        {
            try { await _listenTask; }
            catch (OperationCanceledException) { }
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1, // max connections
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(ct);

                try
                {
                    // leaveOpen: true — the pipe is owned by the outer using statement
                    var reader = new StreamReader(pipe, leaveOpen: true);
                    var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

                    var requestJson = await reader.ReadLineAsync(ct);
                    if (requestJson is null) continue;

                    var request = IpcSerializer.Deserialize<IpcRequest>(requestJson);
                    if (request is null)
                    {
                        var errorResponse = new IpcResponse { Success = false, Error = "Invalid request" };
                        await writer.WriteLineAsync(IpcSerializer.Serialize(errorResponse).AsMemory(), ct);
                        continue;
                    }

                    var response = await _handler(request, ct);
                    await writer.WriteLineAsync(IpcSerializer.Serialize(response).AsMemory(), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Client disconnected or handler error; continue accepting
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Transient pipe error; brief delay then retry
                await Task.Delay(100, ct);
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}

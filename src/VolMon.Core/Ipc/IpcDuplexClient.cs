using System.Collections.Concurrent;
using System.IO.Pipes;

namespace VolMon.Core.Ipc;

/// <summary>
/// Duplex named pipe client. Maintains a persistent connection to the daemon
/// and supports both request/response and receiving push events.
///
/// <b>GUI usage:</b> Call <see cref="ConnectAsync"/>, subscribe to
/// <see cref="EventReceived"/>, and send commands via <see cref="SendAsync"/>.
/// Events arrive on a background thread — marshal to the UI thread as needed.
///
/// <b>CLI usage:</b> Call <see cref="ConnectAsync"/>, call <see cref="SendAsync"/>
/// once, then dispose. Events that arrive before the response are ignored.
///
/// If the connection drops, <see cref="Disconnected"/> fires. The GUI should
/// reconnect.
/// </summary>
public sealed class IpcDuplexClient : IAsyncDisposable
{
    private readonly string _pipeName;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private CancellationTokenSource? _readCts;
    private Task? _readTask;

    /// <summary>
    /// Pending request completions keyed by correlation ID.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<IpcResponse>> _pending = new();

    /// <summary>Raised when a push event arrives from the daemon.</summary>
    public event EventHandler<IpcEvent>? EventReceived;

    /// <summary>Raised when the connection drops unexpectedly.</summary>
    public event EventHandler? Disconnected;

    /// <summary>Whether the client is currently connected.</summary>
    public bool IsConnected => _pipe?.IsConnected ?? false;

    public IpcDuplexClient(string pipeName = IpcConstants.PipeName)
    {
        _pipeName = pipeName;
    }

    /// <summary>
    /// Connects to the daemon and starts the background read loop.
    /// </summary>
    public async Task ConnectAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var connectTimeout = timeout ?? TimeSpan.FromSeconds(5);

        _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await _pipe.ConnectAsync(connectTimeout, ct);

        _reader = new StreamReader(_pipe, leaveOpen: true);
        _writer = new StreamWriter(_pipe, leaveOpen: true) { AutoFlush = true };

        _readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _readTask = ReadLoopAsync(_readCts.Token);
    }

    /// <summary>
    /// Sends a command to the daemon and waits for the correlated response.
    /// Events that arrive before the response are dispatched normally.
    /// </summary>
    public async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken ct = default)
    {
        if (_writer is null)
            throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

        var message = IpcMessage.CreateRequest(request);
        var tcs = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[message.Id] = tcs;

        try
        {
            var json = IpcSerializer.Serialize(message);

            await _writeLock.WaitAsync(ct);
            try
            {
                await _writer.WriteLineAsync(json.AsMemory(), ct);
            }
            finally
            {
                _writeLock.Release();
            }

            // Wait for the correlated response with a timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            try
            {
                return await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("Daemon did not respond within 10 seconds.");
            }
        }
        finally
        {
            _pending.TryRemove(message.Id, out _);
        }
    }

    /// <summary>
    /// Sends a command to the daemon without waiting for a response.
    /// Useful for fire-and-forget volume/mute updates where the daemon will
    /// push a state-changed event instead.
    /// </summary>
    public async Task SendFireAndForgetAsync(IpcRequest request, CancellationToken ct = default)
    {
        if (_writer is null)
            throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

        var message = IpcMessage.CreateRequest(request);
        var json = IpcSerializer.Serialize(message);

        await _writeLock.WaitAsync(ct);
        try
        {
            await _writer.WriteLineAsync(json.AsMemory(), ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ── Background read loop ─────────────────────────────────────────

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await _reader!.ReadLineAsync(ct);
                if (line is null) break; // pipe closed

                IpcMessage? message;
                try
                {
                    message = IpcSerializer.Deserialize<IpcMessage>(line);
                }
                catch
                {
                    continue; // skip malformed messages
                }

                if (message is null) continue;

                switch (message.Type)
                {
                    case IpcMessageType.Response when message.Response is not null:
                        if (_pending.TryGetValue(message.Id, out var tcs))
                            tcs.TrySetResult(message.Response);
                        break;

                    case IpcMessageType.Event when message.Event is not null:
                        try { EventReceived?.Invoke(this, message.Event); }
                        catch { /* don't let subscriber errors kill the read loop */ }
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { /* pipe error */ }
        finally
        {
            // Fault all pending requests
            foreach (var kvp in _pending)
            {
                kvp.Value.TrySetException(
                    new IOException("Connection to daemon was lost."));
            }
            _pending.Clear();

            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    // ── Convenience ──────────────────────────────────────────────────

    /// <summary>
    /// Checks whether the daemon is running by connecting and sending a status command.
    /// Disposes the connection afterward.
    /// </summary>
    public static async Task<bool> IsDaemonRunningAsync(CancellationToken ct = default)
    {
        try
        {
            var client = new IpcDuplexClient();
            await client.ConnectAsync(TimeSpan.FromSeconds(3), ct);
            try
            {
                var resp = await client.SendAsync(new IpcRequest { Command = "status" }, ct);
                return resp.Success;
            }
            finally
            {
                await client.DisposeAsync();
            }
        }
        catch
        {
            return false;
        }
    }

    // ── Cleanup ──────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        _readCts?.Cancel();

        if (_readTask is not null)
        {
            try { await _readTask; }
            catch { /* expected */ }
        }

        _readCts?.Dispose();
        _writeLock.Dispose();
        _reader?.Dispose();
        _writer?.Dispose();
        _pipe?.Dispose();
    }
}

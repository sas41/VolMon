using System.Collections.Concurrent;
using System.IO.Pipes;

namespace VolMon.Core.Ipc;

/// <summary>
/// Duplex named pipe server that maintains persistent connections with clients.
///
/// Each connected client has a dedicated read loop. Commands are dispatched to
/// a handler and the response is written back with a matching correlation ID.
///
/// The server can broadcast <see cref="IpcEvent"/> messages to all connected
/// clients at any time (e.g. when state changes).
///
/// Handler execution is serialized via <see cref="SemaphoreSlim"/> so the
/// (non-thread-safe) daemon handler is never called concurrently.
/// </summary>
public sealed class IpcDuplexServer : IDisposable
{
    private readonly string _pipeName;
    private readonly Func<IpcRequest, CancellationToken, Task<IpcResponse>> _handler;
    private readonly SemaphoreSlim _handlerLock = new(1, 1);
    private readonly ConcurrentDictionary<string, ClientConnection> _clients = new();
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    public IpcDuplexServer(
        Func<IpcRequest, CancellationToken, Task<IpcResponse>> handler,
        string pipeName = IpcConstants.PipeName)
    {
        _handler = handler;
        _pipeName = pipeName;
    }

    /// <summary>
    /// Starts accepting connections in the background.
    /// </summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        _acceptTask = AcceptLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Stops the server and disconnects all clients.
    /// </summary>
    public async Task StopAsync()
    {
        _cts?.Cancel();

        // Disconnect all clients
        foreach (var kvp in _clients)
        {
            kvp.Value.Dispose();
            _clients.TryRemove(kvp.Key, out _);
        }

        if (_acceptTask is not null)
        {
            try { await _acceptTask; }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// Broadcasts an event to all connected clients. Clients that fail to
    /// receive the message are silently disconnected.
    /// Serializes once to UTF-8 bytes to avoid an intermediate string allocation.
    /// </summary>
    public async Task BroadcastAsync(IpcEvent evt)
    {
        var message = IpcMessage.CreateEvent(evt);
        var utf8 = IpcSerializer.SerializeToUtf8Bytes(message);

        var disconnected = new List<string>();

        foreach (var kvp in _clients)
        {
            try
            {
                await kvp.Value.WriteUtf8LineAsync(utf8);
            }
            catch
            {
                disconnected.Add(kvp.Key);
            }
        }

        foreach (var id in disconnected)
        {
            if (_clients.TryRemove(id, out var conn))
                conn.Dispose();
        }
    }

    /// <summary>Number of currently connected clients.</summary>
    public int ClientCount => _clients.Count;

    // ── Accept loop ──────────────────────────────────────────────────

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(ct);

                var conn = new ClientConnection(pipe);
                var id = conn.Id;
                _clients[id] = conn;

                // Start the read loop for this client (fire-and-forget)
                _ = RunClientAsync(conn, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Transient pipe error; brief delay then retry
                try { await Task.Delay(100, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    // ── Per-client read loop ─────────────────────────────────────────

    private async Task RunClientAsync(ClientConnection conn, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && conn.IsConnected)
            {
                var line = await conn.ReadLineAsync(ct);
                if (line is null) break; // client disconnected

                IpcMessage? message;
                try
                {
                    message = IpcSerializer.Deserialize<IpcMessage>(line);
                }
                catch
                {
                    // Malformed message; send error and continue
                    var errResp = IpcMessage.CreateResponse(Guid.Empty,
                        new IpcResponse { Success = false, Error = "Malformed message" });
                    await conn.WriteLineAsync(IpcSerializer.Serialize(errResp));
                    continue;
                }

                if (message is null || message.Type != IpcMessageType.Request || message.Request is null)
                {
                    var errResp = IpcMessage.CreateResponse(message?.Id ?? Guid.Empty,
                        new IpcResponse { Success = false, Error = "Expected a request message" });
                    await conn.WriteLineAsync(IpcSerializer.Serialize(errResp));
                    continue;
                }

                // Serialize handler execution — the daemon handler is not thread-safe.
                await _handlerLock.WaitAsync(ct);
                IpcResponse response;
                try
                {
                    response = await _handler(message.Request, ct);
                }
                finally
                {
                    _handlerLock.Release();
                }

                var respMsg = IpcMessage.CreateResponse(message.Id, response);
                await conn.WriteLineAsync(IpcSerializer.Serialize(respMsg));
            }
        }
        catch (OperationCanceledException) { }
        catch { /* client error / disconnect */ }
        finally
        {
            if (_clients.TryRemove(conn.Id, out _))
                conn.Dispose();
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();

        foreach (var kvp in _clients)
        {
            kvp.Value.Dispose();
            _clients.TryRemove(kvp.Key, out _);
        }

        _cts?.Dispose();
        _handlerLock.Dispose();
    }

    // ── ClientConnection ─────────────────────────────────────────────

    /// <summary>
    /// Wraps a single persistent pipe connection with thread-safe write access.
    /// </summary>
    private sealed class ClientConnection : IDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public string Id { get; } = Guid.NewGuid().ToString();
        public bool IsConnected => _pipe.IsConnected;

        public ClientConnection(NamedPipeServerStream pipe)
        {
            _pipe = pipe;
            _reader = new StreamReader(pipe, leaveOpen: true);
            _writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        }

        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            try
            {
                return await _reader.ReadLineAsync(ct);
            }
            catch when (!_pipe.IsConnected)
            {
                return null;
            }
        }

        /// <summary>
        /// Writes a line to the pipe. Thread-safe — serialized via semaphore
        /// so broadcasts and responses don't interleave.
        /// </summary>
        public async Task WriteLineAsync(string line)
        {
            await _writeLock.WaitAsync();
            try
            {
                await _writer.WriteLineAsync(line.AsMemory());
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Writes pre-serialized UTF-8 bytes as a line to the pipe, avoiding
        /// the intermediate string allocation on the broadcast hot path.
        /// </summary>
        public async Task WriteUtf8LineAsync(byte[] utf8)
        {
            await _writeLock.WaitAsync();
            try
            {
                await _pipe.WriteAsync(utf8);
                // WriteLineAsync appends \n via StreamWriter; replicate that here
                _pipe.WriteByte((byte)'\n');
                await _pipe.FlushAsync();
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose()
        {
            _writeLock.Dispose();
            _reader.Dispose();
            _writer.Dispose();
            _pipe.Dispose();
        }
    }
}

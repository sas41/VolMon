# vsdbg Debugger Fix: Daemon Exits Immediately with Code 0

## The Problem

When launching `VolMon.Daemon` under the VS Code debugger (F5), the process
exited immediately with "exited with code 0" — no errors, no output, no chance
to hit breakpoints. Running the same daemon with `dotnet run` outside the
debugger worked perfectly.

## Root Cause

`pa_threaded_mainloop_start()` calls `pthread_create()` to spawn a native
thread. VS Code's .NET debugger (`vsdbg`) uses `ptrace` to track the process.
When libpulse creates a native thread via the `clone()` syscall, vsdbg loses
track of the process, the process gets killed, and vsdbg misreports the exit as
"exited with code 0."

This is a known class of issues with ptrace-based debuggers and native thread
creation from managed code. Even a `SIGABRT` (from a PA assertion failure) was
reported as exit code 0 by vsdbg — confirming the debugger was fundamentally
losing the process, not reporting a real exit.

## The Fix

### 1. Replace `pa_threaded_mainloop` with `pa_mainloop` + managed Thread

The core fix replaces the native-threaded PulseAudio mainloop with a
non-threaded mainloop (`pa_mainloop`) driven by a managed `System.Threading.Thread`.
Managed threads are properly tracked by vsdbg because the CLR creates them
through APIs the debugger understands.

**Before (broken under debugger):**
```
pa_threaded_mainloop  →  pthread_create()  →  native thread  →  vsdbg loses process
```

**After (works everywhere):**
```
pa_mainloop  →  new Thread(MainloopThreadProc)  →  managed thread  →  vsdbg tracks it
```

The lock/wait/signal pattern that `pa_threaded_mainloop` provides was
reimplemented using `Monitor.Enter` / `Monitor.Wait` / `Monitor.PulseAll`:

| pa_threaded_mainloop API      | Replacement                         |
|-------------------------------|-------------------------------------|
| `pa_threaded_mainloop_lock`   | `Monitor.Enter(_paLock)`            |
| `pa_threaded_mainloop_unlock` | `Monitor.Exit(_paLock)`             |
| `pa_threaded_mainloop_wait`   | `Monitor.Wait(_paLock)`             |
| `pa_threaded_mainloop_signal` | `Monitor.PulseAll(_paLock)`         |
| `pa_threaded_mainloop_start`  | `new Thread(...).Start()`           |
| `pa_threaded_mainloop_stop`   | `_mainloopRunning = false` + `Join` |

**Files changed:**
- `src/VolMon.Core/Audio/Backends/PulseAudioBackend.cs` — full rewrite of
  threading model
- `src/VolMon.Core/Audio/Backends/LibPulse.cs` — added `pa_mainloop_new`,
  `pa_mainloop_free`, `pa_mainloop_get_api`, `pa_mainloop_iterate`,
  `pa_mainloop_wakeup`; removed unused `pa_threaded_mainloop_*` declarations

### 2. Use POSIX `setenv()` instead of `Environment.SetEnvironmentVariable`

.NET's `Environment.SetEnvironmentVariable` only updates an internal .NET
dictionary. Native code (like libpulse) calls `getenv()` which reads the C
runtime's `environ` — it never sees .NET-set variables. The `PULSE_NO_SPAWN=1`
variable (which prevents libpulse from forking a PulseAudio daemon) was not
reaching the native layer.

**Fix:** Added a P/Invoke binding to POSIX `setenv()` in `LibPulse.cs` and
call it before connecting:

```csharp
[DllImport("libc", SetLastError = true)]
public static extern int setenv(string name, string value, int overwrite);

// In EnsureConnected():
setenv("PULSE_NO_SPAWN", "1", 1);
```

### 3. VS Code launch configuration

The `type: "dotnet"` launch configuration does not pass `env` variables to the
target process on Linux. Switched to `type: "coreclr"` which properly passes
environment variables and supports `preLaunchTask`.

**`.vscode/launch.json`:**
```json
{
    "name": "Launch VolMon.Daemon",
    "type": "coreclr",
    "request": "launch",
    "preLaunchTask": "build",
    "program": "${workspaceFolder}/src/VolMon.Daemon/bin/Debug/net10.0/VolMon.Daemon",
    "env": {
        "DOTNET_ENVIRONMENT": "Development",
        "PULSE_NO_SPAWN": "1"
    }
}
```

**`.vscode/tasks.json`** was also created with a `build` task referenced by
`preLaunchTask`.

### 4. Top-level error handling

Added a try/catch in `DaemonService.ExecuteAsync` so that any unhandled
exceptions during startup are logged to stderr instead of being silently
swallowed.

## Threading Model (After Fix)

```
                    ┌─────────────────────────────────┐
                    │  Managed Thread "pa-mainloop"   │
                    │                                 │
                    │  lock(_paLock)                  │
                    │  while(_mainloopRunning)        │
                    │    pa_mainloop_iterate(block=0) │
                    │    Monitor.PulseAll(_paLock)    │
                    │    Monitor.Wait(_paLock, 5ms)   │◄─── yields lock
                    │  end                            │     for external
                    └─────────────────────────────────┘     callers
                                    │
                    PA callbacks fire here (lock held)
                    ────────────────────────────────────
                    OnContextState, OnSubscriptionEvent
                    Sink/Source/SinkInput info callbacks

    ┌───────────────────────────────────────────────────────┐
    │              External threads (IPC handlers)          │
    │                                                       │
    │  PaLock()          → Monitor.Enter(_paLock)           │
    │    pa_context_*()  → issue PA operations              │
    │    PaWait()        → Monitor.Wait(_paLock)            │
    │  PaUnlock()        → Monitor.PulseAll + Monitor.Exit  │
    └───────────────────────────────────────────────────────┘
```

- The mainloop thread holds `_paLock` while inside `pa_mainloop_iterate`.
- After each iteration it pulses waiters and yields the lock for 5ms via
  `Monitor.Wait(_paLock, 5)`.
- External callers acquire `_paLock` during that yield window, issue PA
  operations, and call `PaWait()` to wait for callbacks to complete.
- Subscription event handlers dispatch to the thread pool to avoid re-entrant
  deadlocks when event handlers call back into the backend.

## Debugging Tips

Good breakpoint locations to verify the debugger is working:

| Location | When it fires |
|---|---|
| `PulseAudioBackend.cs:64` (`EnsureConnected`) | Immediately on first API call at startup |
| `PulseAudioBackend.cs:183` (`OnContextState`) | During PA connection handshake |
| `PulseAudioBackend.cs:228` (`GetStreamsAsync`) | When the GUI requests stream data |
| `PulseAudioBackend.cs:529` (`OnSubscriptionEvent`) | When any audio stream/device changes (start/stop audio, change volume externally) |

## Gotchas Discovered

1. **vsdbg misreports exit codes.** Even `SIGABRT` (exit code 134) shows as
   "exited with code 0" when vsdbg loses the process. Don't trust the exit code
   if the daemon dies immediately.

2. **`Environment.SetEnvironmentVariable` is .NET-only on Linux.** Native
   libraries calling `getenv()` will never see it. Use POSIX `setenv()` via
   P/Invoke.

3. **`type: "dotnet"` vs `type: "coreclr"` in launch.json.** The `dotnet` type
   does not pass `env` variables on Linux. Use `coreclr` for native executables
   with environment variable support.

4. **`Monitor.PulseAll` requires the lock.** PA callbacks fire on the mainloop
   thread which holds the lock — but during the initial synchronous connection
   loop (before the background thread starts), callbacks fire without the lock.
   Guard with `Monitor.IsEntered(_paLock)`.

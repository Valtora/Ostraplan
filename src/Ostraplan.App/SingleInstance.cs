using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// One window for files opened from outside. A design double-clicked in Explorer while Ostraplan is already running
/// opens in a tab of that window, which comes forward, rather than in a second copy of the app. A second copy takes
/// no part in the session store (see <c>MainWindow.RestoreSession</c>), so what it opened would not come back at the
/// next launch.
///
/// <para>The first copy to start owns a named mutex and listens on a named pipe. A later launch that was given files
/// finds the mutex taken, writes the paths down the pipe and exits. Several files selected and opened together arrive
/// as several launches at once, and the mutex is what makes exactly one of them the window the rest hand to. A launch
/// given no files starts a window of its own, as it always has, and does not listen.</para>
///
/// <para>The pipe takes connections from this user only. If the hand-over fails for any reason, the launch opens the
/// files itself, so a double-click never comes to nothing.</para>
/// </summary>
internal sealed class SingleInstance(string id)
{
    /// <summary>The app's own, named per user and per Windows session so two people signed in at once each get their
    /// own window. The tests make their own under a name nothing else uses.</summary>
    public static SingleInstance App { get; } = new(AppId());

    /// <summary>How long a launch waits for the running copy's pipe. Long enough for a copy that has only just
    /// started, short enough that a hung one costs a moment rather than the files.</summary>
    internal int ConnectTimeoutMs { get; init; } = 3000;

    private readonly object _gate = new();
    private readonly List<string> _pending = [];

    /// <summary>Held for the life of the process by the copy that listens. Other launches test for its name.</summary>
    private Mutex? _owner;

    /// <summary>Raised on the pipe's thread when another launch hands files over.</summary>
    public event Action? Arrived;

    /// <summary>Whether files are waiting to be opened.</summary>
    public bool HasPending
    {
        get { lock (_gate) return _pending.Count > 0; }
    }

    /// <summary>
    /// Settle which window <paramref name="files"/> open in. False when they were handed to a copy already running,
    /// and this launch should exit. True when it carries on, with the files waiting in <see cref="TakePending"/>.
    /// </summary>
    public bool Start(IReadOnlyList<string> files)
    {
        var mutex = new Mutex(false, $@"Local\{id}", out var first);
        if (first)
        {
            _owner = mutex;
            Listen();
        }
        else
        {
            // Not kept: a handle held here would keep the name alive after the listening copy closes, and the next
            // launch would wait on a pipe that nobody is serving.
            mutex.Dispose();
            if (files.Count > 0 && Forward(files)) return false;
        }
        lock (_gate) _pending.AddRange(files);
        return true;
    }

    /// <summary>The files waiting to be opened, and none waiting after.</summary>
    public IReadOnlyList<string> TakePending()
    {
        lock (_gate)
        {
            var files = _pending.ToList();
            _pending.Clear();
            return files;
        }
    }

    private static string AppId()
    {
        var user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        return $"Ostraplan.{user}.{Process.GetCurrentProcess().SessionId}";
    }

    private bool Forward(IReadOnlyList<string> files)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", id, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(ConnectTimeoutMs);
            // The running window may only come forward if this launch lets it. This launch has the right, having just
            // been started by the user, and the window it hands to does not.
            Win32.AllowSetForegroundWindow(Win32.AsfwAny);
            using var writer = new StreamWriter(pipe);
            foreach (var file in files) writer.WriteLine(file);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void Listen()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                NamedPipeServerStream server;
                try
                {
                    server = new NamedPipeServerStream(id, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.CurrentUserOnly);
                }
                catch (Exception ex)
                {
                    // Later launches then time out and open their files themselves.
                    AuditLog.Add($"Not listening for files opened from Explorer: {ex.Message}");
                    return;
                }

                var files = new List<string>();
                try
                {
                    using (server)
                    {
                        server.WaitForConnection();
                        using var reader = new StreamReader(server);
                        while (reader.ReadLine() is { } line)
                            if (line.Length > 0) files.Add(line);
                    }
                }
                catch (IOException) { continue; }   // a launch that gave up part-way; wait for the next

                if (files.Count == 0) continue;
                lock (_gate) _pending.AddRange(files);
                Arrived?.Invoke();
            }
        })
        {
            IsBackground = true,
            Name = "Files opened from Explorer",
        };
        thread.Start();
    }
}

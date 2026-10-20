using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using EqLink.Core;

namespace EqLink.Server;

[SupportedOSPlatform("linux")]
internal sealed class ContainerSupervisor(ServerFiles files)
{
    private sealed record ControlRequest(string Command, TaskCompletionSource<string> Completion);
    private readonly Channel<ControlRequest> _requests = Channel.CreateUnbounded<ControlRequest>();
    private readonly List<Child> _children = [];
    private readonly CancellationTokenSource _stop = new();
    private ServerSettings _settings = null!;
    private string StatusFile => Path.Combine(ServerRuntime.RunDirectory, "status.json");

    internal async Task RunAsync(string settingsFile)
    {
        files.EnsureDirectories();
        Directory.CreateDirectory(ServerRuntime.RunDirectory);
        File.SetUnixFileMode(ServerRuntime.RunDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var instanceLock = new FileStream(Path.Combine(files.Root, ".instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; _stop.Cancel(); });
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; _stop.Cancel(); });
        File.Delete(StatusFile);
        using (new ServerStateLock(files))
        {
            var requested = Program.ReadSettings(settingsFile);
            if (!File.Exists(files.StateFile))
            {
                if (File.Exists(Path.Combine(files.Pki, "ca.key")))
                    throw new InvalidOperationException("Incomplete initialization found. Restore a consistent /etc/eqlink backup; the existing CA will not be overwritten.");
                Program.Init(files, requested);
            }
            else
            {
                var state = files.ReadState();
                EnrollmentService.RequireV2(state);
                if (ConfigRenderer.Serialize(state.Settings) != ConfigRenderer.Serialize(requested))
                    throw new InvalidOperationException("Bootstrap settings differ from persistent state. Restore the original bootstrap Secret or perform a deliberate state migration.");
                files.Render(state);
                new Pki(files).RefreshCrl();
            }
            _settings = files.ReadState().Settings;
        }
        if (!File.Exists("/dev/net/tun")) throw new FileNotFoundException("/dev/net/tun is required. Check the node TUN driver and Pod device permissions.");
        _children.Add(new Child("openvpn", "openvpn", ["--config", files.OpenVpnFile]));
        if (_settings.EnableUdp) _children.Add(new Child("hysteria", "hysteria", ["server", "-c", files.HysteriaFile]));
        if (_settings.EnableTcp) _children.Add(new Child("xray", "xray", ["run", "-c", files.XrayFile]));
        await using var health = new ControlHost(files);
        await health.StartAsync(_stop.Token);
        var control = ControlLoopAsync(_stop.Token);
        var nextCrl = DateTimeOffset.UtcNow.AddDays(7);
        long authStamp = -1; int? authPid = null;
        Log("supervisor", "Starting one OpenVPN server with independently supervised outer paths.");
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                if (control.IsCompleted) await control;
                while (_requests.Reader.TryRead(out var request))
                {
                    try
                    {
                        var selected = request.Command == "reload" ? _children.Where(c => c.Name != "openvpn") : _children.Where(c => c.Name == "openvpn");
                        foreach (var child in selected)
                        {
                            await child.StopAsync();
                            child.Start();
                            await WaitForListenerAsync(child, _stop.Token);
                        }
                        WriteStatus();
                        request.Completion.TrySetResult("OK");
                    }
                    catch (Exception ex)
                    {
                        Log("supervisor", "Control request failed: " + ex.Message);
                        request.Completion.TrySetResult("ERROR: " + ex.Message);
                    }
                }
                foreach (var child in _children) await child.EnsureRunningAsync();
                var xray = _children.FirstOrDefault(c=>c.Name=="xray");
                var stamp = File.GetLastWriteTimeUtc(files.StateFile).Ticks;
                if (xray is not null && Listening(xray) && (stamp != authStamp || xray.Pid != authPid))
                {
                    try
                    {
                        // Nonblocking lock: enrollment may be applying an API change while holding this same lock.
                        using var reconcileLock = new FileStream(Path.Combine(files.Root,".state.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
                        files.RecoverPending();
                        new EnrollmentService(files).ReconcileUsers(files.ReadState());
                        authStamp = File.GetLastWriteTimeUtc(files.StateFile).Ticks; authPid = xray.Pid;
                    }
                    catch (IOException) { } // Retry after a writer finishes or the API recovers. Existing users stay online.
                }
                await health.EnsureInnerAsync(_stop.Token);
                WriteStatus();
                if (DateTimeOffset.UtcNow >= nextCrl)
                {
                    // CLI commands may be waiting for this supervisor, so never wait for their lock here.
                    try
                    {
                        using var refreshLock = new FileStream(Path.Combine(files.Root, ".state.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                        new Pki(files).RefreshCrl();
                        nextCrl = DateTimeOffset.UtcNow.AddDays(7);
                        Log("supervisor", "CRL refreshed.");
                    }
                    catch (IOException) { nextCrl = DateTimeOffset.UtcNow.AddMinutes(1); }
                }
                await Task.Delay(500, _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            _stop.Cancel();
            foreach (var child in _children) await child.StopAsync();
            try { await control; } catch (OperationCanceledException) { }
            File.Delete(StatusFile);
            File.Delete(ServerRuntime.ControlSocket);
            Log("supervisor", "Stopped.");
        }
    }

    private bool Listening(Child child)
    {
        if (!child.Running) return false;
        var properties = IPGlobalProperties.GetIPGlobalProperties();
        return child.Name == "hysteria"
            ? properties.GetActiveUdpListeners().Any(p => p.Port == _settings.UdpPort)
            : properties.GetActiveTcpListeners().Any(p => p.Port == (child.Name == "openvpn" ? _settings.OpenVpnPort : _settings.TcpPort));
    }

    private async Task WaitForListenerAsync(Child child, CancellationToken stop)
    {
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(100, stop);
            if (!child.Running) throw new IOException(child.Name + " exited while applying configuration.");
            if (Listening(child)) return;
        }
        throw new IOException(child.Name + " did not open its configured listener.");
    }

    private void WriteStatus()
    {
        var vpn = _children.Single(c => c.Name == "openvpn");
        var overlay = _settings.Overlay();
        var hasTunAddress = NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Any(a => a.Address.Equals(IPAddress.Parse(overlay.ServerIp)));
        var healthListening = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Any(p => p.Address.Equals(IPAddress.Loopback) && p.Port == _settings.HealthPort);
        var innerListening = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Any(p => p.Address.Equals(IPAddress.Parse(overlay.ServerIp)) && p.Port == _settings.HealthPort);
        var pathAvailable = _children.Where(c => c.Name != "openvpn").Any(Listening);
        ServerFiles.AtomicWrite(StatusFile, ConfigRenderer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow, pid = Environment.ProcessId,
            ready = Listening(vpn) && healthListening && innerListening && hasTunAddress && pathAvailable,
            virtualIp = overlay.ServerIp,
            services = _children.Select(c => new { name = c.Name, pid = c.Pid, running = c.Running, listening = Listening(c), exitCode = c.LastExitCode })
        }));
    }

    internal static int Probe(string kind)
    {
        using var status = JsonDocument.Parse(File.ReadAllText(Path.Combine(ServerRuntime.RunDirectory, "status.json")));
        if (kind == "status") { Console.WriteLine(status.RootElement.GetRawText()); return 0; }
        if (kind is not ("live" or "ready")) throw new ArgumentException("probe --kind must be live, ready, or status.");
        var timestamp = status.RootElement.GetProperty("timestamp").GetDateTimeOffset();
        var fresh = DateTimeOffset.UtcNow - timestamp < TimeSpan.FromSeconds(30);
        return fresh && (kind == "live" || status.RootElement.GetProperty("ready").GetBoolean()) ? 0 : 1;
    }

    private async Task ControlLoopAsync(CancellationToken stop)
    {
        File.Delete(ServerRuntime.ControlSocket);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(ServerRuntime.ControlSocket));
        File.SetUnixFileMode(ServerRuntime.ControlSocket, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen(8);
        while (!stop.IsCancellationRequested)
        {
            var socket = await listener.AcceptAsync(stop);
            _ = HandleControlAsync(socket, stop);
        }
    }

    private async Task HandleControlAsync(Socket socket, CancellationToken stop)
    {
        using (socket)
        using (var stream = new NetworkStream(socket))
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(22));
            try
            {
                var bytes = new List<byte>();
                var one = new byte[1];
                while (bytes.Count < 64)
                {
                    if (await stream.ReadAsync(one, timeout.Token) == 0) return;
                    if (one[0] == '\n') break;
                    bytes.Add(one[0]);
                }
                var command = Encoding.ASCII.GetString(bytes.ToArray());
                string answer;
                if (command is not ("reload" or "restart-openvpn")) answer = "ERROR: unsupported command";
                else
                {
                    var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    await _requests.Writer.WriteAsync(new ControlRequest(command, completion), timeout.Token);
                    answer = await completion.Task.WaitAsync(timeout.Token);
                }
                await stream.WriteAsync(Encoding.UTF8.GetBytes(answer + "\n"), timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
        }
    }

    internal static void Log(string name, string message) => Console.WriteLine($"{DateTimeOffset.UtcNow:O} [{name}] {message}");

    private sealed class Child(string name, string executable, string[] arguments)
    {
        internal string Name => name;
        private Process? _process;
        private Task? _stdout, _stderr;
        private DateTimeOffset _started, _nextStart;
        private int _failures;
        internal int? LastExitCode { get; private set; }
        internal bool Running => _process is not null && !_process.HasExited;
        internal int? Pid => Running ? _process!.Id : null;

        internal void Start()
        {
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in arguments) info.ArgumentList.Add(arg);
            _process = Process.Start(info) ?? throw new IOException("Could not start " + name);
            _started = DateTimeOffset.UtcNow;
            _stdout = PumpAsync(_process.StandardOutput);
            _stderr = PumpAsync(_process.StandardError);
            Log(name, "Started pid " + _process.Id);
        }

        internal async Task EnsureRunningAsync()
        {
            if (Running) return;
            if (_process is not null)
            {
                LastExitCode = _process.ExitCode;
                Log(name, "Exited with code " + LastExitCode);
                if (_stdout is not null && _stderr is not null) await Task.WhenAll(_stdout, _stderr);
                _process.Dispose();
                _process = null;
                _failures = DateTimeOffset.UtcNow - _started > TimeSpan.FromSeconds(30) ? 1 : Math.Min(_failures + 1, 5);
                _nextStart = DateTimeOffset.UtcNow.AddSeconds(Math.Min(30, 1 << _failures) + Random.Shared.NextDouble());
            }
            if (DateTimeOffset.UtcNow >= _nextStart) Start();
        }

        private async Task PumpAsync(StreamReader reader)
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                // Only event categories cross into supervisor logs. Components may include credentials in raw errors.
                if (line.Contains("AUTH_FAILED", StringComparison.OrdinalIgnoreCase) || line.Contains("authentication failed", StringComparison.OrdinalIgnoreCase)) Log(name, "Authentication rejected.");
                else if (line.Contains("VERIFY ERROR", StringComparison.OrdinalIgnoreCase)) Log(name, "Certificate verification rejected.");
                else if (line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("fatal", StringComparison.OrdinalIgnoreCase)) Log(name, "Component reported an error; raw output withheld.");
            }
        }

        internal async Task StopAsync()
        {
            if (_process is null) return;
            if (!_process.HasExited)
            {
                _ = kill(_process.Id, 15);
                try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)); }
                catch (TimeoutException) { _process.Kill(true); await _process.WaitForExitAsync(); }
            }
            if (_stdout is not null && _stderr is not null) await Task.WhenAll(_stdout, _stderr);
            _process.Dispose();
            _process = null;
            _nextStart = default;
            _failures = 0;
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}

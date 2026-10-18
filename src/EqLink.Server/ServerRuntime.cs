using System.Net.Sockets;
using System.Text;

namespace EqLink.Server;

internal static class ServerRuntime
{
    internal static bool IsContainer => Environment.GetEnvironmentVariable("EQLINK_RUNTIME") == "container";
    internal static string RunDirectory => Environment.GetEnvironmentVariable("EQLINK_RUN_DIR") ?? "/run/eqlink";
    internal static string ControlSocket => Path.Combine(RunDirectory, "control.sock");

    internal static void Request(string command)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        RequestAsync(command, timeout.Token).GetAwaiter().GetResult();
    }

    private static async Task RequestAsync(string command, CancellationToken stop)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(ControlSocket), stop);
            using var stream = new NetworkStream(socket);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(command + "\n"), stop);
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var answer = await reader.ReadLineAsync(stop);
            if (answer != "OK") throw new IOException(answer ?? "Supervisor closed the control connection.");
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            throw new IOException("State was saved, but the container supervisor could not apply it. Run 'EqLink.Server apply' after checking the supervisor logs. " + ex.Message, ex);
        }
    }
}

/// <summary>Serializes CA/database/config mutations made by separate kubectl exec processes.</summary>
internal sealed class ServerStateLock : IDisposable
{
    private readonly FileStream _file;
    internal ServerStateLock(ServerFiles files)
    {
        files.EnsureDirectories();
        var until = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                _file = new FileStream(Path.Combine(files.Root, ".state.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                break;
            }
            catch (IOException) when (DateTimeOffset.UtcNow < until) { Thread.Sleep(100); }
        }
    }
    public void Dispose() => _file.Dispose();
}

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EqLink.Core;

namespace EqLink.Server;

internal sealed class ServerFiles(string root)
{
    internal string Root { get; } = Path.GetFullPath(root);
    internal string Pki => Path.Combine(Root, "pki");
    internal string Ccd => Path.Combine(Root, "ccd");
    internal string StateFile => Path.Combine(Root, "state.json");
    internal string OpenVpnFile => Path.Combine(Root, "openvpn.conf");
    internal string HysteriaFile => Path.Combine(Root, "hysteria.json");
    internal string XrayFile => Path.Combine(Root, "xray.json");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal void EnsureDirectories()
    {
        if (Root.Any(char.IsWhiteSpace)) throw new ArgumentException("Server root path cannot contain whitespace.");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Pki);
        Directory.CreateDirectory(Ccd);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(Pki, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(Ccd, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    internal ServerState ReadState() => JsonSerializer.Deserialize<ServerState>(File.ReadAllText(StateFile), Json)
        ?? throw new InvalidDataException("Server state is empty.");

    internal void WriteState(ServerState state) => AtomicWrite(StateFile, ConfigRenderer.Serialize(state));

    internal static void AtomicWrite(string path, string text)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, true);
    }

    internal void Render(ServerState state)
    {
        state.Settings.Validate();
        var s = state.Settings;
        var ovpn = ConfigRenderer.ServerOpenVpn(s, Root);
        var hy = s.EnableUdp ? ConfigRenderer.ServerHysteria(s, state.Devices) : null;
        var xr = s.EnableTcp ? ConfigRenderer.ServerXray(s, state.Devices) : null;
        if (hy is not null) using (JsonDocument.Parse(hy)) { }
        if (xr is not null) using (JsonDocument.Parse(xr)) { }
        var candidate = Path.Combine(Root, "xray.candidate.json");
        if (xr is not null) AtomicWrite(candidate, xr);
        try
        {
            if (xr is not null && ProcessRunner.Exists("xray"))
                ProcessRunner.Run("xray", "run", "-test", "-c", candidate);
            AtomicWrite(OpenVpnFile, ovpn);
            if (hy is not null) AtomicWrite(HysteriaFile, hy);
            if (xr is not null) AtomicWrite(XrayFile, xr);
        }
        finally { File.Delete(candidate); }
    }

    internal static void RestartOuter(ServerSettings s)
    {
        if (ServerRuntime.IsContainer) { ServerRuntime.Request("reload"); return; }
        if (!OperatingSystem.IsLinux() || !ProcessRunner.Exists("systemctl")) return;
        foreach (var (name, enabled) in new[] { ("eqlink-hysteria", s.EnableUdp), ("eqlink-xray", s.EnableTcp) })
            if (enabled && ProcessRunner.RunResult("systemctl", "is-active", "--quiet", name).ExitCode == 0)
                ProcessRunner.Run("systemctl", "restart", name);
    }

    internal static void CheckPort(int port, ProtocolType protocol)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, protocol == ProtocolType.Tcp ? SocketType.Stream : SocketType.Dgram, protocol);
        socket.Bind(new IPEndPoint(IPAddress.Any, port));
    }
}

internal static class ProcessRunner
{
    internal static bool Exists(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator).Any(dir => File.Exists(Path.Combine(dir, name)));
    }

    internal static string Run(string executable, params string[] args)
    {
        var result = RunResult(executable, args);
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput.Trim() : result.StandardError.Trim();
            throw new InvalidOperationException($"{executable} failed (exit {result.ExitCode}): {detail}");
        }
        return result.StandardOutput;
    }

    internal static (int ExitCode, string StandardOutput, string StandardError) RunResult(string executable, params string[] args)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Cannot start {executable}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000)) { process.Kill(true); throw new TimeoutException($"{executable} timed out."); }
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }
}

internal sealed class Pki(ServerFiles files)
{
    private string At(string name) => Path.Combine(files.Pki, name);
    private string CaConfig => At("ca.cnf");

    internal void Init()
    {
        File.WriteAllText(At("index.txt"), "");
        File.WriteAllText(At("serial"), "1000\n");
        File.WriteAllText(At("crlnumber"), "1000\n");
        Directory.CreateDirectory(At("newcerts"));
        ServerFiles.AtomicWrite(CaConfig, $"""
            [ ca ]
            default_ca = CA_default
            [ CA_default ]
            database = {At("index.txt")}
            serial = {At("serial")}
            crlnumber = {At("crlnumber")}
            new_certs_dir = {At("newcerts")}
            private_key = {At("ca.key")}
            certificate = {At("ca.crt")}
            default_md = sha256
            default_days = 825
            default_crl_days = 30
            policy = policy_any
            unique_subject = no
            [ policy_any ]
            commonName = supplied
            [ server_cert ]
            basicConstraints = critical, CA:false
            keyUsage = critical, digitalSignature, keyEncipherment
            extendedKeyUsage = serverAuth
            subjectAltName = DNS:server
            [ client_cert ]
            basicConstraints = critical, CA:false
            keyUsage = critical, digitalSignature
            extendedKeyUsage = clientAuth
            """ + "\n");
        ProcessRunner.Run("openssl", "req", "-new", "-x509", "-nodes", "-newkey", "rsa:3072", "-days", "3650", "-sha256",
            "-subj", "/CN=EqLink-CA", "-addext", "basicConstraints=critical,CA:TRUE,pathlen:0",
            "-addext", "keyUsage=critical,keyCertSign,cRLSign", "-keyout", At("ca.key"), "-out", At("ca.crt"));
        GenerateCertificate("server", "server_cert", At("server.key"), At("server.crt"));
        RefreshCrl();
        ProcessRunner.Run("openvpn", "--genkey", "tls-crypt-v2-server", At("tls-crypt-v2-server.key"));
        ServerFiles.AtomicWrite(At("management.password"), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) + "\n");
        RestrictFiles();
    }

    internal (string Cert, string Key, string TlsKey) AddDevice(string cn)
    {
        var key = At(cn + ".key");
        var cert = At(cn + ".crt");
        var tls = At(cn + ".tls-crypt-v2.key");
        GenerateCertificate(cn, "client_cert", key, cert);
        ProcessRunner.Run("openvpn", "--tls-crypt-v2", At("tls-crypt-v2-server.key"), "--genkey", "tls-crypt-v2-client", tls);
        RestrictFiles();
        return (cert, key, tls);
    }

    internal void Revoke(string cert)
    {
        ProcessRunner.Run("openssl", "ca", "-config", CaConfig, "-batch", "-revoke", cert);
        RefreshCrl();
    }

    internal void RefreshCrl()
    {
        var candidate = At("crl.pem.tmp");
        try
        {
            ProcessRunner.Run("openssl", "ca", "-config", CaConfig, "-gencrl", "-out", candidate);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(candidate, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(candidate, At("crl.pem"), true);
        }
        finally { File.Delete(candidate); }
    }

    private void GenerateCertificate(string cn, string extension, string key, string cert)
    {
        var csr = At(cn + ".csr");
        ProcessRunner.Run("openssl", "req", "-new", "-nodes", "-newkey", "rsa:3072", "-sha256",
            "-subj", "/CN=" + cn, "-keyout", key, "-out", csr);
        ProcessRunner.Run("openssl", "ca", "-config", CaConfig, "-batch", "-notext", "-extensions", extension, "-in", csr, "-out", cert);
        File.Delete(csr);
    }

    private void RestrictFiles()
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var file in Directory.GetFiles(files.Pki))
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

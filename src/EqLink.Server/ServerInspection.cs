using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using EqLink.Core;

namespace EqLink.Server;

internal static class ServerInspection
{
    internal static object Read(ServerFiles files)
    {
        var state = files.ReadState(); var s = state.Settings;
        var tcp = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        var udp = IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners();
        object[] Listeners(int port, bool datagram = false) => (datagram ? udp : tcp).Where(p => p.Port == port)
            .Select(p => (object)new { address = p.Address.ToString(), port = p.Port, protocol = datagram ? "UDP" : "TCP" }).ToArray();
        bool Loopback(int port) => tcp.Any(p => p.Port == port && p.Address.Equals(IPAddress.Loopback)) && tcp.Where(p => p.Port == port).All(p => IPAddress.IsLoopback(p.Address));
        var directives = File.ReadAllLines(files.OpenVpnFile).Where(l => new[] { "local ","port ","proto ","dev ","topology ","server ","client-to-client","ccd-exclusive","disable-dco","keepalive ","tls-version-min ","crl-verify ","management " }.Any(p => l.StartsWith(p,StringComparison.Ordinal))).ToArray();
        var publicRules = new List<object>();
        int? hyUsers = null, vlessUsers = null; string? hyAuthType = null;
        object? acl = null;
        var hyRestricted = !s.EnableUdp; var xrRestricted = !s.EnableTcp;
        var hyAuthenticationMatches = !s.EnableUdp; var vlessAuthenticationMatches = !s.EnableTcp;
        var activeDevices = state.Devices.Where(d=>d.Active).ToArray();
        if(s.EnableUdp)
        {
            using var hy = JsonDocument.Parse(File.ReadAllText(files.HysteriaFile));
            if(hy.RootElement.TryGetProperty("acl",out var rules) && rules.TryGetProperty("inline",out var inline)) acl = inline.EnumerateArray().Select(v=>v.GetString()).ToArray();
            if(hy.RootElement.TryGetProperty("auth",out var auth))
            {
                if(auth.TryGetProperty("type",out var type)) hyAuthType = type.GetString();
                hyUsers = auth.TryGetProperty("userpass",out var users) ? users.EnumerateObject().Count() : 0;
                hyAuthenticationMatches = activeDevices.Length==0
                    ? hyAuthType=="command" && Text(auth,"command")=="/bin/false"
                    : hyAuthType=="userpass" && hyUsers==activeDevices.Length && activeDevices.All(d=>users.TryGetProperty(d.Id,out var password) && password.GetString()==d.HysteriaPassword);
            }
            var expectedAcl = new[] { $"direct(127.0.0.1, tcp/{s.OpenVpnPort})", $"direct(127.0.0.1, tcp/{s.HealthPort})", "reject(all)" };
            hyRestricted = acl is string?[] entries && entries.SequenceEqual(expectedAcl) &&
                hy.RootElement.TryGetProperty("disableUDP", out var disabled) && disabled.ValueKind == JsonValueKind.True;
        }
        if(s.EnableTcp)
        {
            using var xr = JsonDocument.Parse(File.ReadAllText(files.XrayFile));
            var expected = JsonNode.Parse(ConfigRenderer.ServerXray(s, []))!;
            var actual = JsonNode.Parse(xr.RootElement.GetRawText())!;
            xrRestricted = JsonNode.DeepEquals(expected["routing"], actual["routing"]) && JsonNode.DeepEquals(expected["outbounds"], actual["outbounds"]);
            var expectedClients = JsonNode.Parse(ConfigRenderer.ServerXray(s, activeDevices))!["inbounds"]![0]!["settings"]!["clients"];
            vlessAuthenticationMatches = JsonNode.DeepEquals(expectedClients,actual["inbounds"]![0]!["settings"]!["clients"]);
            vlessUsers = xr.RootElement.GetProperty("inbounds").EnumerateArray().Sum(v=>v.GetProperty("settings").GetProperty("clients").GetArrayLength());
            foreach(var rule in xr.RootElement.GetProperty("routing").GetProperty("rules").EnumerateArray())
                publicRules.Add(new { type = Text(rule,"type"), ip = rule.TryGetProperty("ip",out var ips) ? ips.EnumerateArray().Select(i=>i.GetString()).ToArray() : null,
                    port = Text(rule,"port"), network = Text(rule,"network"), outboundTag = Text(rule,"outboundTag") });
        }
        var overlay = s.Overlay();
        var active = state.Devices.Where(d=>d.Active).ToArray();
        var serverCertificate = Certificate(Path.Combine(files.Pki,"server.crt"));
        var hyCertificate = s.EnableUdp ? Certificate(s.HysteriaCertificate) : null;
        var crl = Crl(Path.Combine(files.Pki,"crl.pem"));
        var virtualAddresses = NetworkInterface.GetAllNetworkInterfaces().SelectMany(n=>n.GetIPProperties().UnicastAddresses)
            .Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork && a.Address.GetAddressBytes().AsSpan(0,3).SequenceEqual(IPAddress.Parse(overlay.ServerIp).GetAddressBytes().AsSpan(0,3)))
            .Select(a=>new {address=a.Address.ToString(),a.PrefixLength}).ToArray();
        return new
        {
            schemaVersion=1, timestamp=DateTimeOffset.UtcNow, version=typeof(ServerInspection).Assembly.GetName().Version?.ToString(),
            components=ComponentDiagnostics.Read(ComponentDiagnostics.OnPath("hysteria"),ComponentDiagnostics.OnPath("xray"),ComponentDiagnostics.OnPath("openvpn")),
            scope="Read-only selected fields; no private material or raw configuration",
            expected = new { s.PublicHost,s.TcpPort,s.UdpPort,s.OpenVpnPort,s.HealthPort,s.ManagementPort,s.OverlayCidr,s.EnableUdp,s.EnableTcp,s.HysteriaServerName,s.RealityTarget,s.RealityServerName,s.XhttpPath,activeDevices=active.Length },
            runtime = new
            {
                architecture=System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                openVpn=Listeners(s.OpenVpnPort),health=Listeners(s.HealthPort),management=Listeners(s.ManagementPort),
                xray=Listeners(s.TcpPort),hysteria=Listeners(s.UdpPort,true),
                virtualAddresses,
                openVpnDirectives=directives, hysteriaAuthType=hyAuthType, hysteriaAuthEntries=hyUsers, vlessAuthEntries=vlessUsers,
                hysteriaAcl=acl,xrayRules=publicRules,
                certificate=serverCertificate,hysteriaCertificate=hyCertificate,
                crl,sessions=Sessions(files,state)
            },
            checks = new
            {
                openVpnLoopbackOnly=Loopback(s.OpenVpnPort),healthLoopbackOnly=Loopback(s.HealthPort),managementLoopbackOnly=Loopback(s.ManagementPort),
                topologySubnet=directives.Contains("topology subnet"),clientInterconnect=directives.Contains("client-to-client"),dcoDisabled=directives.Contains("disable-dco"),
                hysteriaEntryCountMatches=!s.EnableUdp || hyUsers==active.Length,vlessEntryCountMatches=!s.EnableTcp || vlessUsers==active.Length,
                hysteriaAuthenticationMatches=hyAuthenticationMatches,vlessAuthenticationMatches,
                hysteriaTargetsRestricted=hyRestricted,xrayTargetsRestricted=xrRestricted,
                virtualServerAddressPresent=virtualAddresses.Any(a=>a.address==overlay.ServerIp && a.PrefixLength==24),
                serverCertificateValid=serverCertificate.ValidNow,hysteriaCertificateValid=!s.EnableUdp || hyCertificate!.ValidNow,
                crlValid=crl.ValidNow,outerListenerPresent=(s.EnableTcp && tcp.Any(p=>p.Port==s.TcpPort)) || (s.EnableUdp && udp.Any(p=>p.Port==s.UdpPort))
            },
            devices=state.Devices.Select(d=>new {d.Name,d.VirtualIp,d.Active,certificate=Certificate(d.CertificatePath)}).ToArray()
        };
    }
    private static string? Text(JsonElement value,string key) => value.TryGetProperty(key,out var field) && field.ValueKind==JsonValueKind.String ? field.GetString() : null;
    private sealed record CertificateInfo(string? Subject=null,string? Issuer=null,DateTime? NotBefore=null,DateTime? NotAfter=null,bool ValidNow=false,string? Unavailable=null);
    private static CertificateInfo Certificate(string path)
    {
        try { using var cert=X509Certificate2.CreateFromPem(File.ReadAllText(path)); return new(cert.Subject,cert.Issuer,cert.NotBefore.ToUniversalTime(),cert.NotAfter.ToUniversalTime(),DateTime.UtcNow>=cert.NotBefore.ToUniversalTime()&&DateTime.UtcNow<cert.NotAfter.ToUniversalTime()); }
        catch(Exception ex) when(ex is IOException or System.Security.Cryptography.CryptographicException) {return new(Unavailable:ex.GetType().Name);}
    }
    private sealed record CrlInfo(bool Available=false,string? LastUpdate=null,string? NextUpdate=null,bool ValidNow=false,string? Unavailable=null);
    private static CrlInfo Crl(string path)
    {
        try
        {
            var result=ProcessRunner.RunResult("openssl","crl","-in",path,"-noout","-lastupdate","-nextupdate");
            var fields=result.StandardOutput.Split('\n',StringSplitOptions.RemoveEmptyEntries).Where(l=>l.StartsWith("lastUpdate=",StringComparison.Ordinal)||l.StartsWith("nextUpdate=",StringComparison.Ordinal)).Select(l=>l.Split('=',2)).ToDictionary(p=>p[0],p=>p[1].Trim());
            var last = fields.GetValueOrDefault("lastUpdate"); var next = fields.GetValueOrDefault("nextUpdate");
            var valid = result.ExitCode==0 && ParseCrlDate(last,out var from) && ParseCrlDate(next,out var until) && DateTimeOffset.UtcNow>=from && DateTimeOffset.UtcNow<until;
            return new(result.ExitCode==0,last,next,valid);
        }
        catch(Exception ex) when(ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) {return new(Unavailable:ex.GetType().Name);}
    }
    private static bool ParseCrlDate(string? text,out DateTimeOffset value) => DateTimeOffset.TryParseExact(
        text is null ? null : System.Text.RegularExpressions.Regex.Replace(text.Trim(),@"\s+"," "),
        "MMM d HH:mm:ss yyyy 'GMT'",CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out value);
    private static object Sessions(ServerFiles files, EqLink.Core.ServerState state)
    {
        try
        {
            using var client=new TcpClient();
            client.ConnectAsync(IPAddress.Loopback,state.Settings.ManagementPort).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            client.ReceiveTimeout=2000;client.SendTimeout=2000;
            using var reader=new StreamReader(client.GetStream());using var writer=new StreamWriter(client.GetStream()){AutoFlush=true,NewLine="\n"};
            var prompt=new char[15];reader.ReadBlock(prompt,0,prompt.Length);
            writer.WriteLine(File.ReadAllText(Path.Combine(files.Pki,"management.password")).Trim());
            writer.WriteLine("status 3");
            var sessions=new List<object>();
            for(var i=0;i<2000;i++)
            {
                var line=reader.ReadLine();if(line is null || line=="END")break;
                if(!line.StartsWith("CLIENT_LIST\t",StringComparison.Ordinal))continue;
                var f=line.Split('\t');if(f.Length<7)continue;
                var device=state.Devices.FirstOrDefault(d=>"device-"+d.Id==f[1]);
                sessions.Add(new {name=device?.Name??"unregistered",virtualIp=f[3],bytesReceived=long.TryParse(f[5],NumberStyles.Integer,CultureInfo.InvariantCulture,out var rx)?rx:0,bytesSent=long.TryParse(f[6],NumberStyles.Integer,CultureInfo.InvariantCulture,out var tx)?tx:0});
            }
            return new {available=true,clients=sessions};
        }
        catch(Exception ex) when(ex is IOException or SocketException or TimeoutException or AggregateException) {return new {available=false,reason=ex.GetType().Name};}
    }
}

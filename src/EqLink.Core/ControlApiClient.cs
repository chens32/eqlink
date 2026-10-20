using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace EqLink.Core;

public sealed class ControlApiException(string code, int status) : IOException(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public bool AuthenticationFailure => Status is 401 or 403;
}

/// <summary>All control traffic uses an explicit loopback proxy or the verified overlay interface. Never uses system proxies.</summary>
public sealed class ControlApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly X509Certificate2 _root;
    private readonly X509Certificate2? _identity;

    public ControlApiClient(DeviceProfile profile, int? proxyPort = null, int? overlayInterface = null, string? enrollmentToken = null, TimeSpan? connectTimeout = null)
    {
        _root = X509Certificate2.CreateFromPem(profile.CaPem);
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, MaxResponseHeadersLength = 16,
            PooledConnectionLifetime = TimeSpan.Zero, ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(3),
            SslOptions = new SslClientAuthenticationOptions
            {
                CertificateChainPolicy = new X509ChainPolicy
                {
                    TrustMode = X509ChainTrustMode.CustomRootTrust,
                    RevocationMode = X509RevocationMode.NoCheck,
                    CustomTrustStore = { _root }
                }
            }
        };
        if (!string.IsNullOrEmpty(profile.CertificatePem))
        {
            _identity = X509Certificate2.CreateFromPem(profile.CertificatePem, profile.PrivateKeyPem);
            // Schannel needs a persisted CNG key handle for certificates created from PEM.
            if (OperatingSystem.IsWindows())
            {
                var pfx = _identity.Export(X509ContentType.Pkcs12);
                _identity.Dispose();
                // DefaultKeySet gives Schannel a temporary OS-protected key container; Dispose deletes it.
                // EphemeralKeySet fails on supported Windows versions with SEC_E_NO_CREDENTIALS.
                try { _identity = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.DefaultKeySet); }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(pfx); }
            }
            handler.SslOptions.ClientCertificates = new X509CertificateCollection { _identity };
        }
        handler.ConnectCallback = async (_, stop) =>
        {
            if (proxyPort is not null) return await OpenProxyTunnelAsync(proxyPort.Value, profile.HealthPort, stop);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                socket.Bind(new IPEndPoint(IPAddress.Parse(profile.VirtualIp), 0));
                if (OperatingSystem.IsWindows() && overlayInterface is not null)
                    socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(overlayInterface.Value));
                await socket.ConnectAsync(IPAddress.Parse(profile.ServerIp), profile.HealthPort, stop);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri($"https://server:{profile.HealthPort}"), Timeout = Timeout.InfiniteTimeSpan };
        if (enrollmentToken is not null) _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", enrollmentToken);
    }

    public Task<EnrollmentResponse> EnrollAsync(EnrollmentRequest request, CancellationToken stop) => SendAsync<EnrollmentResponse>(HttpMethod.Post, "/v2/enroll", request, stop);
    public Task<NetworkConnection> NetworkAsync(CancellationToken stop) => SendAsync<NetworkConnection>(HttpMethod.Get, "/v2/network", null, stop);
    public Task<AddressAssignment> AddressAsync(AddressPreference request, CancellationToken stop) => SendAsync<AddressAssignment>(HttpMethod.Post, "/v2/address", request, stop);
    public async Task HealthAsync(CancellationToken stop)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var result = await SendAsync<HealthReply>(HttpMethod.Get, "/v2/health?nonce=" + nonce, null, stop);
        if (result.Nonce != nonce || !result.Ok) throw new IOException("HEALTH_RESPONSE_INVALID");
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken stop)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop);
        var bytes = await ReadBoundedResponseAsync(response.Content, stop);
        if (!response.IsSuccessStatusCode)
        {
            string code = "CONTROL_REQUEST_FAILED";
            try
            {
                using var doc = JsonDocument.Parse(bytes);
                var candidate = doc.RootElement.GetProperty("code").GetString();
                if (candidate is { Length: < 80 } && candidate.All(c => char.IsAsciiLetterUpper(c) || c == '_')) code = candidate;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
            throw new ControlApiException(code, (int)response.StatusCode);
        }
        return JsonSerializer.Deserialize<T>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new IOException("CONTROL_RESPONSE_INVALID");
    }

    internal static async Task<byte[]> ReadBoundedResponseAsync(HttpContent content, CancellationToken stop)
    {
        const int limit = 128_000;
        if (content.Headers.ContentLength > limit) throw new IOException("CONTROL_RESPONSE_TOO_LARGE");
        await using var source = await content.ReadAsStreamAsync(stop);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit + 1 - (int)output.Length)), stop);
            if (count == 0) return output.ToArray();
            output.Write(buffer, 0, count);
            if (output.Length > limit) throw new IOException("CONTROL_RESPONSE_TOO_LARGE");
        }
    }

    public static async Task<Stream> OpenProxyTunnelAsync(int proxyPort, int targetPort, CancellationToken stop)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, proxyPort, stop);
            var stream = new NetworkStream(socket, ownsSocket: true);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{targetPort} HTTP/1.1\r\nHost: 127.0.0.1:{targetPort}\r\n\r\n"), stop);
            var header = new List<byte>(); var one = new byte[1];
            while (header.Count < 8192)
            {
                if (await stream.ReadAsync(one, stop) == 0) throw new IOException("OUTER_PROXY_CLOSED");
                header.Add(one[0]);
                if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
            }
            var first = Encoding.ASCII.GetString(header.ToArray()).Split('\r')[0];
            if (!first.StartsWith("HTTP/1.1 200", StringComparison.Ordinal) && !first.StartsWith("HTTP/1.0 200", StringComparison.Ordinal)) throw new IOException("OUTER_PROXY_REFUSED");
            return stream;
        }
        catch { socket.Dispose(); throw; }
    }
    public void Dispose() { _http.Dispose(); _identity?.Dispose(); _root.Dispose(); }
    private sealed record HealthReply(bool Ok, string Nonce);
}

using System.Net;
using System.Security.Cryptography.X509Certificates;
using EqLink.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EqLink.Server;

internal sealed class ControlHost(ServerFiles files) : IAsyncDisposable
{
    private WebApplication? _control, _inner, _auth;
    private readonly EnrollmentService _service = new(files);
    private X509Certificate2? _certificate, _ca;
    private static WebApplicationBuilder Builder()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders(); // No HTTP request bodies, headers, credentials, or URLs in diagnostic logs.
        return builder;
    }
    internal async Task StartAsync(CancellationToken stop)
    {
        var state = files.ReadState(); EnrollmentService.RequireV2(state);
        _certificate = X509Certificate2.CreateFromPemFile(Path.Combine(files.Pki, "server.crt"), Path.Combine(files.Pki, "server.key"));
        _ca = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(files.Pki, "ca.crt")));
        _control = CreateTls(IPAddress.Loopback, state.Settings.HealthPort, inner: false);
        await _control.StartAsync(stop);
        var builder = Builder();
        builder.WebHost.ConfigureKestrel(k => { k.Limits.MaxRequestBodySize = 32768; k.Listen(IPAddress.Loopback, state.Settings.AuthPort); });
        _auth = builder.Build();
        _auth.MapPost("/auth", async (HttpContext context) =>
        {
            try
            {
                var request = await context.Request.ReadFromJsonAsync<HyAuth>(context.RequestAborted);
                var ok = _service.AuthenticateHysteria(request?.Auth ?? "", out var id);
                return Results.Json(new { ok, id });
            }
            catch { return Results.Json(new { ok = false, id = "" }); }
        });
        await _auth.StartAsync(stop);
    }

    internal async Task EnsureInnerAsync(CancellationToken stop)
    {
        if (_inner is not null) return;
        var state = files.ReadState(); var ip = IPAddress.Parse(state.Settings.Overlay().ServerIp);
        if (!System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses).Any(a => a.Address.Equals(ip))) return;
        var app = CreateTls(ip, state.Settings.HealthPort, inner: true);
        try { await app.StartAsync(stop); _inner = app; }
        catch { await app.DisposeAsync(); throw; }
    }

    private WebApplication CreateTls(IPAddress bind, int port, bool inner)
    {
        var builder = Builder();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = 32768; k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            k.Listen(bind, port, listen => listen.UseHttps(new HttpsConnectionAdapterOptions
            {
                ServerCertificate = _certificate, ClientCertificateMode = ClientCertificateMode.AllowCertificate,
                ClientCertificateValidation = (cert, _, _) =>
                {
                    using var chain = new X509Chain(); chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.Add(_ca!); chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.2"));
                    return chain.Build(cert);
                }
            }));
        });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            try { await next(context); }
            catch (ControlApiException ex) { context.Response.StatusCode = ex.Status; await context.Response.WriteAsJsonAsync(new { code = ex.Code }); }
            catch (System.Text.Json.JsonException) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { code = "REQUEST_INVALID" }); }
            catch (BadHttpRequestException) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { code = "REQUEST_INVALID" }); }
            catch (System.Security.Cryptography.CryptographicException) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { code = "DEVICE_KEY_INVALID" }); }
            catch (ArgumentException ex) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { code = SafeCode(ex.Message, "REQUEST_INVALID") }); }
            catch (InvalidOperationException ex) { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { code = SafeCode(ex.Message, "CONTROL_UNAVAILABLE") }); }
            catch { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { code = "CONTROL_UNAVAILABLE" }); }
        });
        app.MapGet("/v2/health", (HttpContext c) =>
        {
            _service.Authenticate(c.Connection.ClientCertificate);
            var nonce = c.Request.Query["nonce"].ToString();
            if (nonce.Length != 32 || !nonce.All(Uri.IsHexDigit)) return Results.BadRequest(new { code = "NONCE_INVALID" });
            return Results.Json(new { ok = true, nonce });
        });
        if (!inner)
        {
            app.MapPost("/v2/enroll", async (HttpContext c) =>
            {
                var header = c.Request.Headers.Authorization.ToString();
                if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) throw new ControlApiException("ENROLLMENT_DENIED", 401);
                var request = await c.Request.ReadFromJsonAsync<EnrollmentRequest>(c.RequestAborted) ?? throw new ArgumentException("ENROLLMENT_REQUEST_INVALID");
                return Results.Json(_service.Enroll(header[7..], request));
            });
            app.MapGet("/v2/network", (HttpContext c) => { _service.Authenticate(c.Connection.ClientCertificate); return Results.Json(_service.Network()); });
            app.MapPost("/v2/address", async (HttpContext c) =>
            {
                var device = _service.Authenticate(c.Connection.ClientCertificate);
                var request = await c.Request.ReadFromJsonAsync<AddressPreference>(c.RequestAborted) ?? throw new ArgumentException("ADDRESS_MODE_INVALID");
                return Results.Json(_service.Assign(device.Id, request));
            });
        }
        return app;
    }
    private static string SafeCode(string candidate, string fallback) => candidate.Length < 80 && candidate.All(c => char.IsAsciiLetterUpper(c) || c == '_') ? candidate : fallback;
    public async ValueTask DisposeAsync()
    {
        foreach (var app in new[] { _inner, _control, _auth }) if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
        _certificate?.Dispose(); _ca?.Dispose();
    }
    private sealed record HyAuth(string Auth);
}

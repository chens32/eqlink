using System.Text.Json;
using EqLink.Core;

namespace EqLink.Tests;

internal static partial class Program
{
    private static void OuterFailures()
    {
        var fixtures = new (string Line, string Code)[]
        {
            ("failed to initialize client: tls: failed to verify certificate: x509: certificate signed by unknown authority", "OUTER_CA_UNTRUSTED"),
            ("authentication error: tls: failed to verify certificate: x509: certificate signed by unknown authority", "OUTER_CA_UNTRUSTED"),
            ("x509: certificate has expired or is not yet valid", "OUTER_CERT_TIME_INVALID"),
            ("x509: certificate is valid for old.test.invalid, not new.test.invalid", "OUTER_CERT_NAME_MISMATCH"),
            ("authentication error: no certificate matches the pinned hash", "OUTER_CERT_PIN_MISMATCH"),
            ("TLS certificate verify failed", "OUTER_TRUST_REJECTED"),
            ("Authentication Failed: HTTP status 403", "OUTER_AUTH_REJECTED"),
            ("invalid username/password", "OUTER_AUTH_REJECTED"),
            ("authentication error", "OUTER_AUTH_REJECTED"),
        };
        var secret = CredentialSecrets.New();
        foreach (var (line, code) in fixtures)
        {
            var failure = OuterFailureClassifier.Classify(line + " auth=" + secret) ?? throw new Exception("Outer failure was not classified");
            Check(failure.Code == code, "Outer TLS/account failures were conflated: " + code);
            Check(!string.IsNullOrWhiteSpace(failure.Message), "Actionable error hint missing");
            Check(!JsonSerializer.Serialize(failure).Contains(secret), "Transport output leaked into diagnostics");
            if (code != "OUTER_AUTH_REJECTED") Check(!failure.Message.Contains("凭据"), "Certificate error blamed account credentials");
            if (code == "OUTER_CA_UNTRUSTED") Check(failure.Message.Contains("通用包") && !failure.Message.Contains("受信任根证书库"), "Scoped trust error incorrectly requires a global root installation");
        }
        foreach (var line in new[] { "client mode", "connection refused", "timeout: no recent network activity", "context deadline exceeded", "" })
            Check(OuterFailureClassifier.Classify(line) is null, "Network failure was reported as authentication rejection");
        Check(OuterFailureClassifier.MessageForCode("ENROLLMENT_DENIED") is null, "Server rejection code was overwritten");
        Console.WriteLine("Outer certificate/account failure classification and log redaction: passed");
    }
}

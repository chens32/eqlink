using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EqLink.Core;

/// <summary>Public CA material scoped to EqLink's Hysteria process, never the OS root store.</summary>
public static class HysteriaTrust
{
    public static string[] Fingerprints(string pem)
    {
        if (string.IsNullOrEmpty(pem)) return [];
        if (pem.Length > 65536) throw new ArgumentException("NETWORK_HY2_CA_INVALID");
        var remaining = pem.AsSpan();
        var hashes = new List<string>();
        while (!remaining.Trim().IsEmpty)
        {
            if (hashes.Count == 8 || !PemEncoding.TryFind(remaining, out var field) ||
                !remaining[..field.Location.Start].Trim().IsEmpty ||
                !remaining[field.Label].SequenceEqual("CERTIFICATE"))
                throw new ArgumentException("NETWORK_HY2_CA_INVALID");
            using var certificate = X509Certificate2.CreateFromPem(remaining[field.Location]);
            var authority = certificate.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();
            var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
            if (authority is not { CertificateAuthority: true } || certificate.HasPrivateKey ||
                usage is not null && !usage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign) ||
                DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow >= certificate.NotAfter.ToUniversalTime())
                throw new ArgumentException("NETWORK_HY2_CA_INVALID");
            hashes.Add(Convert.ToHexString(SHA256.HashData(certificate.RawData)));
            remaining = remaining[field.Location.End..];
        }
        if (hashes.Count == 0) throw new ArgumentException("NETWORK_HY2_CA_INVALID");
        return hashes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    public static void Validate(string pem) => _ = Fingerprints(pem);

    public static bool Equivalent(string first, string second) => Fingerprints(first).SequenceEqual(Fingerprints(second));
}

namespace EqLink.Core;

/// <summary>Only fixed codes and hints may leave a transport's private output stream.</summary>
public sealed record OuterFailureInfo(string Code, string Message);

public static class OuterFailureClassifier
{
    public static OuterFailureInfo? Classify(string line)
    {
        bool Has(string value) => line.Contains(value, StringComparison.OrdinalIgnoreCase);

        // A TLS error can be wrapped by "authentication error". Preserve the
        // certificate cause instead of reporting that the account was rejected.
        string? code = Has("certificate signed by unknown authority") ? "OUTER_CA_UNTRUSTED"
            : Has("certificate has expired") || Has("certificate is not yet valid") || Has("certificate expired") ? "OUTER_CERT_TIME_INVALID"
            : Has("certificate is valid for") || Has("certificate is not valid for") ? "OUTER_CERT_NAME_MISMATCH"
            : Has("no certificate matches the pinned hash") ? "OUTER_CERT_PIN_MISMATCH"
            : Has("certificate verify failed") || Has("x509:") ? "OUTER_TRUST_REJECTED"
            : Has("authentication failed") || Has("authentication error") || Has("invalid username/password") ? "OUTER_AUTH_REJECTED"
            : null;
        return code is null ? null : new(code, MessageForCode(code)!);
    }

    public static string? MessageForCode(string code) => code switch
    {
        "OUTER_CA_UNTRUSTED" => "外层证书的签发 CA 未受信任。请联系管理员核对通用包中的 HY2 CA 和服务端证书链，再导入更新的通用包。",
        "OUTER_CERT_TIME_INVALID" => "外层证书尚未生效或已过期。请检查本机时间和服务端证书有效期。",
        "OUTER_CERT_NAME_MISMATCH" => "外层证书名称不匹配。请核对服务器域名、TLS server name 和服务端证书。",
        "OUTER_CERT_PIN_MISMATCH" => "外层证书与入网包中的指纹不匹配。请联系管理员核对证书和入网包。",
        "OUTER_TRUST_REJECTED" => "外层 TLS 证书校验失败。请核对专用 CA、证书名称、有效期和入网包中的指纹。",
        "OUTER_AUTH_REJECTED" => "外层认证被拒绝。请联系管理员核对入网授权或设备凭据。",
        _ => null
    };
}

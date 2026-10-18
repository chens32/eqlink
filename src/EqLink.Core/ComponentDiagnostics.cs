using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace EqLink.Core;

public sealed record ComponentVersion(string? Version,string? Sha256,bool Available,string? Error=null);

/// <summary>Only invokes fixed version commands. Raw stdout/stderr never leaves this helper.</summary>
public static class ComponentDiagnostics
{
    public static object Read(string hysteria,string xray,string openVpn) => new
    {
        eqLink = typeof(ComponentDiagnostics).Assembly.GetName().Version?.ToString(),
        dotnet = Environment.Version.ToString(),
        hysteria = ReadOne(hysteria,"version",@"(?im)^Version:\s*(v?[0-9][\w.+-]*)"),
        xray = ReadOne(xray,"version",@"(?m)^Xray\s+([0-9][\w.+-]*)"),
        openVpn = ReadOne(openVpn,"--version",@"(?m)^OpenVPN\s+([0-9][\w.+-]*)")
    };

    public static string OnPath(string name) => (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator)
        .Select(p=>Path.Combine(p,name)).FirstOrDefault(File.Exists) ?? name;

    private static ComponentVersion ReadOne(string executable,string argument,string pattern)
    {
        if(!File.Exists(executable))return new(null,null,false,"NOT_INSTALLED");
        string? hash=null;
        try
        {
            using(var stream=File.OpenRead(executable))hash=Convert.ToHexString(SHA256.HashData(stream));
            var start=new ProcessStartInfo(executable) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            start.ArgumentList.Add(argument);
            using var process=Process.Start(start) ?? throw new IOException();
            var stdout=ReadBounded(process.StandardOutput);var stderr=ReadBounded(process.StandardError);
            if(!process.WaitForExit(5000)) {process.Kill(true);process.WaitForExit(1000);return new(null,hash,false,"VERSION_TIMEOUT");}
            var match=Regex.Match(stdout.GetAwaiter().GetResult(),pattern);
            _=stderr.GetAwaiter().GetResult();
            return match.Success && process.ExitCode==0 ? new(match.Groups[1].Value,hash,true) : new(null,hash,false,"VERSION_UNAVAILABLE");
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {return new(null,hash,false,"VERSION_UNAVAILABLE");}
    }

    private static async Task<string> ReadBounded(StreamReader reader)
    {
        var buffer=new char[4096];var content=new System.Text.StringBuilder();int count;
        while((count=await reader.ReadAsync(buffer))>0)
            if(content.Length<8192)content.Append(buffer,0,Math.Min(count,8192-content.Length));
        return content.ToString();
    }
}

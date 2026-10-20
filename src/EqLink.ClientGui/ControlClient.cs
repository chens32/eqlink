using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using EqLink.Core;

namespace EqLink.ClientGui;

public static class ControlClient
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static async Task<IpcResponse> SendAsync(IpcRequest request)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(request.Command == "hosts-set" ? 75 : 35));
        await using var pipe = new NamedPipeClientStream(".", "EqLink.Control.v1", PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        await pipe.ConnectAsync(3000, deadline.Token);
        await pipe.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(request, Json), deadline.Token);
        await pipe.WriteAsync("\n"u8.ToArray(), deadline.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8);
        var line = await reader.ReadLineAsync(deadline.Token) ?? throw new IOException("后台服务未返回状态");
        if (line.Length > 1_000_000) throw new IOException("后台响应过大");
        return JsonSerializer.Deserialize<IpcResponse>(line, Json) ?? throw new IOException("后台响应无效");
    }

    private sealed record AdminAction(IpcRequest Request, string? BundlePath);
    public static async Task<IpcResponse> WriteAsync(IpcRequest request, string? bundlePath = null)
    {
        if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            return await SendAsync(await ExpandImportAsync(request, bundlePath));
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EqLink", "requests");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        var result = path + ".result";
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new AdminAction(request, bundlePath), Json));
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--admin-action"); start.ArgumentList.Add(path);
            using var process = Process.Start(start) ?? throw new IOException("无法启动管理员配置助手");
            await process.WaitForExitAsync();
            if (!File.Exists(result)) throw new IOException("管理员配置未完成");
            return JsonSerializer.Deserialize<IpcResponse>(await File.ReadAllTextAsync(result), Json) ?? throw new IOException("配置助手响应无效");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new IOException("已取消管理员授权，配置未更改。"); }
        finally { File.Delete(path); File.Delete(result); }
    }

    internal static async Task<int> RunAdminAsync(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 65536) throw new IOException("配置请求过大");
            var action = JsonSerializer.Deserialize<AdminAction>(await File.ReadAllTextAsync(path), Json) ?? throw new IOException("配置请求无效");
            if (action.Request.Command is not ("import" or "egress-set" or "address-set" or "protection-set" or "mode" or "hosts-set")) throw new IOException("配置操作无效");
            var response = await SendAsync(await ExpandImportAsync(action.Request, action.BundlePath));
            await File.WriteAllTextAsync(path + ".result", JsonSerializer.Serialize(response, Json));
            return response.Ok ? 0 : 1;
        }
        catch
        {
            await File.WriteAllTextAsync(path + ".result", JsonSerializer.Serialize(new IpcResponse(false, "管理员配置失败，请检查服务和文件权限。", new ClientStatus(false, false, "", "", TransportMode.Auto, ActivePath.None, "")), Json));
            return 1;
        }
    }
    private static async Task<IpcRequest> ExpandImportAsync(IpcRequest request, string? path)
    {
        if (request.Command != "import") return request;
        if (path is null || new FileInfo(path).Length > 128000) throw new IOException("通用入网包无效或过大");
        var bytes = await File.ReadAllBytesAsync(path); _ = EnrollmentBundle.Parse(bytes);
        return request with { BundleBase64 = Convert.ToBase64String(bytes) };
    }
}

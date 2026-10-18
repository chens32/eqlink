using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EqLink.ClientGui;
using EqLink.ClientService;
using EqLink.Core;

internal static class Program
{
    private static readonly string Root = Path.GetFullPath("artifacts/egress-ui-check");
    private static readonly List<string> Results = [];

    [STAThread]
    private static int Main()
    {
        var result = 1;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try { await RunAsync(); result = 0; Console.WriteLine("Isolated backend and WPF checks passed."); }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { app.Shutdown(result); }
        };
        app.Run();
        return result;
    }

    private static void Check(bool success, string name)
    {
        if (!success) throw new Exception(name);
        Results.Add(name);
    }

    private static async Task RunAsync()
    {
        var clientRoot = Path.Combine(Root, "isolated-client-root");
        Directory.CreateDirectory(clientRoot);
        var file = Path.Combine(clientRoot, "egress.json");
        var daemon = new EqLinkDaemon();
        typeof(EqLinkDaemon).GetField("_root", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(daemon, clientRoot);
        var dispatch = typeof(EqLinkDaemon).GetMethod("DispatchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var handle = typeof(EqLinkDaemon).GetMethod("HandlePipeAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var want = typeof(EqLinkDaemon).GetField("_wantConnection", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var writes = 0;
        async Task<IpcResponse> Send(IpcRequest request)
        {
            if (request.Command == "egress-set") writes++;
            return await (Task<IpcResponse>)dispatch.Invoke(daemon, [request])!;
        }
        async Task<IpcResponse> SendThroughPipe(IpcRequest request)
        {
            var name = "EqLink.EgressTest." + Guid.NewGuid().ToString("N");
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var accepted = server.WaitForConnectionAsync(stop.Token);
            await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
            await client.ConnectAsync(stop.Token);
            await accepted;
            var handling = (Task)handle.Invoke(daemon, [server, stop.Token])!;
            await client.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n"), stop.Token);
            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            var line = await reader.ReadLineAsync(stop.Token) ?? throw new IOException("Missing pipe response");
            await handling;
            return JsonSerializer.Deserialize<IpcResponse>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        }
        var initial = await Send(new IpcRequest("egress-get"));
        Check(initial.Egress is { Settings: null, Locked: false, Error: null }, "New client uses system routing");
        var nic = initial.Egress!.Interfaces.First(n => n.IsUp && n.Ipv4Addresses.Length > 0);
        var saved = await Send(new IpcRequest("egress-set", Egress: new(nic.Id, ["223.5.5.5"])));
        Check(saved.Ok && saved.Status.Egress == nic.Name && File.Exists(file), "Backend persists adapter and reports its name while disconnected");
        var original = File.ReadAllBytes(file);
        want.SetValue(daemon, true);
        Check((await Send(new IpcRequest("egress-get"))).Egress!.Locked, "Backend reports connection lock during reconnect");
        try { await Send(new IpcRequest("egress-set")); throw new Exception("Connection lock was bypassed"); }
        catch (InvalidOperationException) { }
        Check(File.ReadAllBytes(file).SequenceEqual(original), "Connection lock preserves saved binding");
        want.SetValue(daemon, false);
        try { await Send(new IpcRequest("egress-set", Egress: new("not-present", ["223.5.5.5"]))); throw new Exception("Missing interface accepted"); }
        catch (InvalidOperationException) { }
        Check(File.ReadAllBytes(file).SequenceEqual(original), "Missing interface cannot replace the binding");
        File.WriteAllText(file, "null");
        Check((await Send(new IpcRequest("egress-get"))).Egress!.Error is not null, "Corrupt configuration remains visible and can be repaired");
        await Send(new IpcRequest("egress-set", Egress: new(nic.Id, ["223.5.5.5"])));
        Check((await SendThroughPipe(new IpcRequest("egress-get"))).Egress!.Settings?.InterfaceId == nic.Id, "Read-only settings use the actual named-pipe handler");
        var admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        var authorized = await SendThroughPipe(new IpcRequest("egress-set", Egress: new(nic.Id, ["223.5.5.5"])));
        Check(authorized.Ok == admin, admin ? "Administrator can save through the actual pipe handler" : "Unelevated user cannot save through the actual pipe handler");

        var window = new EgressWindow(Send);
        var load = typeof(EgressWindow).GetMethod("LoadAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)load.Invoke(window, [false])!;
        var box = (ComboBox)window.FindName("InterfaceBox");
        var dns = (TextBox)window.FindName("DnsBox");
        var save = (Button)window.FindName("SaveButton");
        string? SelectedId() => (string?)box.SelectedItem.GetType().GetProperty("Id")!.GetValue(box.SelectedItem);
        Check(SelectedId() == nic.Id && dns.Text == "223.5.5.5" && save.IsEnabled, "GUI loads the persisted adapter and DNS");
        Render(window, "egress-settings.png");
        var main = new MainWindow();
        Render(main, "main-window.png", 494);
        var mainContent = (ScrollViewer)main.Content;
        mainContent.Measure(new Size(494, main.Height - 40));
        mainContent.Arrange(new Rect(0, 0, 494, main.Height - 40));
        mainContent.UpdateLayout();
        Check(((Button)main.FindName("EgressSettingsButton")).ActualHeight > 0 && ((Button)main.FindName("ProtectionButton")).ActualHeight > 0,
            "Main window lays out egress, protection and diagnostic entries with scrolling available");
        Check((await Send(new IpcRequest("diagnostics"))).Diagnostics is not null, "Read-only diagnostics is available through dispatch");
        typeof(EqLinkDaemon).GetField("_authFailure",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(daemon,true);
        want.SetValue(daemon,true);
        var rejected=(Task)typeof(EqLinkDaemon).GetMethod("RunOpenVpnLoopAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(daemon,[0])!;
        await rejected;
        var failedAuth=(await Send(new IpcRequest("status"))).Status;
        Check(failedAuth is {Connected:false,Path:ActivePath.None,Phase:"Error",ErrorCode:"AUTH_FAILED"} && !(bool)want.GetValue(daemon)!,"Inner certificate rejection stays terminal after transport cleanup");
        if(!admin)Check(!(await SendThroughPipe(new IpcRequest("protection-set",ProtectionEnabled:false))).Ok,"Unelevated user cannot disable subnet protection");
        main.Close();
        box.SelectedIndex = 0;
        dns.Text = "192.168.1.1";
        await (Task)load.Invoke(window, [true])!;
        Check(SelectedId() is null && dns.Text == "192.168.1.1" && !dns.IsEnabled, "Refreshing adapters preserves unsaved system-route choice and DNS");
        want.SetValue(daemon, true);
        await (Task)load.Invoke(window, [false])!;
        Check(!save.IsEnabled && !box.IsEnabled && !dns.IsEnabled, "GUI locks editing while connected or reconnecting");
        Render(window, "egress-settings-locked.png");
        want.SetValue(daemon, false);
        await (Task)load.Invoke(window, [false])!;
        var click = typeof(EgressWindow).GetMethod("Save_Click", BindingFlags.NonPublic | BindingFlags.Instance)!;
        dns.Text = "invalid";
        var before = writes;
        click.Invoke(window, [save, new RoutedEventArgs()]);
        await Task.Yield();
        Check(writes == before && ((TextBlock)window.FindName("ResultText")).Text.Length > 0, "GUI rejects invalid DNS before asking the service to save");
        box.SelectedIndex = 0;
        click.Invoke(window, [save, new RoutedEventArgs()]);
        await Task.Yield();
        Check(!File.Exists(file) && (await Send(new IpcRequest("status"))).Status.Egress == "系统路由", "GUI can explicitly restore system routing");
        File.WriteAllText(Path.Combine(Root, "results.json"), JsonSerializer.Serialize(new
        {
            checkedAt = DateTimeOffset.Now,
            scope = "Temporary configuration root; daemon dispatch and WPF controls only. Installed service and active connection were not changed.",
            results = Results
        }, new JsonSerializerOptions { WriteIndented = true }));
        window.Close();
        Directory.Delete(clientRoot);
    }

    private static void Render(Window window, string name, double width = 620)
    {
        var content = (FrameworkElement)window.Content;
        if (content is Panel panel) panel.Background = new SolidColorBrush(Color.FromRgb(246, 248, 251));
        content.Measure(new Size(width, double.PositiveInfinity));
        content.Arrange(new Rect(new Point(0, 0), content.DesiredSize));
        content.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(content.DesiredSize.Width * 1.5), (int)Math.Ceiling(content.DesiredSize.Height * 1.5), 144, 144, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(246, 248, 251)), null, new Rect(new Point(0, 0), content.DesiredSize));
        image.Render(background);
        image.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(Root, name));
        encoder.Save(output);
    }
}

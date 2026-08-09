using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using LanSwitch.Agent.Infrastructure;
using LanSwitch.Agent.Services;
using LanSwitch.Agent.Tray;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace LanSwitch.Agent;

internal static class Program
{
    internal const SslProtocols AllowedPeerTlsProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += OnThreadException;
        try
        {
            return Run(args);
        }
        catch (Exception exception)
        {
            ReportUnhandledException(exception, args.Any(static value =>
                string.Equals(value, "--headless", StringComparison.OrdinalIgnoreCase)));
            return 1;
        }
        finally
        {
            Application.ThreadException -= OnThreadException;
        }
    }

    private static int Run(string[] args)
    {
        var options = AgentOptions.Parse(args);
        using var singleInstance = new Mutex(true, $"Local\\LanSwitch.Agent.{options.InstanceName}", out var createdNew);
        if (!createdNew && !options.AllowMultipleInstances)
        {
            MessageBox.Show("DeskMesh 已经在运行。", "DeskMesh", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        var dataMutexHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(options.DataDirectory.ToUpperInvariant())))[..24];
        using var dataDirectoryMutex = new Mutex(true, $"Local\\LanSwitch.Data.{dataMutexHash}", out var ownsDataDirectory);
        if (!ownsDataDirectory)
        {
            if (!options.Headless)
                MessageBox.Show("另一个 DeskMesh 实例正在使用同一数据目录。", "DeskMesh", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 2;
        }

        Directory.CreateDirectory(options.DataDirectory);
        using var dataDirectoryLock = TryAcquireDataDirectoryLock(options.DataDirectory);
        if (dataDirectoryLock is null)
        {
            if (!options.Headless)
                MessageBox.Show("另一个 DeskMesh 实例正在使用同一数据目录。", "DeskMesh", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 2;
        }
        var identity = DeviceIdentityStore.LoadOrCreate(options.DataDirectory);
        var applicationDirectory = AppContext.BaseDirectory;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = applicationDirectory,
            WebRootPath = Path.Combine(applicationDirectory, "wwwroot")
        });
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, options.WebPort);
            kestrel.Listen(IPAddress.Any, options.PeerPort, listen => listen.UseHttps(https =>
            {
                https.ServerCertificate = identity.Certificate;
                https.SslProtocols = AllowedPeerTlsProtocols;
                https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                https.ClientCertificateValidation = static (_, _, _) => true;
            }));
            kestrel.Limits.MaxRequestBodySize = AgentOptions.MaxFileRequestBytes;
        });

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(identity);
        builder.Services.AddSingleton<SettingsStore>();
        builder.Services.AddSingleton<AppState>();
        builder.Services.AddSingleton<PeerDirectory>();
        builder.Services.AddSingleton<PairingService>();
        builder.Services.AddSingleton<PeerHttpClientFactory>();
        builder.Services.AddSingleton<FocusCoordinator>();
        builder.Services.AddSingleton<ClipboardCoordinator>();
        builder.Services.AddSingleton<FileSaveLocationPicker>();
        builder.Services.AddSingleton<BrowserUploadStager>();
        builder.Services.AddSingleton<FileTransferCoordinator>();
        builder.Services.AddSingleton<DisplayCoordinator>();
        builder.Services.AddSingleton<InputCoordinator>();
        builder.Services.AddSingleton<AudioRelayService>();
        builder.Services.AddSingleton<RemoteDesktopService>();
        builder.Services.AddSingleton<DistributedPhysicalFollowService>();
        builder.Services.AddHostedService<PeerDiscoveryService>();
        builder.Services.AddHostedService<HeartbeatService>();
        builder.Services.AddSingleton<ClipboardWorker>();
        builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<ClipboardWorker>());
        builder.Services.AddHostedService<InputWorker>();
        builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<AudioRelayService>());
        builder.Services.AddHostedService(serviceProvider =>
            serviceProvider.GetRequiredService<DistributedPhysicalFollowService>());
        builder.Services.AddHostedService<WindowsIntegrationService>();
        builder.Services.AddRouting();
        builder.Services.AddProblemDetails();
        builder.Services.Configure<FormOptions>(form =>
        {
            form.MultipartBodyLengthLimit = AgentOptions.MaxFileRequestBytes;
            form.MemoryBufferThreshold = 64 * 1024;
        });

        using var app = builder.Build();
        _ = app.Services.GetRequiredService<BrowserUploadStager>();
        ApiEndpoints.ConfigurePipeline(app);
        HotkeyEndpoints.Map(app);
        PhysicalFollowEndpoints.Map(app);
        AudioEndpoints.Map(app);
        RemoteDesktopEndpoints.Map(app);
        ApiEndpoints.Map(app);

        try
        {
            app.StartAsync().GetAwaiter().GetResult();
            if (options.Headless)
            {
                app.WaitForShutdownAsync().GetAwaiter().GetResult();
                return 0;
            }

            using var context = new TrayApplicationContext(app.Services, options);
            Application.Run(context);
            return 0;
        }
        finally
        {
            app.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        }
    }

    private static void OnThreadException(object sender, ThreadExceptionEventArgs eventArgs) =>
        ReportUnhandledException(eventArgs.Exception, headless: false);

    private static void ReportUnhandledException(Exception exception, bool headless)
    {
        var message = $"DeskMesh 遇到未预期错误，已阻止系统异常窗口。\n\n{exception.Message}";
        try
        {
            if (headless) Console.Error.WriteLine(message);
            else MessageBox.Show(message, "DeskMesh 操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            try { Console.Error.WriteLine(message); } catch { }
        }
    }

    private static FileStream? TryAcquireDataDirectoryLock(string dataDirectory)
    {
        try
        {
            return new FileStream(Path.Combine(dataDirectory, ".agent.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

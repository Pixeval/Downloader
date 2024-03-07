using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Downloader.DummyHttpServer;

public class HttpServer
{
    private static IMemoryCache s_cache = new MemoryCache(new MemoryCacheOptions());
    private static IWebHost s_server;
    public static int Port { get; set; } = 3333;
    public static CancellationTokenSource CancellationToken { get; set; }

    public static async Task Main()
    {
        Run(Port);
        Console.ReadKey();
        await Stop();
    }

    public static void Run(int port)
    {
        CancellationToken ??= new CancellationTokenSource();
        if (CancellationToken.IsCancellationRequested)
            return;

        s_server ??= s_cache.GetOrCreate("DownloaderWebHost", e => {
            var host = CreateHostBuilder(port);
            host.RunAsync(CancellationToken.Token).ConfigureAwait(false);
            return host;
        });

        if (port == 0) // dynamic port
            SetPort();
    }

    private static void SetPort()
    {
        var feature = s_server.ServerFeatures.Get<IServerAddressesFeature>();
        if (feature.Addresses.Any())
        {
            var address = feature.Addresses.First();
            Port = new Uri(address).Port;
        }
    }

    public static async Task Stop()
    {
        if (s_server is not null)
        {
            CancellationToken?.Cancel();
            await s_server?.StopAsync();
            s_server?.Dispose();
            s_server = null;
        }
    }

    public static IWebHost CreateHostBuilder(int port)
    {
        var host = WebHost.CreateDefaultBuilder()
                      .UseStartup<Startup>();

        if (port > 0)
        {
            host = host.UseUrls($"http://localhost:{port}");
        }

        return host.Build();
    }
}

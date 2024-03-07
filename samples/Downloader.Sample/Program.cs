using Downloader.Extensions.Logging;
using Newtonsoft.Json;
using ShellProgressBar;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileLogger = Downloader.Tests.Helper.FileLogger;

namespace Downloader.Sample;

public partial class Program
{
    private const string DownloadListFile = "download.json";
    private static List<DownloadItem> s_downloadList;
    private static ProgressBar s_consoleProgress;
    private static ConcurrentDictionary<string, ChildProgressBar> s_childConsoleProgresses;
    private static ProgressBarOptions s_childOption;
    private static ProgressBarOptions s_processBarOption;
    private static IDownloadService s_currentDownloadService;
    private static DownloadConfiguration s_currentDownloadConfiguration;
    private static CancellationTokenSource s_cancelAllTokenSource;
    private static ILogger s_logger;

    private static async Task Main()
    {
        try
        {
#if NETCOREAPP
            DummyHttpServer.HttpServer.Run(3333);
#endif
            await Task.Delay(1000);
            Console.Clear();
            Initial();
            new Task(KeyboardHandler).Start();
            await DownloadAll(s_downloadList, s_cancelAllTokenSource.Token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Console.Clear();
            Console.Error.WriteLine(e);
            Debugger.Break();
        }
        finally
        {
#if NETCOREAPP
            await DummyHttpServer.HttpServer.Stop();
#endif
        }

        Console.WriteLine("END");
    }
    private static void Initial()
    {
        s_cancelAllTokenSource = new CancellationTokenSource();
        s_childConsoleProgresses = new ConcurrentDictionary<string, ChildProgressBar>();
        s_downloadList = GetDownloadItems();

        s_processBarOption = new ProgressBarOptions {
            ForegroundColor = ConsoleColor.Green,
            ForegroundColorDone = ConsoleColor.DarkGreen,
            BackgroundColor = ConsoleColor.DarkGray,
            BackgroundCharacter = '\u2593',
            EnableTaskBarProgress = true,
            ProgressBarOnBottom = false,
            ProgressCharacter = '#'
        };
        s_childOption = new ProgressBarOptions {
            ForegroundColor = ConsoleColor.Yellow,
            BackgroundColor = ConsoleColor.DarkGray,
            ProgressCharacter = '-',
            ProgressBarOnBottom = true
        };
    }
    private static void KeyboardHandler()
    {
        ConsoleKeyInfo cki;
        Console.CancelKeyPress += CancelAll;

        while (true)
        {
            cki = Console.ReadKey(true);
            if (s_currentDownloadConfiguration != null)
            {
                switch (cki.Key)
                {
                    case ConsoleKey.P:
                        s_currentDownloadService?.Pause();
                        Console.Beep();
                        break;
                    case ConsoleKey.R:
                        s_currentDownloadService?.Resume();
                        break;
                    case ConsoleKey.Escape:
                        s_currentDownloadService?.CancelAsync();
                        break;
                    case ConsoleKey.UpArrow:
                        s_currentDownloadConfiguration.MaximumBytesPerSecond *= 2;
                        break;
                    case ConsoleKey.DownArrow:
                        s_currentDownloadConfiguration.MaximumBytesPerSecond /= 2;
                        break;
                }
            }
        }
    }
    private static void CancelAll(object sender, ConsoleCancelEventArgs e)
    {
        s_cancelAllTokenSource.Cancel();
        s_currentDownloadService?.CancelAsync();
    }

    private static List<DownloadItem> GetDownloadItems()
    {
        List<DownloadItem> downloadList = File.Exists(DownloadListFile)
            ? JsonConvert.DeserializeObject<List<DownloadItem>>(File.ReadAllText(DownloadListFile))
            : new List<DownloadItem>();

        return downloadList;
    }

    private static async Task DownloadAll(IEnumerable<DownloadItem> downloadList, CancellationToken cancelToken)
    {
        foreach (DownloadItem downloadItem in downloadList)
        {
            if (cancelToken.IsCancellationRequested)
                return;

            // begin download from url
            await DownloadFile(downloadItem).ConfigureAwait(false);
        }
    }

    private static async Task<IDownloadService> DownloadFile(DownloadItem downloadItem)
    {
        s_currentDownloadConfiguration = GetDownloadConfiguration();
        s_currentDownloadService = CreateDownloadService(s_currentDownloadConfiguration);
        if (string.IsNullOrWhiteSpace(downloadItem.FileName))
        {
            s_logger = FileLogger.Factory(downloadItem.FolderPath);
            s_currentDownloadService.AddLogger(s_logger);
            await s_currentDownloadService.DownloadFileTaskAsync(downloadItem.Url, new DirectoryInfo(downloadItem.FolderPath)).ConfigureAwait(false);
        }
        else
        {
            s_logger = FileLogger.Factory(downloadItem.FolderPath, Path.GetFileName(downloadItem.FileName));
            s_currentDownloadService.AddLogger(s_logger);
            await s_currentDownloadService.DownloadFileTaskAsync(downloadItem.Url, downloadItem.FileName).ConfigureAwait(false);
        }

        if (downloadItem.ValidateData)
        {
            var isValid = await ValidateDataAsync(s_currentDownloadService.Package.FileName, s_currentDownloadService.Package.TotalFileSize).ConfigureAwait(false);
            if (!isValid)
            {
                var message = "Downloaded data is invalid: " + s_currentDownloadService.Package.FileName;
                s_logger?.LogCritical(message);
                throw new InvalidDataException(message);
            }
        }

        return s_currentDownloadService;
    }

    private static async Task<bool> ValidateDataAsync(string filename, long size)
    {
        await using var stream = File.OpenRead(filename);
        for (var i = 0L; i < size; i++)
        {
            var next = stream.ReadByte();
            if (next != i % 256)
            {
                s_logger?.LogWarning($"Sample.Program.ValidateDataAsync():  Data at index [{i}] of `{filename}` is `{next}`, expectation is `{i % 256}`");
                return false;
            }
        }

        return true;
    }

    private static void WriteKeyboardGuidLines()
    {
        Console.Clear();
        Console.WriteLine("Press Esc to Stop current file download");
        Console.WriteLine("Press P to Pause and R to Resume downloading");
        Console.WriteLine("Press Up Arrow to Increase download speed 2X");
        Console.WriteLine("Press Down Arrow to Decrease download speed 2X");
        Console.WriteLine();
    }
    private static DownloadService CreateDownloadService(DownloadConfiguration config)
    {
        var downloadService = new DownloadService(config);

        // Provide `FileName` and `TotalBytesToReceive` at the start of each downloads
        downloadService.DownloadStarted += OnDownloadStarted;

        // Provide any information about chunker downloads, 
        // like progress percentage per chunk, speed, 
        // total received bytes and received bytes array to live streaming.
        downloadService.ChunkDownloadProgressChanged += OnChunkDownloadProgressChanged;

        // Provide any information about download progress, 
        // like progress percentage of sum of chunks, total speed, 
        // average speed, total received bytes and received bytes array 
        // to live streaming.
        downloadService.DownloadProgressChanged += OnDownloadProgressChanged;

        // Download completed event that can include occurred errors or 
        // cancelled or download completed successfully.
        downloadService.DownloadFileCompleted += OnDownloadFileCompleted;

        return downloadService;
    }

    private static void OnDownloadStarted(object sender, DownloadStartedEventArgs e)
    {
        WriteKeyboardGuidLines();
        s_consoleProgress = new ProgressBar(10000, $"Downloading {Path.GetFileName(e.FileName)}   ", s_processBarOption);
    }

    private static void OnDownloadFileCompleted(object sender, AsyncCompletedEventArgs e)
    {
        s_consoleProgress?.Tick(10000);

        if (e.Cancelled)
        {
            s_consoleProgress.Message += " CANCELED";
        }
        else if (e.Error != null)
        {
            Console.Error.WriteLine(e.Error);
            Debugger.Break();
        }
        else
        {
            s_consoleProgress.Message += " DONE";
            Console.Title = "100%";
        }

        foreach (var child in s_childConsoleProgresses.Values)
            child.Dispose();

        s_childConsoleProgresses.Clear();
        s_consoleProgress?.Dispose();
    }

    private static void OnChunkDownloadProgressChanged(object sender, DownloadProgressChangedEventArgs e)
    {
        ChildProgressBar progress = s_childConsoleProgresses.GetOrAdd(e.ProgressId,
            id => s_consoleProgress?.Spawn(10000, $"chunk {id}", s_childOption));
        progress.Tick((int)(e.ProgressPercentage * 100));
        var activeChunksCount = e.ActiveChunks; // Running chunks count
    }

    private static void OnDownloadProgressChanged(object sender, DownloadProgressChangedEventArgs e)
    {
        s_consoleProgress.Tick((int)(e.ProgressPercentage * 100));
        if (sender is DownloadService ds)
            e.UpdateTitleInfo(ds.IsPaused);
    }
}

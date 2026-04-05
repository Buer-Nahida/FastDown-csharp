using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using Spectre.Console.Cli;

namespace fd_cs
{
    public class Program
    {
        public static int Main(string[] args)
        {
            var app = new CommandApp<DownloadCommand>();
            app.Configure(config =>
            {
                config.AddCommand<DownloadCommand>("download").WithDescription("Download a file (default)");
                config.AddCommand<ListCommand>("list").WithDescription("List database entries");
                config.SetApplicationName("fd-cs");
            });

            return app.Run(args);
        }
    }

    public class DownloadSettings : CommandSettings
    {
        [CommandArgument(0, "<URL>")]
        [Description("The URL to download")]
        public string Url { get; set; } = string.Empty;

        [CommandOption("-d|--dir <DIR>")]
        [Description("Save directory")]
        [DefaultValue(".")]
        public string SaveFolder { get; set; } = ".";

        [CommandOption("-t|--threads <THREADS>")]
        [Description("Number of download threads")]
        [DefaultValue(32)]
        public int Threads { get; set; }

        [CommandOption("-f|--force")]
        [Description("Force overwrite existing file")]
        [DefaultValue(false)]
        public bool Force { get; set; }

        [CommandOption("--no-resume")]
        [Description("Disable resume")]
        [DefaultValue(false)]
        public bool NoResume { get; set; }

        [CommandOption("-o|--out <FILE_NAME>")]
        [Description("Custom file name")]
        public string? FileName { get; set; }

        [CommandOption("-p|--proxy <PROXY>")]
        [Description("Proxy address (e.g. http://proxy:port)")]
        public string? Proxy { get; set; }

        [CommandOption("-H|--header <HEADER>")]
        [Description("Custom request headers (can be used multiple times, e.g. 'Key: Value')")]
        public string[]? Headers { get; set; }
    }

    public class ListCommand : AsyncCommand<EmptyCommandSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, EmptyCommandSettings settings)
        {
            using var store = await Store.CreateAsync();
            var entries = store.GetAllEntries();
            
            if (entries.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]Database is empty.[/]");
                return 0;
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("File Name");
            table.AddColumn("Size");
            table.AddColumn("Progress");
            table.AddColumn("URL");

            foreach (var (path, record) in entries)
            {
                long downloaded = record.Progress.Sum(p => p[1] - p[0]);
                double pct = record.FileSize == 0 ? 0 : (double)downloaded / record.FileSize * 100;
                table.AddRow(
                    record.FileName,
                    $"{record.FileSize / 1024.0 / 1024.0:F2} MB",
                    $"{pct:F1}%",
                    record.Url
                );
            }

            AnsiConsole.Write(table);
            return 0;
        }
    }

    public class DownloadCommand : AsyncCommand<DownloadSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, DownloadSettings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.Url))
            {
                AnsiConsole.MarkupLine("[red]Error: URL is required.[/]");
                return 1;
            }

            string fileName = settings.FileName ?? Path.GetFileName(new Uri(settings.Url).LocalPath);
            if (string.IsNullOrEmpty(fileName)) fileName = "download.bin";
            
            Directory.CreateDirectory(settings.SaveFolder);
            string savePath = Path.Combine(settings.SaveFolder, fileName);

            if (File.Exists(savePath) && !settings.Force && settings.NoResume)
            {
                if (!AnsiConsole.Confirm($"[yellow]File '{fileName}' already exists. Overwrite?[/]"))
                {
                    AnsiConsole.MarkupLine("[red]Download cancelled.[/]");
                    return 1;
                }
            }

            var headers = new Dictionary<string, string>();
            if (settings.Headers != null)
            {
                foreach (var header in settings.Headers)
                {
                    var parts = header.Split(':', 2, StringSplitOptions.TrimEntries);
                    if (parts.Length == 2)
                    {
                        headers[parts[0]] = parts[1];
                    }
                }
            }

            using var store = await Store.CreateAsync();
            var downloader = new Downloader(settings.Url, savePath, settings.Threads, !settings.NoResume, store, headers, settings.Proxy);

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                AnsiConsole.MarkupLine("[yellow]Cancelling...[/]");
                e.Cancel = true;
                cts.Cancel();
            };

            await AnsiConsole.Progress()
                .AutoRefresh(false) // Custom refresh
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(new ProgressColumn[]
                {
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new DownloadedColumn(),
                    new TransferSpeedColumn(),
                    new RemainingTimeColumn(),
                    new ElapsedTimeColumn()
                })
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask($"[green]{fileName}[/]", new ProgressTaskSettings { AutoStart = false });
                    
                    downloader.OnProgress += (p) =>
                    {
                        if (p.totalBytes > 0)
                        {
                            task.MaxValue = p.totalBytes;
                            task.Value = p.downloadedBytes;
                        }
                        ctx.Refresh();
                    };

                    downloader.OnLog += (msg) =>
                    {
                        AnsiConsole.MarkupLine($"[grey]{msg}[/]");
                    };

                    task.StartTask();
                    try
                    {
                        await downloader.StartAsync(cts.Token);
                        task.Value = task.MaxValue; // Ensure 100% on complete
                    }
                    catch (OperationCanceledException)
                    {
                        AnsiConsole.MarkupLine("[yellow]Download aborted by user.[/]");
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]Download failed: {ex.Message}[/]");
                    }
                });

            if (!cts.Token.IsCancellationRequested && File.Exists(savePath))
            {
                AnsiConsole.MarkupLine($"[green]Successfully downloaded '{fileName}'[/]");
            }

            return 0;
        }
    }
}

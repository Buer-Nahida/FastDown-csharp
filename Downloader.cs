using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace fd_cs
{
    public class Downloader
    {
        private readonly HttpClient _client;
        private readonly string _url;
        private readonly string _savePath;
        private readonly int _threads;
        private readonly bool _resume;
        private readonly Store _store;

        public event Action<(long downloadedBytes, long totalBytes, double speedBps, TimeSpan elapsed, List<(long start, long end)> progress)>? OnProgress;
        public event Action<string>? OnLog;

        public Downloader(string url, string savePath, int threads, bool resume, Store store, Dictionary<string, string>? headers = null, string? proxy = null)
        {
            _url = url;
            _savePath = savePath;
            _threads = threads;
            _resume = resume;
            _store = store;

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true // allow invalid certs
            };

            if (!string.IsNullOrEmpty(proxy))
            {
                handler.Proxy = new WebProxy(proxy);
                handler.UseProxy = true;
            }
            else if (proxy == "")
            {
                handler.UseProxy = false;
            }

            _client = new HttpClient(handler);

            if (headers != null)
            {
                foreach (var h in headers)
                {
                    _client.DefaultRequestHeaders.TryAddWithoutValidation(h.Key, h.Value);
                }
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            OnLog?.Invoke($"Prefetching {_url}...");

            var req = new HttpRequestMessage(HttpMethod.Get, _url);
            req.Headers.Range = new RangeHeaderValue(0, 0);
            
            var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            resp.EnsureSuccessStatusCode();

            var totalSize = resp.Content.Headers.ContentRange?.Length ?? resp.Content.Headers.ContentLength ?? 0;
            var supportsRange = resp.Content.Headers.ContentRange != null || resp.StatusCode == HttpStatusCode.PartialContent;
            var etag = resp.Headers.ETag?.Tag;
            var lastModified = resp.Content.Headers.LastModified?.ToString();

            OnLog?.Invoke($"Size: {totalSize} bytes. Supports Range: {supportsRange}");

            List<(long start, long end)> remainingChunks = new() { (0L, totalSize) };
            List<(long start, long end)> currentProgress = new();
            TimeSpan elapsed = TimeSpan.Zero;

            if (_resume && File.Exists(_savePath) && supportsRange)
            {
                var entry = _store.GetEntry(_savePath);
                if (entry != null && entry.FileSize == (ulong)totalSize)
                {
                    OnLog?.Invoke("Resuming download...");
                    currentProgress = entry.Progress.Select(p => (p[0], p[1])).ToList();
                    remainingChunks = Invert(currentProgress, totalSize).ToList();
                    elapsed = TimeSpan.FromMilliseconds(entry.ElapsedMs);
                }
            }
            else
            {
                _store.InitEntry(_savePath, Path.GetFileName(_savePath), (ulong)totalSize, etag, lastModified, _url);
            }

            if (remainingChunks.Count == 0 || totalSize == 0)
            {
                OnLog?.Invoke("File already fully downloaded or empty.");
                return; // Nothing to download
            }

            using var fileHandle = File.OpenHandle(_savePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite, FileOptions.Asynchronous);
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                RandomAccess.SetLength(fileHandle, totalSize);
            }

            if (!supportsRange)
            {
                await DownloadSingleAsync(fileHandle, totalSize, cancellationToken);
            }
            else
            {
                await DownloadMultiAsync(fileHandle, remainingChunks, currentProgress, totalSize, elapsed, cancellationToken);
            }
        }

        private async Task DownloadSingleAsync(SafeFileHandle fileHandle, long totalSize, CancellationToken ct)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, _url);
            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[81920]; // 80KB buffer
            long downloaded = 0;
            var sw = Stopwatch.StartNew();

            while (!ct.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer, ct);
                if (read == 0) break;

                await RandomAccess.WriteAsync(fileHandle, buffer.AsMemory(0, read), downloaded, ct);
                downloaded += read;

                if (sw.ElapsedMilliseconds > 200)
                {
                    OnProgress?.Invoke((downloaded, totalSize, downloaded / sw.Elapsed.TotalSeconds, sw.Elapsed, new List<(long, long)> { (0, downloaded) }));
                }
            }
        }

        private async Task DownloadMultiAsync(SafeFileHandle fileHandle, List<(long, long)> remainingChunks, List<(long, long)> currentProgress, long totalSize, TimeSpan previousElapsed, CancellationToken ct)
        {
            var taskQueue = new TaskQueue(remainingChunks);
            var sw = Stopwatch.StartNew();
            long initialDownloaded = currentProgress.Sum(p => p.Item2 - p.Item1);
            long totalDownloadedSession = 0;

            var workers = new List<Task>();
            var lockObj = new object();
            long minChunkSize = 1024 * 1024; // 1MB

            var localProgress = new List<(long start, long end)>();
            
            for (int i = 0; i < _threads; i++)
            {
                workers.Add(Task.Run(async () =>
                {
                    TaskItem? currentTask = taskQueue.Steal(null!, minChunkSize);
                    if (currentTask == null) return;
                    taskQueue.RegisterWorker(currentTask);

                    try
                    {
                        var buffer = new byte[128 * 1024]; // 128KB buffer
                        while (!ct.IsCancellationRequested)
                        {
                            long start = currentTask.Start;
                            long end = currentTask.End;

                            if (start >= end)
                            {
                                taskQueue.UnregisterWorker(currentTask);
                                currentTask = taskQueue.Steal(currentTask, minChunkSize);
                                if (currentTask == null) break;
                                taskQueue.RegisterWorker(currentTask);
                                continue;
                            }

                            var req = new HttpRequestMessage(HttpMethod.Get, _url);
                            req.Headers.Range = new RangeHeaderValue(start, end - 1); // HTTP Range is inclusive
                            
                            try
                            {
                                using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                                resp.EnsureSuccessStatusCode();

                                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                                while (start < end && !ct.IsCancellationRequested)
                                {
                                    int read = await stream.ReadAsync(buffer, ct);
                                    if (read == 0) break;

                                    var range = currentTask.SafeAddStart(start, read);
                                    if (range == null) break; // Range was stolen

                                    long actualRead = range.Value.Item2 - range.Value.Item1;
                                    await RandomAccess.WriteAsync(fileHandle, buffer.AsMemory(0, (int)actualRead), start, ct);
                                    start += actualRead;

                                    lock (lockObj)
                                    {
                                        totalDownloadedSession += actualRead;
                                        localProgress.Add((range.Value.Item1, range.Value.Item2));
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                if (ct.IsCancellationRequested) throw;
                                OnLog?.Invoke($"Worker Error: {ex.Message}");
                                await Task.Delay(500, ct); // Retry gap
                            }
                        }
                    }
                    finally
                    {
                        if (currentTask != null) taskQueue.UnregisterWorker(currentTask);
                    }
                }, ct));
            }

            var reportTask = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(200, ct);
                    long downSession;
                    List<(long, long)> toMerge;
                    lock (lockObj)
                    {
                        downSession = totalDownloadedSession;
                        toMerge = new List<(long, long)>(localProgress);
                        localProgress.Clear();
                    }

                    var merged = MergeProgress(currentProgress.Concat(toMerge).ToList());
                    currentProgress = merged;

                    var elapsed = previousElapsed + sw.Elapsed;
                    double speed = downSession / sw.Elapsed.TotalSeconds;

                    OnProgress?.Invoke((initialDownloaded + downSession, totalSize, speed, elapsed, merged));
                    
                    _store.UpdateEntry(_savePath, merged.Select(m => new[] { m.start, m.end }).ToList(), elapsed);

                    if (initialDownloaded + downSession >= totalSize) break;
                }
            }, ct);

            await Task.WhenAll(workers);
            await reportTask;
            
            // Final store update
            _store.RemoveEntry(_savePath);
        }

        private static IEnumerable<(long start, long end)> Invert(List<(long start, long end)> progress, long totalSize)
        {
            long current = 0;
            foreach (var p in progress.OrderBy(x => x.start))
            {
                if (p.start > current)
                {
                    yield return (current, p.start);
                }
                current = Math.Max(current, p.end);
            }
            if (current < totalSize)
            {
                yield return (current, totalSize);
            }
        }

        private static List<(long start, long end)> MergeProgress(List<(long start, long end)> progress)
        {
            if (progress.Count == 0) return progress;
            var sorted = progress.OrderBy(x => x.start).ToList();
            var merged = new List<(long start, long end)> { sorted[0] };

            for (int i = 1; i < sorted.Count; i++)
            {
                var last = merged.Last();
                var curr = sorted[i];

                if (curr.start <= last.end)
                {
                    merged[merged.Count - 1] = (last.start, Math.Max(last.end, curr.end));
                }
                else
                {
                    merged.Add(curr);
                }
            }
            return merged;
        }
    }
}

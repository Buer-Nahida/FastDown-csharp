using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace fd_cs
{
    using System.Text.Json.Serialization;

    [JsonSerializable(typeof(DownloadingRecord))]
    internal partial class AppJsonSerializerContext : JsonSerializerContext
    {
    }

    public class DownloadingRecord
    {
        public string FileName { get; set; } = string.Empty;
        public ulong FileSize { get; set; }
        public string? Etag { get; set; }
        public string? LastModified { get; set; }
        public List<long[]> Progress { get; set; } = new List<long[]>(); // list of [start, end]
        public ulong ElapsedMs { get; set; }
        public string Url { get; set; } = string.Empty;
    }

    public class Store : IDisposable
    {
        private readonly SqliteConnection _conn;
        private readonly ConcurrentDictionary<string, (bool isDirty, DownloadingRecord record)> _cache = new();
        private readonly string _dbPath;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _flushTask;

        private Store(SqliteConnection conn, string dbPath)
        {
            _conn = conn;
            _dbPath = dbPath;
            _flushTask = Task.Run(PeriodicFlushAsync);
        }

        public static async Task<Store> CreateAsync()
        {
            var dbPath = Path.Combine(Directory.GetCurrentDirectory(), "fd-state-v2.db");
            var conn = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
            await conn.OpenAsync();
            
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                CREATE TABLE IF NOT EXISTS downloads (path TEXT PRIMARY KEY, data TEXT);
            ";
            await cmd.ExecuteNonQueryAsync();

            var store = new Store(conn, dbPath);
            await store.CleanAsync();
            return store;
        }

        private async Task CleanAsync()
        {
            var pathsToDelete = new List<string>();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT path FROM downloads";
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    pathsToDelete.Add(reader.GetString(0));
                }
            }

            foreach (var path in pathsToDelete)
            {
                if (!File.Exists(path))
                {
                    using var cmd = _conn.CreateCommand();
                    cmd.CommandText = "DELETE FROM downloads WHERE path = @path";
                    cmd.Parameters.AddWithValue("@path", path);
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }

        public void InitEntry(string filePath, string fileName, ulong fileSize, string? etag, string? lastModified, string url)
        {
            var record = new DownloadingRecord
            {
                FileName = fileName,
                FileSize = fileSize,
                Etag = etag,
                LastModified = lastModified,
                Url = url,
                ElapsedMs = 0,
                Progress = new List<long[]>()
            };

            var data = JsonSerializer.Serialize(record, AppJsonSerializerContext.Default.DownloadingRecord);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO downloads (path, data) VALUES (@path, @data)";
            cmd.Parameters.AddWithValue("@path", filePath);
            cmd.Parameters.AddWithValue("@data", data);
            cmd.ExecuteNonQuery();

            _cache[filePath] = (false, record);
        }

        public DownloadingRecord? GetEntry(string filePath)
        {
            if (_cache.TryGetValue(filePath, out var cached))
                return cached.record;

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT data FROM downloads WHERE path = @path";
            cmd.Parameters.AddWithValue("@path", filePath);
            var data = cmd.ExecuteScalar() as string;
            
            if (data == null) return null;

            var record = JsonSerializer.Deserialize(data, AppJsonSerializerContext.Default.DownloadingRecord);
            if (record != null)
            {
                _cache[filePath] = (false, record);
            }
            return record;
        }

        public void UpdateEntry(string filePath, List<long[]> progress, TimeSpan elapsed)
        {
            if (_cache.TryGetValue(filePath, out var entry))
            {
                entry.record.Progress = progress;
                entry.record.ElapsedMs = (ulong)elapsed.TotalMilliseconds;
                _cache[filePath] = (true, entry.record);
            }
            else if (GetEntry(filePath) is { } record)
            {
                record.Progress = progress;
                record.ElapsedMs = (ulong)elapsed.TotalMilliseconds;
                _cache[filePath] = (true, record);
            }
        }

        public void RemoveEntry(string filePath)
        {
            _cache.TryRemove(filePath, out _);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM downloads WHERE path = @path";
            cmd.Parameters.AddWithValue("@path", filePath);
            cmd.ExecuteNonQuery();
        }

        public List<(string Path, DownloadingRecord Record)> GetAllEntries()
        {
            var results = new List<(string, DownloadingRecord)>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT path, data FROM downloads";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var path = reader.GetString(0);
                var data = reader.GetString(1);
                var record = JsonSerializer.Deserialize(data, AppJsonSerializerContext.Default.DownloadingRecord);
                if (record != null)
                {
                    results.Add((path, record));
                }
            }
            return results;
        }

        private async Task PeriodicFlushAsync()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, _cts.Token);
                    Flush();
                }
                catch (TaskCanceledException) { break; }
                catch { /* Ignore */ }
            }
        }

        private void Flush()
        {
            var dirty = _cache.Where(kvp => kvp.Value.isDirty).ToList();
            if (dirty.Count == 0) return;

            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO downloads (path, data) VALUES (@path, @data)";
            
            var pathParam = cmd.Parameters.Add("@path", SqliteType.Text);
            var dataParam = cmd.Parameters.Add("@data", SqliteType.Text);

            foreach (var kvp in dirty)
            {
                pathParam.Value = kvp.Key;
                dataParam.Value = JsonSerializer.Serialize(kvp.Value.record, AppJsonSerializerContext.Default.DownloadingRecord);
                cmd.ExecuteNonQuery();
                _cache[kvp.Key] = (false, kvp.Value.record);
            }
            tx.Commit();
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _flushTask.Wait(500); } catch { }
            Flush();
            _conn.Dispose();
        }
    }
}

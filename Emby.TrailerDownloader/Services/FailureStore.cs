using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace Emby.TrailerDownloader.Services
{
    public class FailureRecord
    {
        public long ItemId { get; set; }
        public string Name { get; set; }
        public int Attempts { get; set; }
        public DateTime LastAttemptUtc { get; set; }
        public string LastError { get; set; }
    }

    /// <summary>
    /// Remembers items whose trailer could not be downloaded so retries back off instead of hammering
    /// YouTube on every scan, and so items that keep failing are eventually skipped.
    /// </summary>
    public class FailureStore
    {
        private static readonly object Sync = new object();

        private readonly IJsonSerializer _json;
        private readonly ILogger _logger;
        private readonly string _path;
        private Dictionary<long, FailureRecord> _records;

        public FailureStore(IJsonSerializer json, ILogger logger, string dataDir)
        {
            _json = json;
            _logger = logger;
            _path = Path.Combine(dataDir, "failures.json");
        }

        public bool ShouldAttempt(long itemId, bool ignoreBackoff)
        {
            lock (Sync)
            {
                // Manual requests / "retry all" always get a go, even past the give-up limit.
                if (ignoreBackoff) return true;
                if (!Load().TryGetValue(itemId, out var record)) return true;

                var maxAttempts = Plugin.Instance.Configuration.MaxAttempts;
                if (maxAttempts > 0 && record.Attempts >= maxAttempts) return false;

                // 6h, 12h, 24h, 48h ... capped at 7 days.
                var hours = Math.Min(6 * Math.Pow(2, Math.Max(0, record.Attempts - 1)), 24 * 7);
                return DateTime.UtcNow - record.LastAttemptUtc >= TimeSpan.FromHours(hours);
            }
        }

        public void RecordFailure(long itemId, string name, string error)
        {
            lock (Sync)
            {
                var records = Load();
                if (!records.TryGetValue(itemId, out var record))
                {
                    record = new FailureRecord { ItemId = itemId };
                    records[itemId] = record;
                }

                record.Name = name;
                record.Attempts++;
                record.LastAttemptUtc = DateTime.UtcNow;
                record.LastError = error;
                Save(records);
            }
        }

        public void Clear(long itemId)
        {
            lock (Sync)
            {
                var records = Load();
                if (records.Remove(itemId)) Save(records);
            }
        }

        public void ClearAll()
        {
            lock (Sync)
            {
                _records = new Dictionary<long, FailureRecord>();
                Save(_records);
            }
        }

        private Dictionary<long, FailureRecord> Load()
        {
            if (_records != null) return _records;

            _records = new Dictionary<long, FailureRecord>();
            try
            {
                if (File.Exists(_path))
                {
                    var list = _json.DeserializeFromFile<List<FailureRecord>>(_path);
                    if (list != null)
                    {
                        foreach (var r in list) _records[r.ItemId] = r;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Could not read {0}; starting with an empty failure list", ex, _path);
            }

            return _records;
        }

        private void Save(Dictionary<long, FailureRecord> records)
        {
            try
            {
                _json.SerializeToFile(new List<FailureRecord>(records.Values), _path);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Could not write {0}", ex, _path);
            }
        }
    }
}

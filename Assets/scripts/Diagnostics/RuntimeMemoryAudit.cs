using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;

namespace Diagnostics
{
    /// <summary>
    /// Captures snapshots of Unity object memory usage (native + managed estimates) and writes CSVs.
    /// Helpful on device: press the configured key to dump totals by type and top offenders.
    /// </summary>
    public sealed class RuntimeMemoryAudit : MonoBehaviour
    {
        [SerializeField] KeyCode dumpKey = KeyCode.F12;
        [SerializeField, Range(5, 200)] int topCount = 40;
        [SerializeField] bool includeSceneObjectsOnly = false;

        Snapshot _previous;

        void Update()
        {
            if (Input.GetKeyDown(dumpKey))
            {
                var snapshot = CaptureSnapshot();
                var dumpPath = WriteSnapshotCsv(snapshot);
                Debug.Log($"[RuntimeMemoryAudit] Snapshot saved: {dumpPath}");

                if (_previous != null)
                {
                    var diffPath = WriteDiffCsv(_previous, snapshot);
                    Debug.Log($"[RuntimeMemoryAudit] Diff saved: {diffPath}");
                }

               _previous = snapshot;
            }
        }

        Snapshot CaptureSnapshot()
        {
            var objects = Resources.FindObjectsOfTypeAll<UnityEngine.Object>();
            var entries = new List<MemEntry>(objects.Length);

            foreach (var obj in objects)
            {
                if (!obj) continue;
                if (includeSceneObjectsOnly && obj is GameObject go && !go.scene.IsValid()) continue;

                long size = Profiler.GetRuntimeMemorySizeLong(obj);
                if (size <= 0) continue;

                entries.Add(new MemEntry
                {
                    type = obj.GetType().Name,
                    name = obj.name,
                    bytes = size
                });
            }

            var byType = entries.GroupBy(e => e.type)
                                .Select(g => new TypeAggregate
                                {
                                    type = g.Key,
                                    count = g.Count(),
                                    bytes = g.Sum(e => e.bytes)
                                })
                                .OrderByDescending(a => a.bytes)
                                .ToList();

            var top = entries.OrderByDescending(e => e.bytes)
                             .Take(topCount)
                             .ToList();

            return new Snapshot
            {
                time = DateTime.Now,
                totalBytes = entries.Sum(e => e.bytes),
                byType = byType,
                topEntries = top
            };
        }

        string WriteSnapshotCsv(Snapshot snapshot)
        {
            var sb = new StringBuilder(1 << 16);
            sb.AppendLine($"Snapshot,{snapshot.time:yyyy-MM-dd HH:mm:ss},Total,{FormatBytes(snapshot.totalBytes)}");
            sb.AppendLine("== By Type ==");
            sb.AppendLine("Type,Count,Bytes,MB");
            foreach (var type in snapshot.byType)
            {
                sb.AppendLine($"{type.type},{type.count},{type.bytes},{type.bytes / 1048576f:0.00}");
            }
            sb.AppendLine();
            sb.AppendLine("== Top Objects ==");
            sb.AppendLine("Type,Name,Bytes,MB");
            foreach (var entry in snapshot.topEntries)
            {
                sb.AppendLine($"{entry.type},{Sanitise(entry.name)},{entry.bytes},{entry.bytes / 1048576f:0.00}");
            }

            string path = Path.Combine(Application.persistentDataPath, $"mem_{snapshot.time:yyyyMMdd_HHmmss}.csv");
            File.WriteAllText(path, sb.ToString());
            return path;
        }

        string WriteDiffCsv(Snapshot previous, Snapshot current)
        {
            var prevMap = previous.byType.ToDictionary(t => t.type, t => t);
            var currMap = current.byType.ToDictionary(t => t.type, t => t);
            var allKeys = new HashSet<string>(prevMap.Keys);
            foreach (var key in currMap.Keys) allKeys.Add(key);

            var sb = new StringBuilder(1 << 16);
            sb.AppendLine($"Diff,{previous.time:HH:mm:ss} -> {current.time:HH:mm:ss},ΔTotal,{FormatBytes(current.totalBytes - previous.totalBytes)}");
            sb.AppendLine("Type,ΔCount,ΔBytes,ΔMB");
            foreach (var key in allKeys.OrderBy(k => k))
            {
                var prev = prevMap.TryGetValue(key, out var p) ? p : TypeAggregate.Zero;
                var curr = currMap.TryGetValue(key, out var c) ? c : TypeAggregate.Zero;
                long deltaBytes = curr.bytes - prev.bytes;
                int deltaCount = curr.count - prev.count;
                if (deltaBytes == 0 && deltaCount == 0) continue;
                sb.AppendLine($"{key},{deltaCount},{deltaBytes},{deltaBytes / 1048576f:0.00}");
            }

            string path = Path.Combine(Application.persistentDataPath, $"memdiff_{current.time:yyyyMMdd_HHmmss}.csv");
            File.WriteAllText(path, sb.ToString());
            return path;
        }

        static string FormatBytes(long bytes) => $"{bytes / 1048576f:0.00} MB";

        static string Sanitise(string input) => string.IsNullOrEmpty(input) ? "" : input.Replace(",", "_");

        sealed class MemEntry
        {
            public string type;
            public string name;
            public long bytes;
        }

        sealed class TypeAggregate
        {
            public static readonly TypeAggregate Zero = new TypeAggregate();

            public string type;
            public int count;
            public long bytes;
        }

        sealed class Snapshot
        {
            public DateTime time;
            public long totalBytes;
            public List<TypeAggregate> byType;
            public List<MemEntry> topEntries;
        }
    }
}

using ApeFree.DonateDisk.Contracts;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace ApeFree.DonateDisk.Master
{
    /// <summary>
    /// Master 端文件与目录元数据短期高速缓存（TTL Cache）。
    /// 拦截 Windows Explorer 和应用程序对同一路径密集重复的属性查询，大幅减少 RPC 远程往返。
    /// </summary>
    public class MetadataCache
    {
        private class CacheEntry
        {
            public DiskItemInfoDto Item { get; set; } = null!;
            public long ExpireTime { get; set; }
        }

        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new ConcurrentDictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly int _ttlMilliseconds;

        /// <summary>
        /// 初始化元数据缓存
        /// </summary>
        /// <param name="ttlMilliseconds">缓存生存时间（毫秒，默认 2500 毫秒）</param>
        public MetadataCache(int ttlMilliseconds = 2500)
        {
            _ttlMilliseconds = ttlMilliseconds;
        }

        private static long CurrentTimestamp => Stopwatch.GetTimestamp();
        private long GetExpireTime() => CurrentTimestamp + (_ttlMilliseconds * Stopwatch.Frequency / 1000);

        private static string NormalizeKey(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "\\";
            return path.Replace('/', '\\').TrimEnd('\\');
        }

        /// <summary>
        /// 尝试从缓存中获取元数据
        /// </summary>
        public bool TryGet(string path, out DiskItemInfoDto? item)
        {
            var key = NormalizeKey(path);
            if (_cache.TryGetValue(key, out var entry))
            {
                if (CurrentTimestamp <= entry.ExpireTime)
                {
                    item = entry.Item;
                    return true;
                }
                _cache.TryRemove(key, out _);
            }

            item = null;
            return false;
        }

        /// <summary>
        /// 写入单条路径元数据到缓存
        /// </summary>
        public void Set(string path, DiskItemInfoDto item)
        {
            if (item == null) return;
            var key = NormalizeKey(path);
            _cache[key] = new CacheEntry
            {
                Item = item,
                ExpireTime = GetExpireTime()
            };
        }

        /// <summary>
        /// 批量缓存指定目录下的子项列表
        /// </summary>
        public void SetBatch(string parentPath, IEnumerable<DiskItemInfoDto> items)
        {
            if (items == null) return;
            var baseKey = NormalizeKey(parentPath);
            var expire = GetExpireTime();

            foreach (var item in items)
            {
                var fullKey = baseKey == "\\" ? $"\\{item.Name}" : $"{baseKey}\\{item.Name}";
                _cache[NormalizeKey(fullKey)] = new CacheEntry
                {
                    Item = item,
                    ExpireTime = expire
                };
            }
        }

        /// <summary>
        /// 主动失效指定路径的缓存（如写、删、移后）
        /// </summary>
        public void Invalidate(string path)
        {
            var key = NormalizeKey(path);
            _cache.TryRemove(key, out _);

            // 同时失效子项
            var prefix = key + "\\";
            foreach (var k in _cache.Keys)
            {
                if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    _cache.TryRemove(k, out _);
                }
            }
        }

        /// <summary>
        /// 清空所有缓存
        /// </summary>
        public void Clear()
        {
            _cache.Clear();
        }
    }
}

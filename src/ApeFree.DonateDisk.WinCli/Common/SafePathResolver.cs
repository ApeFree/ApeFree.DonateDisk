using System;
using System.IO;

namespace ApeFree.DonateDisk.Common
{
    /// <summary>
    /// 路径安全沙箱解析器，防止目录穿越（Directory Traversal）攻击并提供路径规范化。
    /// </summary>
    public class SafePathResolver
    {
        private readonly string _rootDirectory;

        /// <summary>
        /// 获取当前共享根目录的绝对物理路径
        /// </summary>
        public string RootDirectory => _rootDirectory;

        /// <summary>
        /// 初始化路径安全沙箱解析器
        /// </summary>
        /// <param name="rootDirectory">本地待共享的物理根目录</param>
        public SafePathResolver(string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
            {
                throw new ArgumentException("根目录路径不能为空。", nameof(rootDirectory));
            }

            var fullRoot = Path.GetFullPath(rootDirectory);
            if (!Directory.Exists(fullRoot))
            {
                Directory.CreateDirectory(fullRoot);
            }

            // 统一在尾部带上路径分隔符以精确判定前缀包含
            _rootDirectory = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                             + Path.DirectorySeparatorChar;
        }

        /// <summary>
        /// 将客户端传来的相对路径解析为安全的本地绝对物理路径。
        /// 若检测到越界或目录穿越，将抛出 <see cref="UnauthorizedAccessException"/>。
        /// </summary>
        /// <param name="relativePath">客户端传来的相对路径（例如 "/" 或 @"\SubDir\file.txt"）</param>
        /// <returns>安全的本地物理绝对路径</returns>
        public string ResolveFullPath(string? relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return _rootDirectory.TrimEnd(Path.DirectorySeparatorChar);
            }

            var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar)
                                         .Trim(Path.DirectorySeparatorChar);

            if (string.IsNullOrEmpty(normalized))
            {
                return _rootDirectory.TrimEnd(Path.DirectorySeparatorChar);
            }

            var combined = Path.GetFullPath(Path.Combine(_rootDirectory, normalized));

            // 安全沙箱校验：解析后的物理全路径必须以 _rootDirectory 开头
            if (!combined.StartsWith(_rootDirectory, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(combined, _rootDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException($"拒绝访问：试图越界访问共享沙箱外路径 '{relativePath}'。");
            }

            return combined;
        }

        /// <summary>
        /// 将本地绝对物理路径转换为相对于根目录的相对路径
        /// </summary>
        /// <param name="fullPath">物理绝对路径</param>
        /// <returns>以反斜杠开头的规范相对路径</returns>
        public string GetRelativePath(string fullPath)
        {
            var normalizedFull = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var trimmedRoot = _rootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(normalizedFull, trimmedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return Path.DirectorySeparatorChar.ToString();
            }

            if (normalizedFull.StartsWith(_rootDirectory, StringComparison.OrdinalIgnoreCase))
            {
                var rel = normalizedFull.Substring(_rootDirectory.Length);
                return Path.DirectorySeparatorChar + rel;
            }

            throw new ArgumentException($"路径 '{fullPath}' 不位于共享根目录下。", nameof(fullPath));
        }
    }
}

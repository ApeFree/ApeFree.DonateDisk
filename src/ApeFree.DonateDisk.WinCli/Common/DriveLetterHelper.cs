using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ApeFree.DonateDisk.Common
{
    /// <summary>
    /// Windows 盘符探测与分配帮助类
    /// </summary>
    public static class DriveLetterHelper
    {
        /// <summary>
        /// 获取当前系统中所有已被占用的逻辑驱动器盘符（大写字符，例如 'C', 'D'）
        /// </summary>
        public static HashSet<char> GetUsedDriveLetters()
        {
            var used = new HashSet<char>();
            try
            {
                var drives = DriveInfo.GetDrives();
                foreach (var d in drives)
                {
                    if (!string.IsNullOrEmpty(d.Name))
                    {
                        used.Add(char.ToUpperInvariant(d.Name[0]));
                    }
                }
            }
            catch
            {
                // 若由于安全策略无法读取部分驱动器，忽略异常
            }

            return used;
        }

        /// <summary>
        /// 自动寻找可用的盘符。
        /// 优先尝试 preferredLetter（缺省为 'Z'），若已被占用则从 'Z' 递减至 'D' 寻找首个空闲盘符。
        /// </summary>
        /// <param name="preferredLetter">首选盘符字符（例如 'Z'）</param>
        /// <param name="excludeLetters">需要额外排除的盘符集合（如已被其他挂载占用的盘符）</param>
        /// <returns>找到的可用盘符；若无可用盘符则返回 null</returns>
        public static char? FindAvailableDriveLetter(char? preferredLetter = 'Z', IEnumerable<char>? excludeLetters = null)
        {
            var used = GetUsedDriveLetters();
            if (excludeLetters != null)
            {
                foreach (var letter in excludeLetters)
                {
                    used.Add(char.ToUpperInvariant(letter));
                }
            }

            if (preferredLetter.HasValue)
            {
                var pref = char.ToUpperInvariant(preferredLetter.Value);
                if (pref >= 'D' && pref <= 'Z' && !used.Contains(pref))
                {
                    return pref;
                }
            }

            // 从 Z 倒序搜索到 D
            for (char c = 'Z'; c >= 'D'; c--)
            {
                if (!used.Contains(c))
                {
                    return c;
                }
            }

            return null;
        }

        /// <summary>
        /// 格式化盘符为挂载点字符串（如 "Z:" 或 "Z:\"）
        /// </summary>
        /// <param name="letter">盘符字符</param>
        /// <param name="withTrailingSlash">是否带反斜杠</param>
        public static string FormatMountPoint(char letter, bool withTrailingSlash = true)
        {
            var upper = char.ToUpperInvariant(letter);
            return withTrailingSlash ? $"{upper}:\\" : $"{upper}:";
        }
    }
}

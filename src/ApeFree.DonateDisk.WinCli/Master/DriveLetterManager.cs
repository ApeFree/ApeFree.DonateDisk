using ApeFree.DonateDisk.Common;
using System;
using System.Collections.Generic;

namespace ApeFree.DonateDisk.Master
{
    /// <summary>
    /// 盘符动态生命周期分配管理器
    /// </summary>
    public class DriveLetterManager
    {
        private readonly object _lock = new object();
        private readonly HashSet<char> _allocatedLetters = new HashSet<char>();

        /// <summary>
        /// 获取所有已被本管理器分配占用的盘符列表
        /// </summary>
        public IReadOnlyCollection<char> AllocatedLetters
        {
            get
            {
                lock (_lock)
                {
                    return new List<char>(_allocatedLetters);
                }
            }
        }

        /// <summary>
        /// 分配一个可用盘符
        /// </summary>
        /// <param name="preferredLetter">首选盘符字符（如 'Z'）</param>
        /// <returns>分配到的盘符字符；若系统无可用盘符则返回 null</returns>
        public char? Allocate(char? preferredLetter = 'Z')
        {
            lock (_lock)
            {
                var letter = DriveLetterHelper.FindAvailableDriveLetter(preferredLetter, _allocatedLetters);
                if (letter.HasValue)
                {
                    _allocatedLetters.Add(letter.Value);
                }
                return letter;
            }
        }

        /// <summary>
        /// 释放已占用的盘符
        /// </summary>
        /// <param name="letter">盘符字符</param>
        public void Release(char letter)
        {
            lock (_lock)
            {
                _allocatedLetters.Remove(char.ToUpperInvariant(letter));
            }
        }
    }
}

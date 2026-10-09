using System;
using System.IO;

namespace ApeFree.DonateDisk.Slave
{
    /// <summary>
    /// 维护已打开物理文件流的会话上下文
    /// </summary>
    public class FileStreamSession : IDisposable
    {
        private readonly object _lock = new object();
        private bool _isDisposed;

        /// <summary>
        /// 句柄唯一标识符
        /// </summary>
        public long HandleId { get; }

        /// <summary>
        /// 关联的相对路径
        /// </summary>
        public string RelativePath { get; }

        /// <summary>
        /// 物理文件流
        /// </summary>
        public FileStream Stream { get; }

        /// <summary>
        /// 最后一次活跃时间
        /// </summary>
        public DateTime LastAccessTime { get; private set; }

        /// <summary>
        /// 初始化文件流会话
        /// </summary>
        /// <param name="handleId">句柄唯一标识</param>
        /// <param name="relativePath">相对路径</param>
        /// <param name="stream">物理文件流</param>
        public FileStreamSession(long handleId, string relativePath, FileStream stream)
        {
            HandleId = handleId;
            RelativePath = relativePath;
            Stream = stream ?? throw new ArgumentNullException(nameof(stream));
            LastAccessTime = DateTime.UtcNow;
        }

        /// <summary>
        /// 分块读取文件数据
        /// </summary>
        public byte[] ReadBlock(long offset, int count)
        {
            lock (_lock)
            {
                if (_isDisposed) throw new ObjectDisposedException(nameof(FileStreamSession));
                LastAccessTime = DateTime.UtcNow;

                if (offset >= Stream.Length || count <= 0)
                {
                    return Array.Empty<byte>();
                }

                Stream.Seek(offset, SeekOrigin.Begin);
                var buffer = new byte[count];
                int bytesRead = Stream.Read(buffer, 0, count);

                if (bytesRead == count)
                {
                    return buffer;
                }

                var result = new byte[bytesRead];
                Buffer.BlockCopy(buffer, 0, result, 0, bytesRead);
                return result;
            }
        }

        /// <summary>
        /// 分块写入文件数据
        /// </summary>
        public int WriteBlock(long offset, byte[] data)
        {
            lock (_lock)
            {
                if (_isDisposed) throw new ObjectDisposedException(nameof(FileStreamSession));
                LastAccessTime = DateTime.UtcNow;

                if (data == null || data.Length == 0) return 0;

                if (offset < 0)
                {
                    Stream.Seek(0, SeekOrigin.End);
                }
                else
                {
                    Stream.Seek(offset, SeekOrigin.Begin);
                }

                Stream.Write(data, 0, data.Length);
                return data.Length;
            }
        }

        /// <summary>
        /// 刷新流缓冲区
        /// </summary>
        public void Flush()
        {
            lock (_lock)
            {
                if (_isDisposed) return;
                LastAccessTime = DateTime.UtcNow;
                Stream.Flush();
            }
        }

        /// <summary>
        /// 截断或设置文件大小
        /// </summary>
        public void SetLength(long length)
        {
            lock (_lock)
            {
                if (_isDisposed) return;
                LastAccessTime = DateTime.UtcNow;
                Stream.SetLength(length);
            }
        }

        /// <summary>
        /// 释放文件流并刷新缓冲区
        /// </summary>
        public void Dispose()
        {
            lock (_lock)
            {
                if (_isDisposed) return;
                _isDisposed = true;
                try
                {
                    Stream.Flush();
                    Stream.Dispose();
                }
                catch
                {
                    // 忽略关闭流时的非关键异常
                }
            }
        }
    }
}

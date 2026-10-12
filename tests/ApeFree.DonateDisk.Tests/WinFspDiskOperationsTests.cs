using ApeFree.DonateDisk.Common;
using ApeFree.DonateDisk.Contracts;
using ApeFree.DonateDisk.Master;
using ApeFree.DonateDisk.Slave;
using Fsp;
using Fsp.Interop;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace ApeFree.DonateDisk.Tests
{
    public class WinFspDiskOperationsTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly SafePathResolver _resolver;
        private readonly DiskOperationsService _slaveService;
        private readonly MetadataCache _cache;
        private readonly WinFspDiskOperations _ops;

        public WinFspDiskOperationsTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "WinFspOpsTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _resolver = new SafePathResolver(_tempDir);
            _slaveService = new DiskOperationsService(_resolver, "TestVolume", isReadOnly: false);
            _cache = new MetadataCache();
            _ops = new WinFspDiskOperations(_slaveService, _cache, "TestVolume");
        }

        public void Dispose()
        {
            _ops.Dispose();
            _slaveService.Dispose();
            try
            {
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, true);
                }
            }
            catch { }
        }

        [Fact]
        public void RealDriveMountAndIO_WithWinFspHost_Succeeds()
        {
            // 找到空闲的驱动器盘符
            char freeLetter = '\0';
            for (char c = 'Z'; c >= 'D'; c--)
            {
                if (!Directory.Exists($"{c}:\\"))
                {
                    freeLetter = c;
                    break;
                }
            }

            Assert.True(freeLetter != '\0', "找不到可用的空闲驱动器盘符进行挂载测试");
            string mountPoint = $"{freeLetter}:";

            using var host = new FileSystemHost(_ops);
            var preflight = host.Preflight(mountPoint);
            Assert.Equal(0, preflight);

            var mountStatus = host.Mount(mountPoint);
            Assert.Equal(0, mountStatus);

            try
            {
                string driveRoot = $"{mountPoint}\\";
                Assert.True(Directory.Exists(driveRoot), $"挂载后驱动器根目录应可访问: {driveRoot}");

                // 通过 Windows 标准 I/O 在虚拟驱动器根目录下创建并写入文件
                string testFileOnDrive = Path.Combine(driveRoot, "real_drive_test.txt");
                File.WriteAllText(testFileOnDrive, "Hello from real WinFsp Windows drive mounting test!");

                // 读取验证
                string readContent = File.ReadAllText(testFileOnDrive);
                Assert.Equal("Hello from real WinFsp Windows drive mounting test!", readContent);

                // 验证 Slave 端物理文件夹中对应文件已生成
                string physicalFile = Path.Combine(_tempDir, "real_drive_test.txt");
                Assert.True(File.Exists(physicalFile), "Slave 物理目录中应存在由虚拟驱动器写入的文件");

                // 列举目录
                var driveFiles = Directory.GetFiles(driveRoot);
                Assert.Contains(driveFiles, f => f.EndsWith("real_drive_test.txt", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                host.Unmount();
            }

            // 卸载后验证
            Assert.False(Directory.Exists($"{mountPoint}\\"), $"卸载后驱动器盘符 {mountPoint}\\ 应不可访问");
        }

        [Fact]
        public void GetVolumeInfo_ReturnsSuccessAndCorrectSizes()
        {
            var status = _ops.GetVolumeInfo(out var volInfo);
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
            Assert.True(volInfo.TotalSize > 0);
            Assert.True(volInfo.FreeSize > 0);
        }

        [Fact]
        public void GetSecurityByName_Root_ReturnsDirectory()
        {
            byte[]? sd = null;
            var status = _ops.GetSecurityByName(@"\", out var attrs, ref sd!);
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
            Assert.True(((FileAttributes)attrs).HasFlag(FileAttributes.Directory));
        }

        [Fact]
        public void GetSecurityByName_MissingFile_ReturnsNotFound()
        {
            byte[]? sd = null;
            var status = _ops.GetSecurityByName(@"\missing.txt", out var attrs, ref sd!);
            Assert.Equal(FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND, status);
        }

        [Fact]
        public void Create_File_CreatesPhysicalFileAndSetsContext()
        {
            var status = _ops.Create(@"\newfile.txt", 0, 0, (uint)FileAttributes.Normal, null!, 0, out var node, out var desc, out var info, out var normName);
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
            Assert.NotNull(desc);
            Assert.True(desc is WinFspDiskOperations.FileContext ctx && ctx.HandleId > 0);

            _ops.Close(node, desc);
            Assert.True(File.Exists(Path.Combine(_tempDir, "newfile.txt")));
        }

        [Fact]
        public void Create_Directory_CreatesPhysicalDirectory()
        {
            var status = _ops.Create(@"\newdir", FileSystemBase.FILE_DIRECTORY_FILE, 0, (uint)FileAttributes.Directory, null!, 0, out var node, out var desc, out var info, out var normName);
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
            Assert.NotNull(desc);
            Assert.True(desc is WinFspDiskOperations.FileContext ctx && ctx.IsDirectory);

            _ops.Close(node, desc);
            Assert.True(Directory.Exists(Path.Combine(_tempDir, "newdir")));
        }

        [Fact]
        public void Open_MissingFile_ReturnsNotFound()
        {
            var status = _ops.Open(@"\missing.txt", 0, 0, out var node, out var desc, out var info, out var normName);
            Assert.Equal(FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND, status);
            Assert.Null(desc);
        }

        [Fact]
        public void Open_ExistingFile_ReturnsSuccess()
        {
            File.WriteAllText(Path.Combine(_tempDir, "existing.txt"), "hello");

            var status = _ops.Open(@"\existing.txt", 0, 0, out var node, out var desc, out var info, out var normName);
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);
            Assert.NotNull(desc);

            _ops.Close(node, desc);
        }

        [Fact]
        public void Write_And_Read_WorksAcrossSessions()
        {
            var createStatus = _ops.Create(@"\io.dat", 0, 0, (uint)FileAttributes.Normal, null!, 0, out var node, out var desc, out var info, out var normName);
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, createStatus);

            var writeData = Encoding.UTF8.GetBytes("Data block to write");
            var writePtr = Marshal.AllocHGlobal(writeData.Length);
            try
            {
                Marshal.Copy(writeData, 0, writePtr, writeData.Length);
                var writeStatus = _ops.Write(node, desc, writePtr, 0, (uint)writeData.Length, false, false, out var bytesWritten, out info);
                Assert.Equal(FileSystemBase.STATUS_SUCCESS, writeStatus);
                Assert.Equal((uint)writeData.Length, bytesWritten);

                var readPtr = Marshal.AllocHGlobal(writeData.Length);
                try
                {
                    var readStatus = _ops.Read(node, desc, readPtr, 0, (uint)writeData.Length, out var bytesRead);
                    Assert.Equal(FileSystemBase.STATUS_SUCCESS, readStatus);
                    Assert.Equal((uint)writeData.Length, bytesRead);

                    var readBuf = new byte[writeData.Length];
                    Marshal.Copy(readPtr, readBuf, 0, writeData.Length);
                    Assert.Equal(writeData, readBuf);
                }
                finally
                {
                    Marshal.FreeHGlobal(readPtr);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(writePtr);
            }

            _ops.Close(node, desc);
        }

        [Fact]
        public void SetFileSize_ResizesFile()
        {
            var createStatus = _ops.Create(@"\resize.bin", 0, 0, (uint)FileAttributes.Normal, null!, 0, out var node, out var desc, out var info, out var normName);
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, createStatus);

            var data = new byte[] { 1, 2, 3, 4, 5 };
            var ptr = Marshal.AllocHGlobal(data.Length);
            try
            {
                Marshal.Copy(data, 0, ptr, data.Length);
                _ops.Write(node, desc, ptr, 0, (uint)data.Length, false, false, out _, out info);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            var setStatus = _ops.SetFileSize(node, desc, 2, false, out info);
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, setStatus);

            _ops.Close(node, desc);

            var fileBytes = File.ReadAllBytes(Path.Combine(_tempDir, "resize.bin"));
            Assert.Equal(2, fileBytes.Length);
            Assert.Equal(new byte[] { 1, 2 }, fileBytes);
        }

        [Fact]
        public void CanDelete_And_Cleanup_RemovesFilePhysically()
        {
            var filePath = Path.Combine(_tempDir, "to_delete.txt");
            File.WriteAllText(filePath, "delete me");

            var openStatus = _ops.Open(@"\to_delete.txt", 0, 0x00010000 /* DELETE */, out var node, out var desc, out var info, out var normName);
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, openStatus);

            var canDeleteStatus = _ops.CanDelete(node, desc, @"\to_delete.txt");
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, canDeleteStatus);

            _ops.Cleanup(node, desc, @"\to_delete.txt", FileSystemBase.CleanupDelete);
            _ops.Close(node, desc);

            Assert.False(File.Exists(filePath));
        }

        [Fact]
        public void CanDelete_Directory_NonEmpty_ReturnsDirectoryNotEmpty()
        {
            var subDir = Path.Combine(_tempDir, "NonEmptyFolder");
            Directory.CreateDirectory(subDir);
            File.WriteAllText(Path.Combine(subDir, "file.txt"), "inside");

            var status = _ops.Open(@"\NonEmptyFolder", 0, 0, out var node, out var desc, out var info, out var normName);
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);

            var canDelStatus = _ops.CanDelete(node, desc, @"\NonEmptyFolder");
            Assert.Equal(FileSystemBase.STATUS_DIRECTORY_NOT_EMPTY, canDelStatus);

            _ops.Close(node, desc);
        }

        [Fact]
        public void CanDelete_Directory_Empty_SucceedsAndCleanupDeletesFolder()
        {
            var subDir = Path.Combine(_tempDir, "EmptyFolder");
            Directory.CreateDirectory(subDir);

            var status = _ops.Open(@"\EmptyFolder", 0, 0, out var node, out var desc, out var info, out var normName);
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);

            var canDelStatus = _ops.CanDelete(node, desc, @"\EmptyFolder");
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, canDelStatus);

            _ops.Cleanup(node, desc, @"\EmptyFolder", FileSystemBase.CleanupDelete);
            _ops.Close(node, desc);

            Assert.False(Directory.Exists(subDir));
        }

        [Fact]
        public void Rename_RenamesFileSuccessfully()
        {
            File.WriteAllText(Path.Combine(_tempDir, "source.txt"), "move this");

            var status = _ops.Open(@"\source.txt", 0, 0, out var node, out var desc, out var info, out var normName);
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);

            var moveStatus = _ops.Rename(node, desc, @"\source.txt", @"\dest.txt", true);
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, moveStatus);

            _ops.Close(node, desc);

            Assert.False(File.Exists(Path.Combine(_tempDir, "source.txt")));
            Assert.True(File.Exists(Path.Combine(_tempDir, "dest.txt")));
        }

        [Fact]
        public void Rename_Directory_Succeeds()
        {
            var oldPhysical = Path.Combine(_tempDir, "FolderA");
            var newPhysical = Path.Combine(_tempDir, "FolderB");
            Directory.CreateDirectory(oldPhysical);
            File.WriteAllText(Path.Combine(oldPhysical, "file.txt"), "hello world");

            var status = _ops.Open(@"\FolderA", 0, 0, out var node, out var desc, out var info, out var normName);
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);

            var moveStatus = _ops.Rename(node, desc, @"\FolderA", @"\FolderB", false);
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, moveStatus);

            _ops.Close(node, desc);

            Assert.False(Directory.Exists(oldPhysical));
            Assert.True(Directory.Exists(newPhysical));
            Assert.True(File.Exists(Path.Combine(newPhysical, "file.txt")));
        }

        [Fact]
        public void ReadDirectoryEntry_EnumeratesDotAndChildren()
        {
            File.WriteAllText(Path.Combine(_tempDir, "a.log"), "log a");
            File.WriteAllText(Path.Combine(_tempDir, "b.txt"), "text b");
            Directory.CreateDirectory(Path.Combine(_tempDir, "c_folder"));

            var status = _ops.Open(@"\", 0, 0, out var node, out var desc, out var info, out var normName);
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);

            object? ctx = null;
            var files = new System.Collections.Generic.List<string>();
            while (_ops.ReadDirectoryEntry(node, desc, null!, null!, ref ctx!, out var name, out var fInfo))
            {
                files.Add(name);
            }

            Assert.Contains(".", files);
            Assert.Contains("..", files);
            Assert.Contains("a.log", files);
            Assert.Contains("b.txt", files);
            Assert.Contains("c_folder", files);

            _ops.Close(node, desc);
        }

        [Fact]
        public void GetFileInfo_ReturnsCorrectMetadata()
        {
            var physicalPath = Path.Combine(_tempDir, "meta.dat");
            File.WriteAllBytes(physicalPath, new byte[] { 1, 2, 3, 4, 5, 6 });

            var status = _ops.Open(@"\meta.dat", 0, 0, out var node, out var desc, out var info, out var normName);
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, status);

            var getStatus = _ops.GetFileInfo(node, desc, out var fileInfo);
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, getStatus);
            Assert.Equal(6UL, fileInfo.FileSize);
            Assert.False(((FileAttributes)fileInfo.FileAttributes).HasFlag(FileAttributes.Directory));

            _ops.Close(node, desc);
        }

        [Fact]
        public async Task RemoteFileChanged_InvalidatesCacheAndTriggersEvent()
        {
            // Seed the cache with a fake entry
            _cache.Set(@"\cached_file.txt", new DiskItemInfoDto { Name = "cached_file.txt", Length = 100 });
            Assert.True(_cache.TryGet(@"\cached_file.txt", out _));

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ops.FileChanged += (s, e) =>
            {
                if (e.RelativePath.Contains("cached_file.txt"))
                {
                    tcs.TrySetResult(true);
                }
            };

            // Trigger FileChanged on the slave service
            File.WriteAllText(Path.Combine(_tempDir, "cached_file.txt"), "modified");

            // Allow watcher thread to fire
            var delayTask = Task.Delay(5000);
            var completedTask = await Task.WhenAny(tcs.Task, delayTask);

            Assert.True(completedTask == tcs.Task, "RemoteFileChanged 应在 5 秒内触发并通知 Master");
            // Cache entry should be invalidated
            Assert.False(_cache.TryGet(@"\cached_file.txt", out _));
        }
    }
}

using ApeFree.DonateDisk.Common;
using ApeFree.DonateDisk.Slave;
using System;
using System.IO;
using System.Text;
using Xunit;

namespace ApeFree.DonateDisk.Tests
{
    public class DiskOperationsServiceTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly SafePathResolver _resolver;
        private readonly DiskOperationsService _service;

        public DiskOperationsServiceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "ApeFree_DonateDisk_OpsTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _resolver = new SafePathResolver(_tempDir);
            _service = new DiskOperationsService(_resolver, "TestVolume", isReadOnly: false);
        }

        public void Dispose()
        {
            _service.Dispose();
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
        public void VolumeInfo_ReturnsConfiguredLabel()
        {
            var vol = _service.GetVolumeInfo();
            Assert.Equal("TestVolume", vol.VolumeLabel);
            Assert.Equal("NTFS", vol.FileSystemName);
        }

        [Fact]
        public void CreateFile_WriteBlock_ReadBlock_WorksCorrectly()
        {
            const string relativePath = @"\testfile.txt";
            var text = "Hello ApeFree.DonateDisk! 欢迎使用远程主动捐赠式虚拟磁盘系统。";
            var bytes = Encoding.UTF8.GetBytes(text);

            // Open / Create
            var openResult = _service.OpenFile(relativePath, (int)FileAccess.ReadWrite, (int)FileShare.ReadWrite, (int)FileMode.Create, 0, 0);
            Assert.Equal(0, openResult.NtStatusCode);
            Assert.True(openResult.HandleId > 0);

            // Write
            int written = _service.WriteBlock(openResult.HandleId, 0, bytes);
            Assert.Equal(bytes.Length, written);

            // Read
            var readData = _service.ReadBlock(openResult.HandleId, 0, bytes.Length);
            Assert.Equal(bytes, readData);

            // Close
            _service.CloseFile(openResult.HandleId);

            // Verify physical existence
            var fullPath = Path.Combine(_tempDir, "testfile.txt");
            Assert.True(File.Exists(fullPath));
            var physicalContent = File.ReadAllText(fullPath, Encoding.UTF8);
            Assert.Equal(text, physicalContent);
        }

        [Fact]
        public void CreateDirectory_And_FindFiles_WorksCorrectly()
        {
            _service.CreateDirectory(@"\SubDir");
            _service.CreateDirectory(@"\SubDir\ChildDir");

            // Create a file in SubDir
            var openResult = _service.OpenFile(@"\SubDir\file1.dat", (int)FileAccess.Write, (int)FileShare.None, (int)FileMode.Create, 0, 0);
            _service.WriteBlock(openResult.HandleId, 0, new byte[] { 1, 2, 3 });
            _service.CloseFile(openResult.HandleId);

            var items = _service.FindFiles(@"\SubDir");
            Assert.Equal(2, items.Count);
            Assert.Contains(items, x => x.Name == "ChildDir" && x.IsDirectory);
            Assert.Contains(items, x => x.Name == "file1.dat" && !x.IsDirectory && x.Length == 3);
        }

        [Fact]
        public void MoveFile_MovesSuccessfully()
        {
            var openResult = _service.OpenFile(@"\original.txt", (int)FileAccess.Write, (int)FileShare.None, (int)FileMode.Create, 0, 0);
            _service.WriteBlock(openResult.HandleId, 0, new byte[] { 10, 20 });
            _service.CloseFile(openResult.HandleId);

            _service.MoveFile(@"\original.txt", @"\renamed.txt", replaceExisting: false);

            Assert.False(File.Exists(Path.Combine(_tempDir, "original.txt")));
            Assert.True(File.Exists(Path.Combine(_tempDir, "renamed.txt")));
        }

        [Fact]
        public void ReadOnlyMode_BlocksWriteAndCreate()
        {
            var roService = new DiskOperationsService(_resolver, "ROVolume", isReadOnly: true);

            var openResult = roService.OpenFile(@"\cannot_write.txt", (int)FileAccess.Write, (int)FileShare.None, (int)FileMode.Create, 0, 0);
            Assert.NotEqual(0, openResult.NtStatusCode);
            Assert.Contains("只读", openResult.ErrorMessage ?? "");

            Assert.Throws<UnauthorizedAccessException>(() =>
            {
                roService.CreateDirectory(@"\forbidden_dir");
            });
        }

        [Fact]
        public void OpenFile_WithWin32AccessMasks_OpensSuccessfully()
        {
            // Win32 GenericRead (0x80000000) & GenericWrite (0x40000000)
            const int win32ReadWriteMask = unchecked((int)0xC0000000);
            var result = _service.OpenFile(@"\win32_mask.txt", win32ReadWriteMask, (int)FileShare.ReadWrite, (int)FileMode.Create, 0, 0);
            Assert.Equal(0, result.NtStatusCode);
            Assert.True(result.HandleId > 0);

            var written = _service.WriteBlock(result.HandleId, 0, new byte[] { 1, 2, 3 });
            Assert.Equal(3, written);
            _service.CloseFile(result.HandleId);

            // Win32 ReadData / ReadAttributes (0x120089)
            const int win32ReadMask = 0x120089;
            var readOpen = _service.OpenFile(@"\win32_mask.txt", win32ReadMask, (int)FileShare.ReadWrite, (int)FileMode.Open, 0, 0);
            Assert.Equal(0, readOpen.NtStatusCode);
            var readData = _service.ReadBlock(readOpen.HandleId, 0, 3);
            Assert.Equal(new byte[] { 1, 2, 3 }, readData);
            _service.CloseFile(readOpen.HandleId);
        }

        [Fact]
        public void WriteBlock_WithNegativeOffset_AppendsData()
        {
            var openResult = _service.OpenFile(@"\append_test.txt", (int)FileAccess.ReadWrite, (int)FileShare.ReadWrite, (int)FileMode.Create, 0, 0);
            _service.WriteBlock(openResult.HandleId, 0, Encoding.UTF8.GetBytes("Part1"));
            _service.WriteBlock(openResult.HandleId, -1, Encoding.UTF8.GetBytes("Part2"));
            _service.CloseFile(openResult.HandleId);

            var content = File.ReadAllText(Path.Combine(_tempDir, "append_test.txt"), Encoding.UTF8);
            Assert.Equal("Part1Part2", content);
        }

        [Fact]
        public void RootDirectory_Protection_ThrowsUnauthorizedAccess()
        {
            Assert.Throws<UnauthorizedAccessException>(() => _service.DeleteFile(@"\"));
            Assert.Throws<UnauthorizedAccessException>(() => _service.DeleteDirectory(@"\"));
            Assert.Throws<UnauthorizedAccessException>(() => _service.MoveFile(@"\", @"\renamed_root", replaceExisting: false));
        }

        [Fact]
        public async Task FileSystemWatcher_DetectsFileCreationAndModification()
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _service.FileChanged += (s, e) =>
            {
                if (e.RelativePath.Contains("watcher_test"))
                {
                    tcs.TrySetResult(true);
                }
            };

            // Create file physically on disk
            File.WriteAllText(Path.Combine(_tempDir, "watcher_test.txt"), "hello watcher");

            var delayTask = Task.Delay(5000);
            var completedTask = await Task.WhenAny(tcs.Task, delayTask);

            Assert.True(completedTask == tcs.Task, "FileSystemWatcher 应在 5 秒内监测到物理文件创建并触发 FileChanged 事件");
        }
    }
}

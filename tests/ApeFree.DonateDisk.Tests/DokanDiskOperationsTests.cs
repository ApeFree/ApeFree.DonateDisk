using ApeFree.DonateDisk.Common;
using ApeFree.DonateDisk.Contracts;
using ApeFree.DonateDisk.Master;
using ApeFree.DonateDisk.Slave;
using DokanNet;
using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace ApeFree.DonateDisk.Tests
{
    public class MockDokanFileInfo : IDokanFileInfo
    {
        public object? Context { get; set; }
        public int ProcessId => 1000;
        public bool IsDirectory { get; set; }
        public bool DeletePending { get; set; }
        public bool SynchronousIo => true;
        public bool NoCache => false;
        public bool PagingIo => false;
        public bool WriteToEndOfFile { get; set; }
        public System.Security.Principal.WindowsIdentity GetRequestor() => System.Security.Principal.WindowsIdentity.GetCurrent();
        public bool TryResetTimeout(int milliseconds) => true;
    }

    public class DokanDiskOperationsTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly SafePathResolver _resolver;
        private readonly DiskOperationsService _slaveService;
        private readonly MetadataCache _cache;
        private readonly DokanDiskOperations _ops;

        public DokanDiskOperationsTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "DokanOpsTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _resolver = new SafePathResolver(_tempDir);
            _slaveService = new DiskOperationsService(_resolver, "TestVolume", isReadOnly: false);
            _cache = new MetadataCache();
            _ops = new DokanDiskOperations(_slaveService, _cache, "TestVolume");
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
        public void CreateFile_Root_ReturnsSuccessAndSetsIsDirectory()
        {
            var info = new MockDokanFileInfo();
            var status = _ops.CreateFile(@"\", DokanNet.FileAccess.ReadData, FileShare.ReadWrite, FileMode.Open, 0, 0, info);
            Assert.Equal(DokanResult.Success, status);
            Assert.True(info.IsDirectory);
        }

        [Fact]
        public void CreateFile_FileModeCreateNew_CreatesFileAndSetsContext()
        {
            var info = new MockDokanFileInfo();
            var status = _ops.CreateFile(@"\newfile.txt", DokanNet.FileAccess.WriteData, FileShare.ReadWrite, FileMode.CreateNew, 0, 0, info);
            Assert.Equal(DokanResult.Success, status);
            Assert.NotNull(info.Context);
            Assert.True((long)info.Context > 0);

            _ops.CloseFile(@"\newfile.txt", info);
            Assert.Null(info.Context);
            Assert.True(File.Exists(Path.Combine(_tempDir, "newfile.txt")));
        }

        [Fact]
        public void CreateFile_FileModeCreateNew_WhenFileExists_ReturnsAlreadyExists()
        {
            File.WriteAllText(Path.Combine(_tempDir, "existing.txt"), "hello");

            var info = new MockDokanFileInfo();
            var status = _ops.CreateFile(@"\existing.txt", DokanNet.FileAccess.WriteData, FileShare.ReadWrite, FileMode.CreateNew, 0, 0, info);
            Assert.Equal(DokanResult.AlreadyExists, status);
        }

        [Fact]
        public void CreateFile_FileModeOpen_WhenFileMissing_ReturnsFileNotFound()
        {
            var info = new MockDokanFileInfo();
            var status = _ops.CreateFile(@"\missing.txt", DokanNet.FileAccess.ReadData, FileShare.ReadWrite, FileMode.Open, 0, 0, info);
            Assert.Equal(DokanResult.FileNotFound, status);
        }

        [Fact]
        public void CreateFile_FileModeOpenOrCreate_ExistingFile_ReturnsAlreadyExistsAndSetsContext()
        {
            File.WriteAllText(Path.Combine(_tempDir, "exist2.txt"), "content");

            var info = new MockDokanFileInfo();
            var status = _ops.CreateFile(@"\exist2.txt", DokanNet.FileAccess.ReadData | DokanNet.FileAccess.WriteData, FileShare.ReadWrite, FileMode.OpenOrCreate, 0, 0, info);
            Assert.Equal(DokanResult.AlreadyExists, status);
            Assert.NotNull(info.Context);

            _ops.CloseFile(@"\exist2.txt", info);
        }

        [Fact]
        public void CreateFile_FileModeOpenOrCreate_NonExistingFile_ReturnsSuccessAndSetsContext()
        {
            var info = new MockDokanFileInfo();
            var status = _ops.CreateFile(@"\created.txt", DokanNet.FileAccess.WriteData, FileShare.ReadWrite, FileMode.OpenOrCreate, 0, 0, info);
            Assert.Equal(DokanResult.Success, status);
            Assert.NotNull(info.Context);

            _ops.CloseFile(@"\created.txt", info);
            Assert.True(File.Exists(Path.Combine(_tempDir, "created.txt")));
        }

        [Fact]
        public void WriteFile_And_ReadFile_WorksAcrossSessions()
        {
            var info = new MockDokanFileInfo();
            var createStatus = _ops.CreateFile(@"\io.dat", DokanNet.FileAccess.ReadData | DokanNet.FileAccess.WriteData, FileShare.ReadWrite, FileMode.Create, 0, 0, info);
            Assert.Equal(DokanResult.Success, createStatus);

            var writeData = Encoding.UTF8.GetBytes("Data block to write");
            var writeStatus = _ops.WriteFile(@"\io.dat", writeData, out int bytesWritten, 0, info);
            Assert.Equal(DokanResult.Success, writeStatus);
            Assert.Equal(writeData.Length, bytesWritten);

            var readBuffer = new byte[writeData.Length];
            var readStatus = _ops.ReadFile(@"\io.dat", readBuffer, out int bytesRead, 0, info);
            Assert.Equal(DokanResult.Success, readStatus);
            Assert.Equal(writeData.Length, bytesRead);
            Assert.Equal(writeData, readBuffer);

            _ops.CloseFile(@"\io.dat", info);
        }

        [Fact]
        public void WriteFile_WithWriteToEndOfFile_AppendsCorrectly()
        {
            var info = new MockDokanFileInfo();
            _ops.CreateFile(@"\append.txt", DokanNet.FileAccess.ReadData | DokanNet.FileAccess.WriteData, FileShare.ReadWrite, FileMode.Create, 0, 0, info);

            var part1 = Encoding.UTF8.GetBytes("Part 1: Hello. ");
            _ops.WriteFile(@"\append.txt", part1, out _, 0, info);

            info.WriteToEndOfFile = true;
            var part2 = Encoding.UTF8.GetBytes("Part 2: World!");
            _ops.WriteFile(@"\append.txt", part2, out _, 0, info);

            _ops.CloseFile(@"\append.txt", info);

            var fullText = File.ReadAllText(Path.Combine(_tempDir, "append.txt"), Encoding.UTF8);
            Assert.Equal("Part 1: Hello. Part 2: World!", fullText);
        }

        [Fact]
        public void SetEndOfFile_ResizesFile()
        {
            var info = new MockDokanFileInfo();
            _ops.CreateFile(@"\resize.bin", DokanNet.FileAccess.ReadData | DokanNet.FileAccess.WriteData, FileShare.ReadWrite, FileMode.Create, 0, 0, info);
            _ops.WriteFile(@"\resize.bin", new byte[] { 1, 2, 3, 4, 5 }, out _, 0, info);

            var setStatus = _ops.SetEndOfFile(@"\resize.bin", 2, info);
            Assert.Equal(DokanResult.Success, setStatus);

            _ops.CloseFile(@"\resize.bin", info);

            var fileBytes = File.ReadAllBytes(Path.Combine(_tempDir, "resize.bin"));
            Assert.Equal(2, fileBytes.Length);
            Assert.Equal(new byte[] { 1, 2 }, fileBytes);
        }

        [Fact]
        public void DeleteFile_And_Cleanup_RemovesFilePhysically()
        {
            var filePath = Path.Combine(_tempDir, "to_delete.txt");
            File.WriteAllText(filePath, "delete me");

            var info = new MockDokanFileInfo();
            var openStatus = _ops.CreateFile(@"\to_delete.txt", DokanNet.FileAccess.Delete, FileShare.ReadWrite | FileShare.Delete, FileMode.Open, 0, 0, info);
            Assert.Equal(DokanResult.Success, openStatus);

            var delStatus = _ops.DeleteFile(@"\to_delete.txt", info);
            Assert.Equal(DokanResult.Success, delStatus);

            info.DeletePending = true;
            _ops.Cleanup(@"\to_delete.txt", info);
            _ops.CloseFile(@"\to_delete.txt", info);

            Assert.False(File.Exists(filePath));
        }

        [Fact]
        public void DeleteDirectory_NonEmpty_ReturnsDirectoryNotEmpty()
        {
            var subDir = Path.Combine(_tempDir, "NonEmptyFolder");
            Directory.CreateDirectory(subDir);
            File.WriteAllText(Path.Combine(subDir, "file.txt"), "inside");

            var info = new MockDokanFileInfo { IsDirectory = true };
            var delStatus = _ops.DeleteDirectory(@"\NonEmptyFolder", info);
            Assert.Equal(DokanResult.DirectoryNotEmpty, delStatus);
        }

        [Fact]
        public void DeleteDirectory_Empty_SucceedsAndCleanupDeletesFolder()
        {
            var subDir = Path.Combine(_tempDir, "EmptyFolder");
            Directory.CreateDirectory(subDir);

            var info = new MockDokanFileInfo { IsDirectory = true };
            var delStatus = _ops.DeleteDirectory(@"\EmptyFolder", info);
            Assert.Equal(DokanResult.Success, delStatus);

            info.DeletePending = true;
            _ops.Cleanup(@"\EmptyFolder", info);

            Assert.False(Directory.Exists(subDir));
        }

        [Fact]
        public void MoveFile_RenamesFileSuccessfully()
        {
            File.WriteAllText(Path.Combine(_tempDir, "source.txt"), "move this");

            var info = new MockDokanFileInfo();
            var moveStatus = _ops.MoveFile(@"\source.txt", @"\dest.txt", replace: true, info);
            Assert.Equal(DokanResult.Success, moveStatus);

            Assert.False(File.Exists(Path.Combine(_tempDir, "source.txt")));
            Assert.True(File.Exists(Path.Combine(_tempDir, "dest.txt")));
        }

        [Fact]
        public void FindFiles_And_FindFilesWithPattern_ReturnsAccurateFiles()
        {
            File.WriteAllText(Path.Combine(_tempDir, "a.log"), "log a");
            File.WriteAllText(Path.Combine(_tempDir, "b.txt"), "text b");
            Directory.CreateDirectory(Path.Combine(_tempDir, "c_folder"));

            var info = new MockDokanFileInfo { IsDirectory = true };
            var status = _ops.FindFiles(@"\", out var allFiles, info);
            Assert.Equal(DokanResult.Success, status);
            Assert.Equal(3, allFiles.Count);

            var patternStatus = _ops.FindFilesWithPattern(@"\", "*.log", out var logFiles, info);
            Assert.Equal(DokanResult.Success, patternStatus);
            Assert.Single(logFiles);
            Assert.Equal("a.log", logFiles[0].FileName);
        }

        [Fact]
        public void GetFileInformation_ReturnsCorrectMetadata()
        {
            var physicalPath = Path.Combine(_tempDir, "meta.dat");
            File.WriteAllBytes(physicalPath, new byte[] { 1, 2, 3, 4, 5, 6 });

            var info = new MockDokanFileInfo();
            var status = _ops.GetFileInformation(@"\meta.dat", out var fileInfo, info);
            Assert.Equal(DokanResult.Success, status);
            Assert.Equal("meta.dat", fileInfo.FileName);
            Assert.Equal(6, fileInfo.Length);
            Assert.False((fileInfo.Attributes & FileAttributes.Directory) == FileAttributes.Directory);
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

using ApeFree.DonateDisk.Common;
using System;
using System.IO;
using Xunit;

namespace ApeFree.DonateDisk.Tests
{
    public class SafePathResolverTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly SafePathResolver _resolver;

        public SafePathResolverTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "ApeFree_DonateDisk_Test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _resolver = new SafePathResolver(_tempDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, true);
                }
            }
            catch { }
        }

        [Theory]
        [InlineData("")]
        [InlineData("/")]
        [InlineData("\\")]
        [InlineData(null)]
        public void ResolveFullPath_RootPaths_ReturnsRootDirectory(string? input)
        {
            var resolved = _resolver.ResolveFullPath(input);
            Assert.Equal(_tempDir.TrimEnd(Path.DirectorySeparatorChar), resolved.TrimEnd(Path.DirectorySeparatorChar), ignoreCase: true);
        }

        [Fact]
        public void ResolveFullPath_ValidSubPath_ReturnsCombinedPath()
        {
            var resolved = _resolver.ResolveFullPath(@"\folder\file.txt");
            var expected = Path.Combine(_tempDir, "folder", "file.txt");
            Assert.Equal(expected, resolved, ignoreCase: true);
        }

        [Fact]
        public void ResolveFullPath_ForwardSlashes_NormalizesToBackslashes()
        {
            var resolved = _resolver.ResolveFullPath("/folder/sub/file.txt");
            var expected = Path.Combine(_tempDir, "folder", "sub", "file.txt");
            Assert.Equal(expected, resolved, ignoreCase: true);
        }

        [Theory]
        [InlineData(@"..\..\Windows\System32")]
        [InlineData(@"/../../etc/passwd")]
        [InlineData(@"folder\..\..\..\secret.txt")]
        public void ResolveFullPath_DirectoryTraversalAttack_ThrowsUnauthorizedAccessException(string maliciousPath)
        {
            Assert.Throws<UnauthorizedAccessException>(() =>
            {
                _resolver.ResolveFullPath(maliciousPath);
            });
        }

        [Fact]
        public void GetRelativePath_ValidPhysicalPath_ReturnsNormalizedRelative()
        {
            var physical = Path.Combine(_tempDir, "docs", "readme.txt");
            var relative = _resolver.GetRelativePath(physical);
            Assert.Equal(@"\docs\readme.txt", relative);
        }
    }
}

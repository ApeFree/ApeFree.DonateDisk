using ApeFree.DonateDisk.Contracts;
using ApeFree.DonateDisk.Master;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace ApeFree.DonateDisk.Tests
{
    public class MetadataCacheTests
    {
        [Fact]
        public void SetAndGet_WorksCorrectly()
        {
            var cache = new MetadataCache(ttlMilliseconds: 3000);
            var item = new DiskItemInfoDto
            {
                Name = "test.txt",
                Length = 1024,
                IsDirectory = false,
                Attributes = FileAttributes.Normal
            };

            cache.Set(@"\docs\test.txt", item);

            bool found = cache.TryGet(@"\docs\test.txt", out var cached);
            Assert.True(found);
            Assert.NotNull(cached);
            Assert.Equal("test.txt", cached.Name);
            Assert.Equal(1024, cached.Length);
        }

        [Fact]
        public void SetBatchAndInvalidate_WorksCorrectly()
        {
            var cache = new MetadataCache(ttlMilliseconds: 3000);
            var items = new List<DiskItemInfoDto>
            {
                new DiskItemInfoDto { Name = "file1.txt", Length = 100 },
                new DiskItemInfoDto { Name = "file2.txt", Length = 200 }
            };

            cache.SetBatch(@"\myfolder", items);

            Assert.True(cache.TryGet(@"\myfolder\file1.txt", out var item1));
            Assert.Equal("file1.txt", item1!.Name);

            // Invalidate parent folder
            cache.Invalidate(@"\myfolder");

            Assert.False(cache.TryGet(@"\myfolder\file1.txt", out _));
        }
    }
}

using ApeFree.DonateDisk.Common;
using System.Collections.Generic;
using Xunit;

namespace ApeFree.DonateDisk.Tests
{
    public class DriveLetterHelperTests
    {
        [Fact]
        public void GetUsedDriveLetters_ReturnsNonEmpty()
        {
            var used = DriveLetterHelper.GetUsedDriveLetters();
            Assert.NotEmpty(used);
            // System drive C is almost always used
            Assert.Contains('C', used);
        }

        [Fact]
        public void FindAvailableDriveLetter_ReturnsValidLetter()
        {
            var letter = DriveLetterHelper.FindAvailableDriveLetter('Z');
            Assert.NotNull(letter);
            Assert.True(letter.Value >= 'D' && letter.Value <= 'Z');
        }

        [Fact]
        public void FindAvailableDriveLetter_ExcludesSpecifiedLetters()
        {
            var exclude = new HashSet<char> { 'Z', 'Y', 'X' };
            var letter = DriveLetterHelper.FindAvailableDriveLetter('Z', exclude);
            Assert.NotNull(letter);
            Assert.DoesNotContain(letter.Value, exclude);
        }

        [Theory]
        [InlineData('Z', true, "Z:\\")]
        [InlineData('z', true, "Z:\\")]
        [InlineData('D', false, "D:")]
        public void FormatMountPoint_ReturnsFormattedString(char letter, bool trailing, string expected)
        {
            var actual = DriveLetterHelper.FormatMountPoint(letter, trailing);
            Assert.Equal(expected, actual);
        }
    }
}

using TwitchDownloaderCore.Services;

namespace TwitchDownloaderCore.Tests.ServiceTests
{
    public class FilenameServiceTests
    {
        [Theory]
        [InlineData("\"", "＂")]
        [InlineData("*", "＊")]
        [InlineData(":", "：")]
        [InlineData("<", "＜")]
        [InlineData(">", "＞")]
        [InlineData("?", "？")]
        [InlineData("|", "｜")]
        [InlineData("/", "／")]
        [InlineData("\\", "＼")]
        [InlineData("\0", "_")]
        public void CorrectlyReplacesInvalidFilenameCharacters(string str, string expected)
        {
            var actual = FilenameService.ReplaceInvalidFilenameChars(str);

            Assert.Equal(expected, actual);
        }

        [Fact]
        public void ReplaceInvalidFilenameCharactersDoesNotThrow_WhenNullInput()
        {
            const string? STR = null;
            const string? EXPECTED = null;

            var actual = FilenameService.ReplaceInvalidFilenameChars(STR);

            Assert.Equal(EXPECTED, actual);
        }

        [Fact]
        public void GetNonCollidingNameWorks_WhenNoCollisionExists()
        {
            var expected = Path.Combine(Path.GetTempPath(), "foo.txt");
            var path = Path.Combine(Path.GetTempPath(), "foo.txt");
            var fileInfo = new FileInfo(path);

            try
            {
                var actual = FilenameService.GetNonCollidingName(fileInfo);

                Assert.Equal(expected, actual.FullName);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void GetNonCollidingNameWorks_WhenCollisionExists()
        {
            var expected = Path.Combine(Path.GetTempPath(), "foo (1).txt");
            var path = Path.Combine(Path.GetTempPath(), "foo.txt");
            var fileInfo = new FileInfo(path);

            try
            {
                fileInfo.Create().Close();

                var actual = FilenameService.GetNonCollidingName(fileInfo);

                Assert.Equal(expected, actual.FullName);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
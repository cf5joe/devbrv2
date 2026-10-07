using DevBR.Application.Archive;
using DevBR.Archive;

namespace DevBR.Tests.Archive;

public sealed class ArchivePathValidatorTests
{
    [Theory]
    [InlineData("manifest.json", "manifest.json")]
    [InlineData("payload/vscode/settings.json", @"payload\vscode\settings.json")]
    [InlineData("payload/unicode/Привет мир — 世界.txt", @"payload\unicode\Привет мир — 世界.txt")]
    [InlineData("payload/with spaces/a.b.c", @"payload\with spaces\a.b.c")]
    public void Accepts_safe_relative_paths(string input, string expected)
        => Assert.Equal(expected, ArchivePathValidator.Normalize(input));

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData(@"payload\..\..\evil.txt")]
    [InlineData("./manifest.json")]
    [InlineData(@"C:\Windows\evil.dll")]
    [InlineData("C:evil.txt")]
    [InlineData(@"\evil.txt")]
    [InlineData("/evil.txt")]
    [InlineData(@"\\server\share\evil.txt")]
    [InlineData("payload/file.txt:hidden")]
    [InlineData("payload//double")]
    [InlineData("payload/CON")]
    [InlineData("payload/nul.txt")]
    [InlineData("payload/COM1.log")]
    [InlineData("payload/LPT¹")]
    [InlineData("payload/trailing.")]
    [InlineData("payload/trailing ")]
    [InlineData("payload/a\u0001b")]
    [InlineData("payload/a*b")]
    [InlineData("")]
    public void Rejects_unsafe_paths(string input)
    {
        var error = Assert.Throws<ArchiveException>(() => ArchivePathValidator.Normalize(input));
        Assert.Equal(ArchiveErrorKind.UnsafeEntryPath, error.Kind);
    }

    [Fact]
    public void Resolved_paths_stay_under_the_destination()
    {
        using var temp = new TempDirectory();
        var resolved = ArchivePathValidator.ResolveUnder(temp.Path, @"payload\a.txt");
        Assert.StartsWith(temp.Path + Path.DirectorySeparatorChar, resolved, StringComparison.OrdinalIgnoreCase);
    }
}

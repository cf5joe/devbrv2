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

    [Theory]
    // Traversal with mixed separators and in every position.
    [InlineData(@"payload/..\..\evil.txt")]
    [InlineData(@"payload\sub/../../../evil.txt")]
    [InlineData("payload/..")]
    [InlineData("..")]
    [InlineData(@"..\")]
    [InlineData("payload/./a.txt")]
    [InlineData("payload/../payload/a.txt")]
    // Drive letters, roots and device or long-path prefixes.
    [InlineData("C:/Windows/evil.dll")]
    [InlineData("c:")]
    [InlineData("Z:payload")]
    [InlineData(@"\\?\C:\evil.txt")]
    [InlineData(@"\\?\UNC\server\share\evil.txt")]
    [InlineData(@"\\.\pipe\evil")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData("//server/share/evil.txt")]
    [InlineData(@"/\server\share")]
    // Alternate data streams.
    [InlineData("payload/file.txt::$DATA")]
    [InlineData("payload/folder:stream/file.txt")]
    [InlineData("payload/file.txt:Zone.Identifier:$DATA")]
    // Reserved device names: any case, with extensions, trailing dots or spaces.
    [InlineData("con")]
    [InlineData("payload/Con.txt")]
    [InlineData("payload/NUL.tar.gz")]
    [InlineData("payload/nul /x.txt")]
    [InlineData("payload/CON .txt")]
    [InlineData("payload/AUX.")]
    [InlineData("payload/PRN ")]
    [InlineData("payload/com9.json")]
    [InlineData("payload/LPT1/inner.txt")]
    [InlineData("payload/CONIN$")]
    [InlineData("payload/conout$.log")]
    [InlineData("payload/COM²")]
    [InlineData("payload/CLOCK$")]
    // Trailing dots and spaces Windows would silently strip.
    [InlineData("payload/folder./a.txt")]
    [InlineData("payload/folder /a.txt")]
    [InlineData("payload/...")]
    // Other characters Windows refuses or that alter path parsing.
    [InlineData("payload/a?b")]
    [InlineData("payload/a<b>")]
    [InlineData("payload/a|b")]
    [InlineData("payload/a\"b")]
    [InlineData("payload/a\0b")]
    [InlineData("payload/line\nbreak")]
    [InlineData(@"payload\\double")]
    [InlineData("payload/trailing/")]
    public void Rejects_hostile_entry_names(string input)
    {
        Assert.False(ArchivePathValidator.TryNormalize(input, out var normalized, out var reason));
        Assert.Empty(normalized);
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void Overlong_paths_and_segments_are_rejected()
    {
        Assert.False(ArchivePathValidator.TryNormalize("payload/" + new string('a', 256), out _, out _));
        Assert.False(ArchivePathValidator.TryNormalize(string.Join('/', Enumerable.Repeat("abcdefgh", 600)), out _, out _));
    }

    [Theory]
    // Percent-encoded or look-alike separators are ordinary characters on Windows; they must stay inside the root.
    [InlineData("payload/%2e%2e/%2e%2e/evil.txt")]
    [InlineData("payload/..%5c..%5cevil.txt")]
    [InlineData("payload/a\u2215..\u2215evil.txt")]
    [InlineData("payload/\uFF0E\uFF0E/evil.txt")]
    [InlineData("payload/CONSOLE.txt")]
    [InlineData("payload/COM10")]
    [InlineData("payload/.hidden")]
    public void Encoded_or_lookalike_names_are_literal_and_stay_inside_the_root(string input)
    {
        using var temp = new TempDirectory();
        var normalized = ArchivePathValidator.Normalize(input);
        var resolved = ArchivePathValidator.ResolveUnder(temp.Path, normalized);
        Assert.StartsWith(temp.Path + Path.DirectorySeparatorChar, resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_under_refuses_paths_that_escape_the_destination()
    {
        using var temp = new TempDirectory();
        Assert.Equal(ArchiveErrorKind.UnsafeEntryPath, Assert.Throws<ArchiveException>(() => ArchivePathValidator.ResolveUnder(temp.Path, @"..\escape.txt")).Kind);
        Assert.Equal(ArchiveErrorKind.UnsafeEntryPath, Assert.Throws<ArchiveException>(() => ArchivePathValidator.ResolveUnder(temp.Path, @"C:\Windows\x.dll")).Kind);

        // A sibling folder that merely shares the destination's name as a prefix is outside it.
        Assert.Throws<ArchiveException>(() => ArchivePathValidator.ResolveUnder(temp.Path, $@"..\{Path.GetFileName(temp.Path)}-evil\x.txt"));
    }

    [Fact]
    public void Rejection_messages_never_echo_control_characters_or_unbounded_input()
    {
        var hostile = "payload/" + new string('a', 300) + "\u001b[31m:evil";
        var error = Assert.Throws<ArchiveException>(() => ArchivePathValidator.Normalize(hostile));
        Assert.DoesNotContain('\u001b', error.Message);
        Assert.True(error.Message.Length < 400, $"Message length {error.Message.Length}");
    }

    [Fact]
    public void Resolved_paths_stay_under_the_destination()
    {
        using var temp = new TempDirectory();
        var resolved = ArchivePathValidator.ResolveUnder(temp.Path, @"payload\a.txt");
        Assert.StartsWith(temp.Path + Path.DirectorySeparatorChar, resolved, StringComparison.OrdinalIgnoreCase);
    }
}

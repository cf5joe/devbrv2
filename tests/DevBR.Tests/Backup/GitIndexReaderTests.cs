using System.Diagnostics;
using DevBR.Backup.Capture;
using DevBR.Simulation;

namespace DevBR.Tests.Backup;

public sealed class GitIndexReaderTests
{
    [Fact]
    public void Reads_indexes_written_for_simulated_repositories()
    {
        var tracked = GitIndexReader.Parse(GitIndexWriter.Create(["README.md", "bin/build.sh", "src/deep/very/long/path/name.cs"]));
        Assert.NotNull(tracked);
        Assert.Equal(3, tracked.Count);
        Assert.True(GitIndexReader.ContainsTrackedContent(tracked, @"bin"));
        Assert.False(GitIndexReader.ContainsTrackedContent(tracked, @"obj"));
        Assert.False(GitIndexReader.ContainsTrackedContent(tracked, @"src\deep\very\long\path\name.cs\x"));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { (byte)'D', (byte)'I', (byte)'R', (byte)'C', 0, 0, 0, 9, 0, 0, 0, 1 })]
    [InlineData(new byte[] { (byte)'D', (byte)'I', (byte)'R', (byte)'C', 0, 0, 0, 2, 0, 0, 0, 5, 1, 2, 3 })]
    public void Malformed_indexes_are_rejected(byte[] data) => Assert.Null(GitIndexReader.Parse(data));

    /// <summary>Validates the reader against indexes produced by real Git, in versions 2 and 4.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void Reads_indexes_written_by_git(int version)
    {
        var git = FindGit();
        Assert.SkipWhen(git is null, "git is not installed");

        using var temp = new TempDirectory();
        string[] files = ["README.md", "bin/build.sh", "src/app/main.ts", "src/app/main.test.ts", "docs/Ünïcödé guide.md", "a/b/c/d/e/f/deep.txt"];
        foreach (var file in files)
        {
            temp.WriteFile(file, "content"u8.ToArray());
        }

        Run(git!, temp.Path, "init", "-q");
        Run(git!, temp.Path, "-c", "core.quotepath=off", "add", ".");
        Run(git!, temp.Path, "update-index", "--index-version", version.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var tracked = GitIndexReader.Parse(File.ReadAllBytes(Path.Combine(temp.Path, ".git", "index")));
        Assert.NotNull(tracked);
        Assert.Equal(files.Order(StringComparer.Ordinal), tracked.Order(StringComparer.Ordinal));
    }

    private static string? FindGit()
    {
        foreach (var candidate in new[] { @"C:\Program Files\Git\cmd\git.exe", @"C:\Program Files\Git\bin\git.exe" })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void Run(string git, string directory, params string[] args)
    {
        var start = new ProcessStartInfo(git) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }
}

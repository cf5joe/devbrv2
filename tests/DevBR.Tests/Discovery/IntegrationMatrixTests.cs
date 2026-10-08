using DevBR.Discovery;
using DevBR.Discovery.Adapters;

namespace DevBR.Tests.Discovery;

/// <summary>docs/INTEGRATIONS.md is generated from the adapters' support descriptors and must not drift from them.</summary>
public sealed class IntegrationMatrixTests
{
    [Fact]
    public void Integrations_document_matches_the_adapter_descriptors()
    {
        var path = Path.Combine(RepositoryRoot(), "docs", "INTEGRATIONS.md");
        var expected = IntegrationMatrix.Render(DiscoveryEngine.DefaultAdapters);
        var text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);

        var start = text.IndexOf(IntegrationMatrix.BeginMarker, StringComparison.Ordinal);
        var end = text.IndexOf(IntegrationMatrix.EndMarker, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"{path} has no generated integration matrix section.");
        var actual = text[start..(end + IntegrationMatrix.EndMarker.Length)];

        // Set DEVBR_UPDATE_DOCS=1 and run this test to regenerate the section after changing a descriptor.
        if (actual != expected && Environment.GetEnvironmentVariable("DEVBR_UPDATE_DOCS") == "1")
        {
            File.WriteAllText(path, (text[..start] + expected + text[(end + IntegrationMatrix.EndMarker.Length)..]).Replace("\n", "\r\n", StringComparison.Ordinal));
            return;
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Matrix_lists_every_adapter_with_its_verified_versions()
    {
        var matrix = IntegrationMatrix.Render(DiscoveryEngine.DefaultAdapters);
        Assert.All(DiscoveryEngine.DefaultAdapters, a => Assert.Contains($"| {a.DisplayName} (`{a.Id}`) | {a.Support.HostName} (`{a.Support.HostToolId}`) | {a.Support.VersionText} |", matrix, StringComparison.Ordinal));
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DevBR.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (DevBR.slnx) was not found above the test output folder.");
    }
}

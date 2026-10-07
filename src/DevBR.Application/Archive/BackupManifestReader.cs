using System.Text.Json;
using System.Text.Json.Serialization;
using DevBR.Domain;

namespace DevBR.Application.Archive;

public sealed record ManifestReadResult(BackupManifest? Manifest, string? Error);

/// <summary>Parses manifest.json from an untrusted archive as bounded data. Nothing in it is executed.</summary>
public static class BackupManifestReader
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static ManifestReadResult Parse(string json)
    {
        if (json.Length > ArchiveContract.MaxManifestBytes)
        {
            return new ManifestReadResult(null, "The backup manifest is larger than DevBR allows.");
        }

        // Check the format version first so a future major version is rejected with a clear message
        // instead of a schema error.
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("formatVersion", out var version) || version.ValueKind != JsonValueKind.Object ||
                !version.TryGetProperty("major", out var major) || major.ValueKind != JsonValueKind.Number ||
                !major.TryGetInt32(out var majorValue))
            {
                return new ManifestReadResult(null, "The backup manifest has no format version.");
            }

            if (majorValue != ArchiveContract.CurrentMajor)
            {
                return new ManifestReadResult(null, $"This backup uses archive format {majorValue}.x, which this version of DevBR cannot read (supported: {ArchiveContract.CurrentMajor}.x).");
            }

            var manifest = document.RootElement.Deserialize<BackupManifest>(Options);
            return manifest is null
                ? new ManifestReadResult(null, "The backup manifest is empty.")
                : new ManifestReadResult(manifest, null);
        }
        catch (JsonException ex)
        {
            return new ManifestReadResult(null, $"The backup manifest is malformed ({ex.Message}).");
        }
    }

    public static string Serialize(BackupManifest manifest) => JsonSerializer.Serialize(manifest, Options);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            MaxDepth = 16,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

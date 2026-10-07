using System.Text.Json;
using System.Text.Json.Serialization;
using DevBR.Application.Settings;
using Microsoft.Extensions.Logging;

namespace DevBR.Infrastructure.Settings;

public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AppPaths _paths;
    private readonly ILogger<JsonSettingsStore> _logger;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public JsonSettingsStore(AppPaths paths, ILogger<JsonSettingsStore> logger)
    {
        _paths = paths;
        _logger = logger;
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public event EventHandler<AppSettings>? Changed;

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var temp = _paths.SettingsPath + ".tmp";
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, settings, Options, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, _paths.SettingsPath, overwrite: true);
            Current = settings;
        }
        finally
        {
            _saveLock.Release();
        }

        Changed?.Invoke(this, settings);
    }

    private AppSettings Load()
    {
        if (!File.Exists(_paths.SettingsPath))
        {
            return AppSettings.Default;
        }

        try
        {
            var json = File.ReadAllBytes(_paths.SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? AppSettings.Default;
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Settings file is unreadable; using defaults and keeping a copy.");
            try
            {
                File.Copy(_paths.SettingsPath, _paths.SettingsPath + ".unreadable", overwrite: true);
            }
            catch (IOException)
            {
            }

            return AppSettings.Default;
        }
    }
}

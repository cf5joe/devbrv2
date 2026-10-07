using System.Reflection;
using System.Security.Principal;

namespace DevBR.Infrastructure;

public static class BuildInfo
{
    private static readonly Assembly Entry = Assembly.GetEntryAssembly() ?? typeof(BuildInfo).Assembly;

    public static string Version { get; } =
        (Entry.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public static string ReleaseChannel { get; } =
        Entry.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "DevBR.ReleaseChannel")?.Value ?? "Development";

    /// <summary>Unsigned development builds remain usable but are always identified as such.</summary>
    public static bool IsDevelopmentBuild => !string.Equals(ReleaseChannel, "Release", StringComparison.OrdinalIgnoreCase);

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}

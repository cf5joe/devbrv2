namespace DevBR.UiTests;

/// <summary>UI tests drive a real desktop session, so they only run when DEVBR_UI_TESTS=1.</summary>
public static class UiTestEnvironment
{
    public static bool Enabled => Environment.GetEnvironmentVariable("DEVBR_UI_TESTS") == "1";
}

public sealed class UiFactAttribute : FactAttribute
{
    public UiFactAttribute(
        [System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "UI automation tests are opt-in: set DEVBR_UI_TESTS=1 (they launch DevBR.exe on the desktop).";
        SkipUnless = nameof(UiTestEnvironment.Enabled);
        SkipType = typeof(UiTestEnvironment);
        DisableParallelization = true;
    }
}

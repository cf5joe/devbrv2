namespace DevBR.App.ViewModels;

public sealed record WizardStep(int Number, string Title, string Description);

public sealed class BackupViewModel : PageViewModel
{
    public override string Title => "Backup";

    public override string Glyph => "";

    public IReadOnlyList<WizardStep> Steps { get; } =
    [
        new(1, "Select artifacts", "Review the inventory and choose supported items. Only the system baseline is selected by default."),
        new(2, "Add files and folders", "Include your own scripts, notes or projects, with overlap and self-inclusion checks."),
        new(3, "Review", "Exclusions, sensitive items, dependencies and the estimated size."),
        new(4, "Output", "Backup file, scratch location, compression, and optional encryption."),
        new(5, "Confirm the plan", "See exactly what will be captured before anything is copied."),
        new(6, "Back up", "Run the capture, verify the archive, and read the final report."),
    ];
}

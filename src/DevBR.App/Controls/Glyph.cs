using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace DevBR.App.Controls;

/// <summary>
/// A decorative icon-font glyph. Hidden from screen readers (UI Automation control and content views):
/// the adjacent text or the owning control's name carries the meaning.
/// </summary>
public sealed class Glyph : TextBlock
{
    protected override AutomationPeer OnCreateAutomationPeer() => new GlyphAutomationPeer(this);

    private sealed class GlyphAutomationPeer(Glyph owner) : TextBlockAutomationPeer(owner)
    {
        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;
    }
}

using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Views.Shell;

/// <summary>
/// The chips of the shell's status strip (CUST-273): Watchdog, Guardrail, Keys, alerts, connector, redaction, policy, running, stale and
/// version, after the gateway's detail sentence, in the space between the state pill and the buttons. The view of a
/// <see cref="StatusStripViewModel"/> (its <c>DataContext</c>); <see cref="ChipStripPanel"/> lays the chips out and hides the ones that do not fit.
/// </summary>
public partial class StatusStripControl : UserControl
{
    public StatusStripControl()
    {
        InitializeComponent();
    }
}

/// <summary>
/// A text block UI Automation leaves out. A strip chip's words are read once, as the chip's own automation name (a sentence that says more than the
/// few visible words do); the text inside it would be a second, shorter reading of the same thing. Same idea as <c>DcSymbolIcon</c>.
/// </summary>
public sealed class StripText : TextBlock
{
    protected override AutomationPeer OnCreateAutomationPeer() => new DecorativePeer(this);

    private sealed class DecorativePeer : FrameworkElementAutomationPeer
    {
        public DecorativePeer(StripText owner)
            : base(owner)
        {
        }

        protected override string GetClassNameCore() => nameof(StripText);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;

        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}

/// <summary>Picks a chip's template by how it is drawn: the sentence, a badge, a state badge with its mark, a neutral chip, or the <c>+N</c> chip.</summary>
public sealed class StripChipTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Sentence { get; set; }

    public DataTemplate? Badge { get; set; }

    public DataTemplate? State { get; set; }

    public DataTemplate? Chip { get; set; }

    public DataTemplate? Overflow { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is not StripChip chip)
        {
            return base.SelectTemplate(item, container);
        }

        if (chip.Key == StripChipKey.Overflow)
        {
            return Overflow;
        }

        return chip.Kind switch
        {
            StripChipKind.Sentence => Sentence,
            StripChipKind.State => State,
            StripChipKind.Chip => Chip,
            _ => Badge,
        };
    }
}

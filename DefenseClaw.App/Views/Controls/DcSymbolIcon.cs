using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Markup;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Views.Controls;

/// <summary>
/// WPF-UI's Fluent <see cref="SymbolIcon"/>, made decorative for UI Automation.
/// <para>
/// A stock <c>SymbolIcon</c> draws through a text block holding one private-use character, and that text block is a UIA
/// Text element like any other: a screen reader walking a button or a card meets an unnamed "text" with a glyph code in it
/// next to the label it repeats. Every icon in this app sits beside words (a button's label, a card's title, a row's
/// name) or is an icon-only button whose own <c>AutomationProperties.Name</c> says what it does, so the glyph never carries
/// meaning of its own. This subclass says so: its peer is neither a control nor content and hides its children, so the
/// glyph drops out of the Control and Content views and the label is read once.
/// </para>
/// <para>Use <c>&lt;ctl:DcSymbolIcon Symbol="..." /&gt;</c> or, for a button, <c>Icon="{ctl:DcSymbolIcon Refresh24}"</c>.</para>
/// </summary>
public sealed class DcSymbolIcon : SymbolIcon
{
    public DcSymbolIcon()
    {
    }

    public DcSymbolIcon(SymbolRegular symbol)
    {
        Symbol = symbol;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new DecorativePeer(this);

    private sealed class DecorativePeer : FrameworkElementAutomationPeer
    {
        public DecorativePeer(DcSymbolIcon owner)
            : base(owner)
        {
        }

        protected override string GetClassNameCore() => nameof(DcSymbolIcon);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;

        /// <summary>The glyph's own text block would read as a Text element; the icon has nothing inside worth exposing.</summary>
        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}

/// <summary>The XAML shorthand <c>{ctl:DcSymbolIcon Symbol}</c>, the decorative twin of WPF-UI's <c>{ui:SymbolIcon Symbol}</c>.</summary>
[MarkupExtensionReturnType(typeof(DcSymbolIcon))]
public sealed class DcSymbolIconExtension : MarkupExtension
{
    public DcSymbolIconExtension()
    {
    }

    public DcSymbolIconExtension(SymbolRegular symbol)
    {
        Symbol = symbol;
    }

    [ConstructorArgument("symbol")]
    public SymbolRegular Symbol { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) => new DcSymbolIcon(Symbol);
}

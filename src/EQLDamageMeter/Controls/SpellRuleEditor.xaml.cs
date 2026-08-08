using System.Windows;
using System.Windows.Controls;
using EQLDamageMeter.ViewModels;

namespace EQLDamageMeter.Controls;

public partial class SpellRuleEditor : UserControl
{
    public static readonly RoutedEvent OverlayRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(OverlayRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(SpellRuleEditor));

    public SpellRuleEditor() => InitializeComponent();

    public event RoutedEventHandler OverlayRequested
    {
        add => AddHandler(OverlayRequestedEvent, value);
        remove => RemoveHandler(OverlayRequestedEvent, value);
    }

    private SpellRuleSetViewModel? Rules => DataContext as SpellRuleSetViewModel;
    private void Overlay_Click(object sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(OverlayRequestedEvent));
    private void Add_Click(object sender, RoutedEventArgs e) => Rules?.AddRule();
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Rules is null || sender is not Button { Tag: BuffRuleViewModel rule }) return;
        var owner = Window.GetWindow(this);
        if (MessageBox.Show(owner, $"Delete the tracking rule for {rule.SpellName}?", "Delete spell",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        ShowError(await Rules.DeleteRuleAsync(rule));
    }
    private async void RuleToggle_Click(object sender, RoutedEventArgs e) => ShowError(await (Rules?.SaveAsync() ?? Task.FromResult<string?>(null)));
    private async void Save_Click(object sender, RoutedEventArgs e) => ShowError(await (Rules?.SaveAsync() ?? Task.FromResult<string?>(null)));
    private void TestAlert_Click(object sender, RoutedEventArgs e) => ShowError(Rules?.TestAlert());
    private void ResetTimings_Click(object sender, RoutedEventArgs e) =>
        ShowError(Rules?.ResetSelectedTimingsToCatalog());
    private void SpellName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is SpellNameSearchBox { Tag: BuffRuleViewModel rule })
            ShowError(Rules?.ValidateSpell(rule));
    }
    private void All_Checked(object sender, RoutedEventArgs e) => Rules?.SetFilter("All");
    private void Enabled_Checked(object sender, RoutedEventArgs e) => Rules?.SetFilter("Enabled");
    private void Disabled_Checked(object sender, RoutedEventArgs e) => Rules?.SetFilter("Disabled");
    private void ShowError(string? error)
    {
        if (error is not null) MessageBox.Show(Window.GetWindow(this), error, "Spell Tracker", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}

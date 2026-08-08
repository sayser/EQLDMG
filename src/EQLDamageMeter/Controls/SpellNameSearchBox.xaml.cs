using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EQLDamageMeter.Services;
using EQLDamageMeter.ViewModels;

namespace EQLDamageMeter.Controls;

public partial class SpellNameSearchBox : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(SpellNameSearchBox),
        new FrameworkPropertyMetadata(string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnTextChanged));

    public static readonly DependencyProperty CatalogProperty = DependencyProperty.Register(
        nameof(Catalog), typeof(SpellDataCatalog), typeof(SpellNameSearchBox),
        new PropertyMetadata(null));

    private bool _updatingFromControl;
    private bool _updatingFromSource;
    private bool _applyingSuggestion;

    public SpellNameSearchBox() => InitializeComponent();

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public SpellDataCatalog? Catalog
    {
        get => (SpellDataCatalog?)GetValue(CatalogProperty);
        set => SetValue(CatalogProperty, value);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (SpellNameSearchBox)d;
        if (control._updatingFromControl) return;
        control._updatingFromSource = true;
        try
        {
            var text = e.NewValue as string ?? string.Empty;
            if (control.InputBox.Text != text)
                control.InputBox.Text = text;
        }
        finally
        {
            control._updatingFromSource = false;
        }
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingFromSource || _applyingSuggestion) return;
        _updatingFromControl = true;
        try
        {
            Text = InputBox.Text ?? string.Empty;
        }
        finally
        {
            _updatingFromControl = false;
        }

        RefreshSuggestions();
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseSuggestions();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Down && SuggestionPopup.IsOpen && SuggestionList.Items.Count > 0)
        {
            if (SuggestionList.SelectedIndex < 0) SuggestionList.SelectedIndex = 0;
            else SuggestionList.SelectedIndex =
                Math.Min(SuggestionList.SelectedIndex + 1, SuggestionList.Items.Count - 1);
            SuggestionList.ScrollIntoView(SuggestionList.SelectedItem);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Up && SuggestionPopup.IsOpen && SuggestionList.Items.Count > 0)
        {
            if (SuggestionList.SelectedIndex < 0) SuggestionList.SelectedIndex = 0;
            else SuggestionList.SelectedIndex = Math.Max(SuggestionList.SelectedIndex - 1, 0);
            SuggestionList.ScrollIntoView(SuggestionList.SelectedItem);
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Enter or Key.Tab && SuggestionPopup.IsOpen)
        {
            var selected = SuggestionList.SelectedItem as string
                           ?? SuggestionList.Items.OfType<string>().FirstOrDefault();
            if (selected is not null)
            {
                ApplySuggestion(selected);
                e.Handled = e.Key == Key.Enter;
            }
        }
    }

    private void InputBox_LostFocus(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_applyingSuggestion) return;
            if (SuggestionPopup.IsOpen && SuggestionBorder.IsMouseOver) return;
            if (!IsKeyboardFocusWithin) CloseSuggestions();
        });
    }

    private void SuggestionBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Keep focus from leaving the control while interacting with the list.
        e.Handled = true;
    }

    private void SuggestionList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindListBoxItem(e.OriginalSource as DependencyObject);
        if (item?.DataContext is string selected)
        {
            ApplySuggestion(selected);
            e.Handled = true;
        }
    }

    private void SuggestionList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SuggestionList.SelectedItem is string selected)
        {
            ApplySuggestion(selected);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseSuggestions();
            InputBox.Focus();
            e.Handled = true;
        }
    }

    private void ApplySuggestion(string selected)
    {
        _applyingSuggestion = true;
        try
        {
            _updatingFromControl = true;
            try
            {
                SetCurrentValue(TextProperty, selected);
                InputBox.Text = selected;
                InputBox.CaretIndex = selected.Length;
            }
            finally
            {
                _updatingFromControl = false;
            }

            CloseSuggestions();
            InputBox.Focus();
            InputBox.Select(selected.Length, 0);

            // Re-run parent LostFocus validation against the selected spell name.
            RaiseEvent(new RoutedEventArgs(LostFocusEvent));
        }
        finally
        {
            _applyingSuggestion = false;
        }
    }

    private void CloseSuggestions()
    {
        SuggestionPopup.IsOpen = false;
        SuggestionList.SelectedIndex = -1;
    }

    private void RefreshSuggestions()
    {
        var query = InputBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            CloseSuggestions();
            return;
        }

        var catalog = ResolveCatalog();
        var matches = catalog?.FindMatches(query) ?? [];
        if (matches.Count == 0)
        {
            CloseSuggestions();
            return;
        }

        SuggestionList.ItemsSource = matches;
        SuggestionList.SelectedIndex = -1;
        SuggestionPopup.IsOpen = true;
    }

    private SpellDataCatalog? ResolveCatalog()
    {
        if (Catalog is not null) return Catalog;
        if (DataContext is MainViewModel main && main.SpellCatalog is not null)
            return main.SpellCatalog;
        if (DataContext is SpellRuleSetViewModel rules && rules.SpellCatalog is not null)
            return rules.SpellCatalog;
        if (Window.GetWindow(this)?.DataContext is MainViewModel windowMain)
            return windowMain.SpellCatalog;
        return null;
    }

    private static ListBoxItem? FindListBoxItem(DependencyObject? current)
    {
        while (current is not null)
        {
            if (current is ListBoxItem item) return item;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}

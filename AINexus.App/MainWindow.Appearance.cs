using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AINexus.App;

public partial class MainWindow
{
    internal NexusAppearanceMode CurrentAppearance { get; private set; } = NexusAppearanceMode.Standard;
    internal double CurrentUiScale { get; private set; } = 1;

    private void InitializeAppearance() => ApplyAppearanceSelection(NexusAppearanceMode.Standard, false);

    internal void ApplyAppearanceSelection(NexusAppearanceMode requested, bool shiftHeld)
    {
        CurrentAppearance = NexusAppearance.ResolveSelection(requested, shiftHeld);
        var palette = NexusAppearance.PaletteFor(CurrentAppearance);
        SetAppearanceBrush("Background", palette.Background);
        SetAppearanceBrush("Panel", palette.Panel);
        SetAppearanceBrush("Button", palette.Button);
        SetAppearanceBrush("Hover", palette.Hover);
        SetAppearanceBrush("Accent", palette.Accent);
        SetAppearanceBrush("Secondary", palette.Secondary);
        SetAppearanceBrush("Text", palette.Text);
        SetAppearanceBrush("Muted", palette.Muted);
        SetAppearanceBrush("Border", palette.Border);
        SetAppearanceBrush("Selected", palette.Selected);
        Resources["NexusFontFamily"] = new FontFamily(palette.FontFamily);
        Resources["NexusCornerRadius"] = new CornerRadius(palette.CornerRadius);
        Resources[SystemColors.MenuBrushKey] = Resources["NexusPanelBrush"];
        Resources[SystemColors.MenuTextBrushKey] = Resources["NexusTextBrush"];
        SpaceBackdrop.Mode = CurrentAppearance;
        AppearanceStandardMenuItem.IsChecked = CurrentAppearance == NexusAppearanceMode.Standard;
        AppearanceLcarsMenuItem.IsChecked = CurrentAppearance is NexusAppearanceMode.Lcars or NexusAppearanceMode.Retro;
        UpdateUiScaleResources();
    }

    private void SetAppearanceBrush(string name, string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        Resources["Nexus" + name + "Brush"] = brush;
    }

    private void AppearanceStandard_Click(object sender, RoutedEventArgs e) => ApplyAppearanceSelection(NexusAppearanceMode.Standard, false);
    private void AppearanceLcars_Click(object sender, RoutedEventArgs e) =>
        ApplyAppearanceSelection(NexusAppearanceMode.Lcars, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (HandleUiScaleWheel((Keyboard.Modifiers & ModifierKeys.Shift) != 0, e.Delta)) e.Handled = true;
    }

    internal bool HandleUiScaleWheel(bool shiftHeld, int delta)
    {
        if (!shiftHeld || delta == 0) return false;
        SetUiScale(CurrentUiScale + delta / 120d * .10);
        return true;
    }

    internal void ResetUiScale() => SetUiScale(1);
    private void UiScaleLarger_Click(object sender, RoutedEventArgs e) => SetUiScale(CurrentUiScale + .10);
    private void UiScaleSmaller_Click(object sender, RoutedEventArgs e) => SetUiScale(CurrentUiScale - .10);
    private void UiScaleReset_Click(object sender, RoutedEventArgs e) => ResetUiScale();

    private void SetUiScale(double scale)
    {
        CurrentUiScale = Math.Clamp(Math.Round(scale, 2), .75, 1.75);
        UpdateUiScaleResources();
    }

    private void UpdateUiScaleResources()
    {
        Resources["NexusBaseFontSize"] = 14 * CurrentUiScale;
        for (var size = 1; size <= 64; size++) Resources["NexusFontSize" + size] = size * CurrentUiScale;
        Resources["NexusButtonMinHeight"] = 38 * CurrentUiScale;
        Resources["NexusButtonPadding"] = new Thickness(16 * CurrentUiScale, 10 * CurrentUiScale, 16 * CurrentUiScale, 10 * CurrentUiScale);
        Resources["NexusNavPadding"] = new Thickness(16 * CurrentUiScale, 11 * CurrentUiScale, 16 * CurrentUiScale, 11 * CurrentUiScale);
        Resources["NexusRowHeight"] = 48 * CurrentUiScale;
        Resources["NexusSidebarWidth"] = new GridLength(212 * CurrentUiScale);
        Resources["NexusHomeActionsWidth"] = new GridLength(360 * CurrentUiScale);
        UiScaleText.Text = (CurrentUiScale * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
    }
}

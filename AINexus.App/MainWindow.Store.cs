using System.Windows;
using System.Windows.Controls;

namespace AINexus.App;

public partial class MainWindow
{
    private void StoreNavigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string target } && FindName(target) is TabItem page)
            page.IsSelected = true;
    }

    private void WorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, WorkspaceTabs) || StorePageTitleText is null) return;
        if (WorkspaceTabs.SelectedItem is not TabItem page) return;
        StorePageTitleText.Text = page.Header?.ToString() ?? "AI Nexus";
        foreach (var button in StoreNavigation.Children.OfType<RadioButton>())
            button.IsChecked = button.Tag as string == page.Name;
    }
}

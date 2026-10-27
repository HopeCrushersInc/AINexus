using System.Windows;
using System.Windows.Controls;

namespace AINexus.App;

public partial class MainWindow
{
    private void StoreNavigate_Click(object sender, RoutedEventArgs e)
    {
        if (!_workspaceConfigured) { WorkloadSetupTab.IsSelected = true; return; }
        if (sender is FrameworkElement { Tag: string target } && FindName(target == "CatalogTab" ? "DiscoveryTab" : target) is TabItem page)
            page.IsSelected = true;
    }

    private void WorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, WorkspaceTabs) || StorePageTitleText is null) return;
        if (WorkspaceTabs.SelectedItem is not TabItem page) return;
        if (page == CatalogTab) { if (_workspaceConfigured) DiscoveryTab.IsSelected = true; else WorkloadSetupTab.IsSelected = true; return; }
        if (WorkloadSetupTab is not null)
        {
            if (!_workspaceConfigured && page != WorkloadSetupTab) { WorkloadSetupTab.IsSelected = true; return; }
            if (_workspaceConfigured && page == WorkloadSetupTab) { StoreHomeTab.IsSelected = true; return; }
            if (page == ImageStudioTab && _workspaceProfile?.HasImages != true
                || page == AssistantTab && _workspaceProfile?.HasAssistant != true) { StoreHomeTab.IsSelected = true; return; }
        }
        StorePageTitleText.Text = page.Header?.ToString() ?? "AI Nexus";
        foreach (var button in StoreNavigation.Children.OfType<RadioButton>())
            button.IsChecked = button.Tag as string == page.Name;
    }
}

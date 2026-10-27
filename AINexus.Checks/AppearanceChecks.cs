using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AINexus.App;

namespace AINexus.Checks;

internal static class AppearanceChecks
{
    internal static void Run(Action<bool, string> check)
    {
        check(NexusAppearance.ResolveSelection(NexusAppearanceMode.Lcars, shiftHeld: false) == NexusAppearanceMode.Lcars,
            "Normal LCARS selection keeps the visible LCARS appearance");
        check(NexusAppearance.ResolveSelection(NexusAppearanceMode.Lcars, shiftHeld: true) == NexusAppearanceMode.Retro,
            "Shift-clicking LCARS resolves to the hidden retro appearance");
        check(NexusAppearance.ResolveSelection(NexusAppearanceMode.Standard, shiftHeld: true) == NexusAppearanceMode.Standard
            && NexusAppearance.ResolveSelection(NexusAppearanceMode.Standard, shiftHeld: false) == NexusAppearanceMode.Standard,
            "Shift does not turn ordinary appearance selections into an unexpected retro mode");
        foreach (var mode in Enum.GetValues<NexusAppearanceMode>())
        {
            var palette = NexusAppearance.PaletteFor(mode);
            check(!string.IsNullOrWhiteSpace(palette.FontFamily)
                && ColorConverter.ConvertFromString(palette.Accent) is Color
                && ColorConverter.ConvertFromString(palette.Panel) is Color,
                $"{mode} appearance supplies a usable native font and palette without external assets");
        }
    }

    internal static void RunNative(MainWindow window, Action<bool, string> check)
    {
        check(window.FindName("AppearanceStandardMenuItem") is MenuItem
            && window.FindName("AppearanceLcarsMenuItem") is MenuItem lcars
            && lcars.Header?.ToString()?.Contains("LCARS", StringComparison.OrdinalIgnoreCase) == true
            && window.FindName("AppearanceRetroMenuItem") is null,
            "Appearance presents Standard and LCARS options without a separate visible retro menu item");

        // A separate, never-shown window keeps the shared regression fixture's workspace intact.
        var subject = new MainWindow { GuidedPersistPreferences = false };
        var provider = new CountingProvider();
        var powerCalls = 0;
        try
        {
            var cachePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "unused-appearance-" + Guid.NewGuid().ToString("N"));
            typeof(MainWindow).GetField("_discoveryPipeline", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(subject, new DiscoveryPipeline(provider, new DiscoveryCacheStore(cachePath)));
            typeof(MainWindow).GetField("_gpuPowerPolicy", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(subject, new GpuPowerPolicy((_, _) => { powerCalls++; return Task.FromResult(""); }));
            subject.CompleteWorkloadScan(DiscoveryPipelineChecks.FixtureMachine());
            var choices = ((IEnumerable<WorkloadChoice>)((ItemsControl)subject.FindName("WorkloadChoices")).ItemsSource).ToArray();
            check(subject.CurrentAppearance == NexusAppearanceMode.Standard && choices.All(choice => !choice.Selected),
                "A new window starts in the normal appearance with no workload selection restored");
            check(subject.WindowStyle == WindowStyle.SingleBorderWindow && subject.ResizeMode == ResizeMode.CanResize,
                "The appearance shell keeps native title-bar dragging and resizable window borders and corners");
            var initialFont = subject.FontSize;
            var heading = (TextBlock)subject.FindName("StorePageTitleText");
            var initialHeadingFont = heading.FontSize;
            var initialButtonHeight = (double)subject.FindResource("NexusButtonMinHeight");
            var initialButtonPadding = (Thickness)subject.FindResource("NexusButtonPadding");
            check(subject.CurrentUiScale == 1 && !subject.HandleUiScaleWheel(shiftHeld: false, delta: 120)
                && !subject.HandleUiScaleWheel(shiftHeld: true, delta: 0) && subject.CurrentUiScale == 1,
                "Ordinary scrolling and zero wheel movement leave appearance scale unchanged and unhandled");
            check(subject.HandleUiScaleWheel(shiftHeld: true, delta: 120) && Near(subject.CurrentUiScale, 1.1)
                && Near(subject.FontSize, initialFont * 1.1) && Near(heading.FontSize, initialHeadingFont * 1.1)
                && Near((double)subject.FindResource("NexusButtonMinHeight"), initialButtonHeight * 1.1)
                && Near(((Thickness)subject.FindResource("NexusButtonPadding")).Left, initialButtonPadding.Left * 1.1),
                "Shift-wheel scales inherited and explicit text sizes together with button height and padding");
            check(subject.HandleUiScaleWheel(shiftHeld: true, delta: -120) && Near(subject.CurrentUiScale, 1),
                "Shift-wheel in the opposite direction decreases the interface scale");
            subject.HandleUiScaleWheel(shiftHeld: true, delta: int.MaxValue);
            check(Near(subject.CurrentUiScale, 1.75), "Interface zoom stops at its upper bound instead of growing controls without limit");
            subject.HandleUiScaleWheel(shiftHeld: true, delta: -int.MaxValue);
            check(Near(subject.CurrentUiScale, .75), "Interface zoom stops at its lower bound so text and controls remain usable");
            subject.ResetUiScale();
            check(Near(subject.CurrentUiScale, 1) && Near(subject.FontSize, initialFont)
                && Near(heading.FontSize, initialHeadingFont)
                && Near((double)subject.FindResource("NexusButtonMinHeight"), initialButtonHeight),
                "Reset scale restores text and button dimensions to their original sizes");
            subject.HandleUiScaleWheel(shiftHeld: true, delta: 120);
            choices.Single(choice => choice.Interest == DiscoveryInterest.Coding).Selected = true;
            choices.Single(choice => choice.Interest == DiscoveryInterest.Images).Selected = true;
            subject.ApplyWorkloadSelection([DiscoveryInterest.Coding, DiscoveryInterest.Images]);
            var profileField = typeof(MainWindow).GetField("_workspaceProfile", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var originalProfile = profileField.GetValue(subject);
            var initialAccent = ((SolidColorBrush)subject.FindResource("NexusAccentBrush")).Color;
            var initialPanel = ((SolidColorBrush)subject.FindResource("NexusPanelBrush")).Color;

            subject.ApplyAppearanceSelection(NexusAppearanceMode.Lcars, shiftHeld: false);
            check(subject.CurrentAppearance == NexusAppearanceMode.Lcars
                && ((SolidColorBrush)subject.FindResource("NexusAccentBrush")).Color != initialAccent,
                "Normal LCARS selection updates the live native palette");
            var lcarsAccent = ((SolidColorBrush)subject.FindResource("NexusAccentBrush")).Color;
            subject.ApplyAppearanceSelection(NexusAppearanceMode.Lcars, shiftHeld: true);
            check(subject.CurrentAppearance == NexusAppearanceMode.Retro
                && ((SolidColorBrush)subject.FindResource("NexusAccentBrush")).Color != lcarsAccent
                && subject.FontFamily.Source == ((FontFamily)subject.FindResource("NexusFontFamily")).Source,
                "Shift-LCARS applies retro resources and the bound native font without restarting the window");
            check(((MenuItem)subject.FindName("AppearanceLcarsMenuItem")).Header?.ToString()?.Contains("LCARS", StringComparison.OrdinalIgnoreCase) == true,
                "The hidden retro path retains the visible LCARS menu choice");
            subject.ApplyAppearanceSelection(NexusAppearanceMode.Standard, shiftHeld: true);
            check(subject.CurrentAppearance == NexusAppearanceMode.Standard
                && ((SolidColorBrush)subject.FindResource("NexusAccentBrush")).Color == initialAccent
                && ((SolidColorBrush)subject.FindResource("NexusPanelBrush")).Color == initialPanel,
                "Returning to Standard restores its original palette rather than retaining retro colors");
            check(Near(subject.CurrentUiScale, 1.1) && Near(subject.FontSize, initialFont * 1.1)
                && Near(heading.FontSize, initialHeadingFont * 1.1),
                "Changing appearances preserves the user's text and button zoom");
            subject.ResetUiScale();
            var workflows = ((IEnumerable<DiscoveryWorkflow>)((ComboBox)subject.FindName("DiscoveryInterestSelector")).ItemsSource).Select(w => w.Interest).Order();
            check(ReferenceEquals(originalProfile, profileField.GetValue(subject))
                && choices.Where(choice => choice.Selected).Select(choice => choice.Interest).Order()
                    .SequenceEqual(new[] { DiscoveryInterest.Coding, DiscoveryInterest.Images }.Order())
                && workflows.SequenceEqual(new[] { DiscoveryInterest.Coding, DiscoveryInterest.Images }.Order()),
                "Appearance changes preserve the user's exact workload choices and discovery scope");
            check(provider.Calls == 0 && powerCalls == 0 && !subject.OwnedLlamaServerRunning
                && ((DataGrid)subject.FindName("DiscoveryResultsGrid")).SelectedIndex == -1
                && ((CheckBox)subject.FindName("AcquisitionApprovalCheck")).IsChecked != true
                && ((CheckBox)subject.FindName("GuidedModelApprovalCheck")).IsChecked != true
                && ((CheckBox)subject.FindName("GpuPowerApprovalCheck")).IsChecked != true,
                "Theme switching performs no search, GPU operation, model selection, acquisition approval or runtime launch");
            check(!Directory.Exists(cachePath), "Appearance changes create no discovery cache or downloaded files");
        }
        finally { subject.Close(); }
    }

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < .000001;

    private sealed class CountingProvider : IOnlineDiscoveryProvider
    {
        internal int Calls { get; private set; }
        public Task<OnlineDiscoverySnapshot> SearchAsync(OnlineDiscoveryRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new OnlineDiscoverySnapshot([], [], DateTimeOffset.UtcNow));
        }
    }
}

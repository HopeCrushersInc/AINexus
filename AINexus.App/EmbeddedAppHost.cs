using System.IO;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AINexus.App;

/// <summary>
/// A lazy embedded browser for a user-selected local web application. Navigation
/// and intercepted resources stay on one origin. This is not an OS network sandbox.
/// </summary>
public sealed class EmbeddedAppHost : UserControl, IDisposable
{
    private WebView2? browser;
    private Task? initialization;
    private Uri? allowedOrigin;
    private int disposed;
    private int navigationGeneration;

    public event Action<string>? StatusChanged;

    public async Task NavigateAsync(Uri endpoint)
    {
        EmbeddedNavigationPolicy.ValidateEndpoint(endpoint);
        if (!Dispatcher.CheckAccess())
        {
            await Dispatcher.InvokeAsync(() => NavigateAsync(endpoint)).Task.Unwrap();
            return;
        }
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var generation = ++navigationGeneration;
        try
        {
            initialization ??= InitializeAsync();
            await initialization;
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (generation != navigationGeneration) return;
            var core = browser!.CoreWebView2;
            core.Stop();
            allowedOrigin = new Uri(endpoint.GetLeftPart(UriPartial.Authority));
            Report($"Opening local application at {allowedOrigin.GetLeftPart(UriPartial.Authority)}.");
            core.Navigate(endpoint.AbsoluteUri);
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            Report($"Embedded application could not open: {ex.Message}");
            throw;
        }
    }

    private async Task InitializeAsync()
    {
        Report("Initializing the embedded browser. Microsoft Edge WebView2 Runtime is required.");
        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AINexus", "WebView2");
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: dataDirectory);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        browser = new WebView2();
        Content = browser;
        await browser.EnsureCoreWebView2Async(environment);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var core = browser.CoreWebView2;
        var settings = core.Settings;
        settings.AreHostObjectsAllowed = false;
        settings.IsWebMessageEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;

        core.NavigationStarting += OnNavigationStarting;
        core.FrameNavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            Report("The local application requested a popup; external windows are disabled.");
        };
        core.LaunchingExternalUriScheme += (_, args) =>
        {
            args.Cancel = true;
            Report("Opening external applications from embedded content is disabled.");
        };
        core.DownloadStarting += (_, args) =>
        {
            args.Cancel = true;
            args.Handled = true;
            Report("Downloads from embedded content are disabled.");
        };
        core.PermissionRequested += (_, args) =>
        {
            args.State = CoreWebView2PermissionState.Deny;
            args.Handled = true;
            Report($"Embedded browser permission denied: {args.PermissionKind}.");
        };
        core.ScreenCaptureStarting += (_, args) =>
        {
            args.Cancel = true;
            args.Handled = true;
            Report("Screen capture from embedded content is disabled.");
        };
        core.SaveFileSecurityCheckStarting += (_, args) =>
        {
            args.CancelSave = true;
            Report("Saving files from embedded content is disabled.");
        };
        core.BasicAuthenticationRequested += (_, args) =>
        {
            args.Cancel = true;
            Report("Browser authentication prompts are disabled. Use the local application's sign-in page if available.");
        };
        core.ServerCertificateErrorDetected += (_, args) =>
        {
            args.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
            Report("The local application's TLS certificate could not be verified.");
        };
        core.ClientCertificateRequested += (_, args) =>
        {
            args.Cancel = true;
            args.Handled = true;
            Report("Client certificate prompts from embedded content are disabled.");
        };
        core.ProcessFailed += (_, args) => Report($"Embedded browser process failed: {args.ProcessFailedKind}. Reopen the portal to retry.");
        // Include worker requests; the obsolete two-argument overload omits them.
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += OnWebResourceRequested;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (Volatile.Read(ref disposed) != 0 || allowedOrigin is null
            || !Uri.TryCreate(args.Uri, UriKind.Absolute, out var target)
            || !EmbeddedNavigationPolicy.IsAllowed(target, allowedOrigin))
        {
            args.Cancel = true;
            Report("Blocked navigation outside the selected local application origin.");
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (args.IsSuccess) Report("Local application loaded inside the portal.");
        else if (args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
            Report($"Local application failed to load: {args.WebErrorStatus} (HTTP {args.HttpStatusCode}). Check that its local server is running.");
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (Volatile.Read(ref disposed) == 0 && allowedOrigin is not null
            && Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var target)
            && EmbeddedNavigationPolicy.IsResourceAllowed(target, allowedOrigin)) return;
        if (browser?.CoreWebView2 is { } core)
            args.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked by local application policy", "Content-Type: text/plain\r\n");
        Report("Blocked a resource outside the selected local application origin. Some applications require external assets or APIs.");
    }

    private void Report(string status)
    {
        if (Volatile.Read(ref disposed) == 0) StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (Dispatcher.CheckAccess()) DisposeCore();
        else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(DisposeCore);
        GC.SuppressFinalize(this);
    }

    private void DisposeCore()
    {
        StatusChanged = null;
        allowedOrigin = null;
        browser?.Dispose();
        browser = null;
        Content = null;
    }
}

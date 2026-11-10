using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WorldTime.Core;

namespace WorldTimeFlyout.Views;

/// <summary>
/// The update message at the top of the full view: "Update available · Install",
/// download progress, "You're up to date", errors, and "Updated to X" once
/// after an update. Its button works without taking focus, so the calendar
/// stays open while you click it.
/// (Wraps an InfoBar rather than deriving from it: a C#-only subclass of a
/// templated control has no XAML type info, so its style would not apply.)
/// </summary>
public sealed class UpdateBar
{
    private enum Shown { None, Offer, Progress, Result, Updated }

    private readonly Button _install;
    private readonly HyperlinkButton _whatsNew;
    private readonly ProgressBar _progress;
    private readonly AppModel _model;
    private Updater? _updater;
    private Shown _shown;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _autoHide;

    public InfoBar Bar { get; } = new() { IsOpen = false, Margin = new Thickness(8, 0, 8, 6) };

    public UpdateBar(AppModel model)
    {
        _model = model;
        AutomationProperties.SetAutomationId(Bar, "UpdateBar");
        _install = new Button { Content = "Install", AllowFocusOnInteraction = false };
        AutomationProperties.SetAutomationId(_install, "UpdateInstall");
        _install.Click += async (_, _) => { if (_updater is { } u) await u.InstallAsync(); };
        _whatsNew = new HyperlinkButton { Content = "What's new", AllowFocusOnInteraction = false };
        _whatsNew.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(UpdateFeed.ReleasePage(_updater!.Current)) { UseShellExecute = true }); }
            catch (Exception e) { DiagLog.Write($"open release page: {e.Message}"); }
        };
        _progress = new ProgressBar { Minimum = 0, Maximum = 1, Margin = new Thickness(0, 0, 16, 12) };
        Bar.CloseButtonClick += (_, _) => OnClosedByUser();
    }

    public void Attach(Updater updater)
    {
        _updater = updater;
        updater.Changed += Apply;
        Apply();
    }

    private void OnClosedByUser()
    {
        var u = _updater;
        if (u is null) return;
        if (_shown == Shown.Updated) u.DismissUpdatedNotice();
        else u.Dismiss();
    }

    private void Apply()
    {
        var u = _updater;
        if (u is null) return;
        _autoHide?.Stop();
        switch (u.Status)
        {
            case UpdateStatus.Available when u.Offer is { } o && (u.Manual || _model.Settings.DismissedUpdate != o.Version.ToString()):
                Show(Shown.Offer, InfoBarSeverity.Informational, "Update available",
                    $"Version {o.Version} is ready to install.", _install, null, closable: true);
                break;
            case UpdateStatus.Downloading:
                _progress.IsIndeterminate = false;
                _progress.Value = u.Progress;
                Show(Shown.Progress, InfoBarSeverity.Informational, "Downloading update",
                    $"Version {u.Offer?.Version}, {u.Progress:P0}", null, _progress, closable: false);
                break;
            case UpdateStatus.Installing:
                _progress.IsIndeterminate = true;
                Show(Shown.Progress, InfoBarSeverity.Informational, "Installing update",
                    u.Kind == InstallKind.AllUsers
                        ? "Windows will ask for permission. World Time Flyout restarts when done."
                        : "World Time Flyout will restart in a moment.", null, _progress, closable: false);
                break;
            case UpdateStatus.Checking when u.Manual:
                _progress.IsIndeterminate = true;
                Show(Shown.Progress, InfoBarSeverity.Informational, "Checking for updates", "", null, _progress, closable: false);
                break;
            case UpdateStatus.UpToDate when u.Manual:
                Show(Shown.Result, InfoBarSeverity.Success, "You're up to date", $"Version {u.Current}", null, null, closable: true);
                HideLater();
                break;
            case UpdateStatus.Failed when u.Manual:
                Show(Shown.Result, InfoBarSeverity.Warning, "Couldn't update", u.Error ?? "", null, null, closable: true);
                break;
            default:
                if (u.UpdatedFrom is not null)
                    Show(Shown.Updated, InfoBarSeverity.Success, $"Updated to {u.Current}", "", _whatsNew, null, closable: true);
                else
                {
                    _shown = Shown.None;
                    Bar.IsOpen = false;
                }
                break;
        }
    }

    private void Show(Shown kind, InfoBarSeverity severity, string title, string message, ButtonBase? action, UIElement? content, bool closable)
    {
        _shown = kind;
        Bar.Severity = severity;
        Bar.Title = title;
        Bar.Message = message;
        Bar.ActionButton = action;
        Bar.Content = content;
        Bar.IsClosable = closable;
        Bar.IsOpen = true;
    }

    private void HideLater()
    {
        if (_autoHide is null)
        {
            _autoHide = Bar.DispatcherQueue.CreateTimer();
            _autoHide.IsRepeating = false;
            _autoHide.Interval = TimeSpan.FromSeconds(6);
            _autoHide.Tick += (_, _) => { if (_shown == Shown.Result) _updater?.Dismiss(); };
        }
        _autoHide.Start();
    }
}

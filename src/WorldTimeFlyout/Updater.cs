using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.UI.Dispatching;
using WorldTime.Core;

namespace WorldTimeFlyout;

public enum UpdateStatus { Idle, Checking, UpToDate, Available, Downloading, Installing, Failed }

/// <summary>How this copy is installed, which decides how an update is run.</summary>
public enum InstallKind
{
    /// <summary>Not installed by the installer (the zip): an update installs the regular "just me" version.</summary>
    Portable,
    /// <summary>"Just me": %LOCALAPPDATA%\Programs, no admin.</summary>
    CurrentUser,
    /// <summary>"All users": Program Files; Windows asks for admin rights on every update.</summary>
    AllUsers,
    /// <summary>"Just me" but in a folder only admins can write: can't be updated in place.</summary>
    CurrentUserReadOnly,
}

/// <summary>
/// Over-the-air updates from GitHub Releases. Once a day (if enabled) it reads
/// the latest release; when a newer one has an installer it offers it. On
/// request it downloads the installer, checks its SHA-256 against the release's
/// .sha256 file and runs it silently; the installer closes the app, replaces the
/// files and starts the new version. This is the only network access the app makes.
/// </summary>
public sealed class Updater
{
    private static readonly TimeSpan Daily = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(6);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(90);

    private readonly AppModel _model;
    private readonly DispatcherQueueTimer _timer;
    private readonly Uri _feed;
    private readonly bool _enabled;
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private HttpClient? _http;
    private bool _busy;

    public Updater(AppModel model, DispatcherQueue dq, AppOptions options)
    {
        _model = model;
        Current = CurrentVersion(options.PretendVersion);
        _enabled = !options.Screenshot && !options.NoUpdateCheck;
        _feed = options.UpdateFeed is { } f && Uri.TryCreate(f, UriKind.Absolute, out var u) && u.IsLoopback
            ? u
            : new Uri(UpdateFeed.LatestReleaseApi);
        _timer = dq.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += async (_, _) =>
        {
            await CheckAsync(manual: false);
            Schedule();
        };

        // leftovers of an earlier update (the installer may still be finishing: best effort)
        if (_enabled)
            _ = Task.Run(() =>
            {
                try { Directory.Delete(Path.Combine(Path.GetTempPath(), "WorldTimeFlyout-update"), recursive: true); }
                catch { }
            });

        // "Updated to X" once after an update
        var last = model.Settings.LastRunVersion;
        if (last is not null && AppVersion.TryParse(last, out var lastV) && Current > lastV) UpdatedFrom = lastV;
        if (last != Current.ToString()) model.SetFlag(s => s.LastRunVersion = Current.ToString());
    }

    public AppVersion Current { get; }
    public UpdateStatus Status { get; private set; }
    public UpdateOffer? Offer { get; private set; }
    /// <summary>Download progress, 0..1.</summary>
    public double Progress { get; private set; }
    public string? Error { get; private set; }
    /// <summary>The user asked (menu): show "up to date" / errors, not only offers.</summary>
    public bool Manual { get; private set; }
    /// <summary>Set when this run follows an update from that version.</summary>
    public AppVersion? UpdatedFrom { get; private set; }

    /// <summary>Raised on the UI thread whenever anything above changed.</summary>
    public event Action? Changed;

    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{F4DB3BC9-6641-4980-8AAD-3BBDC55DBF72}_is1";
    private InstallKind? _kind;

    /// <summary>Found from the installer's Installed apps entry and whether this folder is writable.</summary>
    public InstallKind Kind => _kind ??= DetectKind();

    private static InstallKind DetectKind()
    {
        var dir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        static bool Points(Microsoft.Win32.RegistryKey root, string dir)
        {
            try
            {
                using var key = root.OpenSubKey(UninstallKey);
                var path = key?.GetValue("Inno Setup: App Path") as string;
                return path is not null &&
                       string.Equals(Path.TrimEndingDirectorySeparator(path), dir, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }
        if (Points(Microsoft.Win32.Registry.LocalMachine, dir)) return InstallKind.AllUsers;
        if (!Points(Microsoft.Win32.Registry.CurrentUser, dir)) return InstallKind.Portable;
        try
        {
            var probe = Path.Combine(dir, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return InstallKind.CurrentUser;
        }
        catch (Exception)
        {
            return InstallKind.CurrentUserReadOnly;
        }
    }

    public static AppVersion CurrentVersion(string? pretend = null)
    {
        if (pretend is not null && AppVersion.TryParse(pretend, out var p)) return p;
        var info = typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (AppVersion.TryParse(info, out var v)) return v;
        var n = typeof(Updater).Assembly.GetName().Version;
        return n is null ? default : new AppVersion(n.Major, n.Minor, Math.Max(0, n.Build));
    }

    public void DismissUpdatedNotice()
    {
        UpdatedFrom = null;
        Changed?.Invoke();
    }

    /// <summary>Starts (or re-plans) the daily check.</summary>
    public void Schedule()
    {
        _timer.Stop();
        if (!_enabled || !_model.Settings.CheckForUpdates) return;
        var now = DateTimeOffset.UtcNow;
        var last = _model.Settings.LastUpdateCheck ?? DateTimeOffset.MinValue;
        var due = last == DateTimeOffset.MinValue ? now : last + Daily;
        var earliest = _started + StartupDelay; // never on the network right at sign-in
        if (due < earliest) due = earliest;
        var wait = due - now;
        if (wait < TimeSpan.FromSeconds(1)) wait = TimeSpan.FromSeconds(1);
        if (wait > Daily) wait = Daily;
        _timer.Interval = wait;
        _timer.Start();
        DiagLog.Detail($"update: next check in {wait.TotalMinutes:0} min");
    }

    private HttpClient Http => _http ??= CreateClient();

    private HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"WorldTimeFlyout/{Current}");
        return http;
    }

    /// <summary>Looks for a newer release. <paramref name="manual"/>: the user asked, so report the result.</summary>
    public async Task CheckAsync(bool manual)
    {
        if (_busy || Status is UpdateStatus.Downloading or UpdateStatus.Installing) return;
        if (!_enabled && !manual) return;
        _busy = true;
        Manual = manual;
        Set(UpdateStatus.Checking);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _feed);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var response = await Http.SendAsync(request, cts.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidOperationException("No public release found.");
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cts.Token);
            Offer = UpdateFeed.Choose(json, Current);
            _model.SetFlag(s => s.LastUpdateCheck = DateTimeOffset.UtcNow);
            DiagLog.Write(Offer is { } o ? $"update: {Current} -> {o.Version} available" : $"update: {Current} is up to date");
            Set(Offer is null ? UpdateStatus.UpToDate : UpdateStatus.Available);
        }
        catch (Exception e)
        {
            DiagLog.Write($"update check failed: {e.GetType().Name}: {e.Message}");
            // try again in a few hours rather than a day
            _model.SetFlag(s => s.LastUpdateCheck = DateTimeOffset.UtcNow - Daily + RetryAfterFailure);
            Fail(Friendly(e));
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Downloads, verifies and runs the installer. The app keeps running: the
    /// installer closes it once it is ready to replace the files (after Windows'
    /// admin prompt, for all-users installs) and starts the new version. If the
    /// installer ends while we are still here, the update was cancelled or failed.
    /// </summary>
    public async Task InstallAsync()
    {
        if (_busy || Offer is not { } offer) return;
        if (Kind == InstallKind.CurrentUserReadOnly)
        {
            Manual = true;
            Fail("This copy is in a folder that needs admin rights, so it can't update itself. " +
                 "Uninstall it, then run the new installer and choose \"Install for all users\".");
            return;
        }
        _busy = true;
        Manual = true;
        Progress = 0;
        Set(UpdateStatus.Downloading);
        string? path = null;
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "WorldTimeFlyout-update", offer.Version.ToString());
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, offer.Installer.Name);

            var checksumText = await Http.GetStringAsync(offer.Checksum.Url);
            var expected = UpdateFeed.ParseChecksum(checksumText, offer.Installer.Name)
                           ?? throw new InvalidDataException("The release's checksum file could not be read.");

            using (var response = await Http.GetAsync(offer.Installer.Url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? offer.Installer.Size;
                await using var source = await response.Content.ReadAsStreamAsync();
                await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 16];
                long done = 0;
                double lastReported = 0;
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read));
                    sha.AppendData(buffer, 0, read);
                    done += read;
                    if (total > 0)
                    {
                        Progress = Math.Min(1, done / (double)total);
                        if (Progress - lastReported >= 0.02) { lastReported = Progress; Changed?.Invoke(); }
                    }
                }
                var actual = Convert.ToHexStringLower(sha.GetHashAndReset());
                if (actual != expected)
                    throw new InvalidDataException("The download did not match the release's checksum.");
            }
            DiagLog.Write($"update: {offer.Installer.Name} downloaded and verified");

            Set(UpdateStatus.Installing);
            string mode = Kind == InstallKind.AllUsers ? "/ALLUSERS" : "/CURRENTUSER";
            using var installer = Process.Start(new ProcessStartInfo(path)
            {
                Arguments = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART {mode}",
                UseShellExecute = true,
                WorkingDirectory = dir,
            }) ?? throw new InvalidOperationException("The installer did not start.");
            DiagLog.Write($"update: installer started for {offer.Version} ({Kind}), waiting for it to close the app");
            await installer.WaitForExitAsync();
            // Still running: the installer never got to replace us.
            DiagLog.Write($"update: installer ended with code {installer.ExitCode} and the app is still running");
            throw new InvalidOperationException(Kind == InstallKind.AllUsers
                ? "The update was cancelled (Windows needs your OK to update an all-users install)."
                : $"The update didn't finish (installer code {installer.ExitCode}).");
        }
        catch (Exception e)
        {
            DiagLog.Write($"update install failed: {e.GetType().Name}: {e.Message}");
            if (path is not null) try { File.Delete(path); } catch { }
            Fail(Friendly(e));
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>CI/test hook (--update-now): check, and install right away if there is an update.</summary>
    public async Task CheckAndInstallNowAsync()
    {
        await CheckAsync(manual: true);
        if (Status == UpdateStatus.Available) await InstallAsync();
    }

    /// <summary>Screenshots only: show a state without touching the network.</summary>
    public void Preview(UpdateStatus status, UpdateOffer? offer = null, double progress = 0, string? error = null,
        bool manual = true, AppVersion? updatedFrom = null)
    {
        UpdatedFrom = updatedFrom;
        Offer = offer;
        Progress = progress;
        Manual = manual;
        Error = error;
        Set(status);
    }

    public void Dismiss()
    {
        if (Status == UpdateStatus.Available && Offer is { } o) _model.SetFlag(s => s.DismissedUpdate = o.Version.ToString());
        if (Status is UpdateStatus.UpToDate or UpdateStatus.Failed) Set(Offer is null ? UpdateStatus.Idle : UpdateStatus.Available);
        Manual = false;
        Changed?.Invoke();
    }

    private void Set(UpdateStatus status)
    {
        Status = status;
        if (status != UpdateStatus.Failed) Error = null;
        Changed?.Invoke();
    }

    private void Fail(string message)
    {
        Error = message;
        Status = UpdateStatus.Failed;
        Changed?.Invoke();
    }

    private static string Friendly(Exception e) => e switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden } => "GitHub is limiting requests right now. Try again later.",
        HttpRequestException => "Couldn't reach GitHub. Check the internet connection.",
        TaskCanceledException => "GitHub took too long to answer. Try again later.",
        InvalidDataException or InvalidOperationException => e.Message,
        _ => e.Message,
    };
}

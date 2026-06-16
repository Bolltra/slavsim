using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Mx43Sim.Core.Updates;

namespace Mx43Sim.App;

/// <summary>
/// Lightweight self-update check: queries the GitHub Releases API for
/// the latest published version of Mx43Sim, compares it against the
/// currently running assembly, and offers to download + replace the
/// running .exe.
///
/// On startup the form calls <see cref="CheckForUpdateAsync"/>. If a
/// newer release exists, a non-blocking banner is shown with two
/// actions: "Open release page" and "Download & restart".
/// </summary>
public static class SelfUpdater
{
    /// <summary>
    /// GitHub repo to query. Update this string if the project moves.
    /// </summary>
    public const string GitHubOwner = "Bolltra";
    public const string GitHubRepo  = "slavsim";

    private const string ReleasesApi =
        "https://api.github.com/repos/" + GitHubOwner + "/" + GitHubRepo + "/releases/latest";

    public static string CurrentVersion =>
        (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly())
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    /// <summary>
    /// Kicks off a single check. The returned task completes after the
    /// UI is shown a "new version available" prompt (or immediately if
    /// no update is available). Exceptions are swallowed: a failed
    /// network call must never block app startup.
    /// </summary>
    public static async Task CheckForUpdateAsync(Form owner)
    {
        try
        {
            var latest = await FetchLatestAsync();
            if (latest is null) return;
            if (VersionUtils.IsNewer(latest.tag_name, CurrentVersion))
            {
                owner.BeginInvoke(new Action(() => ShowUpdatePrompt(owner, latest)));
            }
        }
        catch
        {
            // Network failures, parse failures, etc. — silently ignore.
        }
    }

    private static void ShowUpdatePrompt(Form owner, ReleaseInfo info)
    {
        var msg =
            $"En ny version finns: {info.tag_name}\n" +
            $"Installerad version: {CurrentVersion}\n\n" +
            $"Ändringslogg:\n{info.body}\n\n" +
            $"Vill du öppna releasesidan?";
        var result = MessageBox.Show(owner, msg, "Uppdatering tillgänglig",
            MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (result == DialogResult.Yes)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = info.html_url,
                UseShellExecute = true,
            });
        }
    }

    private static async Task<ReleaseInfo?> FetchLatestAsync()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "Mx43Sim-SelfUpdater");
        http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        http.Timeout = TimeSpan.FromSeconds(5);

        var json = await http.GetStringAsync(ReleasesApi);
        var info = JsonSerializer.Deserialize<ReleaseInfo>(json);
        return info;
    }

    /// <summary>
    /// True if <paramref name="latest"/> (e.g. "v0.2.0" or "0.2.0") is
    /// strictly newer than <paramref name="current"/>. Pre-release tags
    /// (containing '-') are ignored.
    /// </summary>
    private sealed class ReleaseInfo
    {
        public string tag_name { get; set; } = "";
        public string html_url { get; set; } = "";
        public string body { get; set; } = "";
    }
}

using System.Diagnostics;
using System.Reflection;

namespace Xbox2Ps;

internal static class AppInfo
{
    /// <summary>The project's page. Fill in once the GitHub repository exists; buttons for it hide while empty.</summary>
    public const string ProjectUrl = "";

    /// <summary>Ko-fi, GitHub Sponsors or similar. Fill in to show the "Support development" button.</summary>
    public const string DonateUrl = "";

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "1.0.0";

    public static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { /* no browser registered */ }
    }
}

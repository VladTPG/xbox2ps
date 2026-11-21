using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xbox2Ps.Core;

/// <summary>App options, kept in %APPDATA%\xbox2ps\settings.json.</summary>
public sealed class AppSettings
{
    public string ActiveProfile { get; set; } = "Default";
    /// <summary>0-3, or null to use the first connected controller.</summary>
    public int? Slot { get; set; }
    public bool StartMappingOnLaunch { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool TrayHintShown { get; set; }
    /// <summary>Key name (see <see cref="KeyNames"/>) that pauses and resumes keyboard and mouse capture.</summary>
    public string CaptureHotkey { get; set; } = "F8";
    /// <summary>Capture the keyboard and mouse whenever PS Remote Play is the window in front.</summary>
    public bool AutoCaptureRemotePlay { get; set; } = true;

    /// <summary>The hotkey as a virtual-key code; F8 if the stored one isn't a keyboard key.</summary>
    [JsonIgnore]
    public int CaptureHotkeyCode
    {
        get
        {
            int code = KeyNames.Code(CaptureHotkey);
            return code is > 0 and < 256 && !KeyNames.IsMouse(code) ? code : 0x77;
        }
    }

    public static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "xbox2ps");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
                if (s.Slot is < 0 or > 3) s.Slot = null;
                return s;
            }
        }
        catch (Exception) { /* a broken file falls back to defaults */ }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        Storage.WriteAtomic(FilePath, JsonSerializer.Serialize(this, Json));
    }
}

/// <summary>Profiles, one JSON file each in %APPDATA%\xbox2ps\profiles. The file name is the profile name.</summary>
public static class ProfileStore
{
    public const int MaxNameLength = 60;

    public static string Folder => Path.Combine(AppSettings.Folder, "profiles");

    private static string PathOf(string name) => Path.Combine(Folder, name + ".json");

    /// <summary>Null if <paramref name="name"/> can be used as a profile name, otherwise why not.</summary>
    public static string? ValidateName(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) return "Enter a name.";
        if (name.Length > MaxNameLength) return $"Use at most {MaxNameLength} characters.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.'))
            return "Don't use \\ / : * ? \" < > | or a trailing dot.";
        return null;
    }

    public static bool Exists(string name) => File.Exists(PathOf(name));

    public static List<string> List()
    {
        if (!Directory.Exists(Folder)) return [];
        return Directory.GetFiles(Folder, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Makes sure there's at least one profile, and returns the names.</summary>
    public static List<string> EnsureAny()
    {
        var names = List();
        if (names.Count > 0) return names;
        Save(Profile.CreateDefault());
        return List();
    }

    /// <summary>Loads a profile; a missing or broken file gives the defaults under that name.</summary>
    public static Profile Load(string name)
    {
        try
        {
            var p = Profile.FromJson(File.ReadAllText(PathOf(name)));
            p.Name = name;
            return p;
        }
        catch (Exception)
        {
            return Profile.CreateDefault(name);
        }
    }

    public static void Save(Profile p)
    {
        Directory.CreateDirectory(Folder);
        Storage.WriteAtomic(PathOf(p.Name), p.ToJson());
    }

    public static void Delete(string name)
    {
        if (Exists(name)) File.Delete(PathOf(name));
    }

    public static void Rename(string oldName, string newName)
    {
        var p = Load(oldName);
        p.Name = newName;
        Save(p);
        if (!string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase)) Delete(oldName);
    }

    /// <summary>A name like <paramref name="wanted"/> that no profile uses yet.</summary>
    public static string UniqueName(string wanted)
    {
        wanted = wanted.Trim();
        if (ValidateName(wanted) is not null) wanted = "Imported";
        if (!Exists(wanted)) return wanted;
        for (int i = 2; ; i++)
        {
            string n = $"{wanted} ({i})";
            if (!Exists(n)) return n;
        }
    }
}

internal static class Storage
{
    /// <summary>Write to a temp file and move it over, so a crash never leaves half a file.</summary>
    public static void WriteAtomic(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}

using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using Xbox2Ps.Core;

namespace Xbox2Ps.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private const int MaxLogLines = 500;
    private const double StickBox = 100, StickDot = 12;
    private const double StickHalf = (StickBox - StickDot) / 2;

    private readonly Bridge _bridge = new();
    private readonly AppSettings _settings;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _saveTimer, _liveTimer, _probeTimer, _listenTimer;
    private readonly byte[] _preview = new byte[Mapper.PacketSize];
    private readonly byte[] _controllerPreview = new byte[Mapper.PacketSize];
    private readonly byte[] _shown = new byte[Mapper.PacketSize];

    private Profile _profile;
    private CompiledProfile _compiled;
    private bool _loading;

    // Live preview polling.
    private int _previewSlot = -1;
    private long _nextScan;
    private uint _lastPreviewPacket = uint.MaxValue;
    private CompiledProfile? _lastPreviewCompiled;

    // VIIPER probe results.
    private bool _viiperInstalled = Viiper.IsInstalled, _viiperReachable, _probing;
    private string? _lastError;

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        _ui = Dispatcher.CurrentDispatcher;

        foreach (var input in Enum.GetValues<XboxInput>())
            Buttons.Add(new ButtonRowViewModel(input, ProfileEdited));
        // Mute last, so the rest pair up in the two columns (Cross/Circle, L1/R1, D-pad up/down...).
        foreach (var action in Enum.GetValues<KbmAction>().OrderBy(a => a == KbmAction.Mute))
            KbmRows.Add(new KbmRowViewModel(action));

        _bridge.Log += msg => _ui.BeginInvoke(() => AddLog(msg));
        _bridge.StateChanged += (state, msg) => _ui.BeginInvoke(() => OnBridgeState(state, msg));
        _bridge.CaptureChanged += capture => _ui.BeginInvoke(() => OnCapture(capture));
        ApplyCaptureOptions();

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => SaveNow();
        _liveTimer = new DispatcherTimer(DispatcherPriority.Render, _ui) { Interval = TimeSpan.FromMilliseconds(33) };
        _liveTimer.Tick += (_, _) => LiveTick();
        _probeTimer = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromSeconds(3) };
        _probeTimer.Tick += (_, _) => ProbeViiper();
        _listenTimer = new DispatcherTimer(DispatcherPriority.Normal, _ui) { Interval = TimeSpan.FromSeconds(6) };
        _listenTimer.Tick += (_, _) => CancelListening();

        var names = ProfileStore.EnsureAny();
        foreach (var n in names) ProfileNames.Add(n);
        string active = names.FirstOrDefault(n => string.Equals(n, settings.ActiveProfile, StringComparison.OrdinalIgnoreCase)) ?? names[0];
        _profile = ProfileStore.Load(active);
        _compiled = CompiledProfile.From(_profile);
        LoadIntoUi(_profile);

        _launchWithWindows = Autostart.IsEnabled;
        try { Autostart.Refresh(); } catch (Exception) { /* registry blocked; leave it */ }

        AddLog($"xbox2ps {AppInfo.Version} started.");
        Mapper.Neutral(_controllerPreview);
        Mapper.Neutral(_preview);
        ShowController(default, connected: false);
        ShowOutput(_preview);
        UpdateStatus();
        ProbeViiper();
        _liveTimer.Start();
        _probeTimer.Start();
    }

    // ---- Collections the window binds to ----

    public ObservableCollection<ButtonRowViewModel> Buttons { get; } = [];
    public ObservableCollection<ComboViewModel> Combos { get; } = [];
    public ObservableCollection<string> ProfileNames { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];
    public IReadOnlyList<Option<DsOutput>> OutputOptions => Labels.Outputs;
    public IReadOnlyList<Option<XboxInput>> InputOptions => Labels.Inputs;
    public IReadOnlyList<string> SlotOptions { get; } = ["Any controller", "Controller 1", "Controller 2", "Controller 3", "Controller 4"];

    // ---- Header status ----

    private StatusLevel _viiperLevel, _controllerLevel, _padLevel;
    private string _viiperText = "Checking…", _controllerText = "Not connected", _padText = "Off";
    private bool _isActive;

    public StatusLevel ViiperLevel { get => _viiperLevel; private set => Set(ref _viiperLevel, value); }
    public string ViiperText { get => _viiperText; private set => Set(ref _viiperText, value); }
    public StatusLevel ControllerLevel { get => _controllerLevel; private set => Set(ref _controllerLevel, value); }
    public string ControllerText { get => _controllerText; private set => Set(ref _controllerText, value); }
    public StatusLevel PadLevel { get => _padLevel; private set => Set(ref _padLevel, value); }
    public string PadText { get => _padText; private set => Set(ref _padText, value); }

    /// <summary>True from Start until the bridge has stopped.</summary>
    public bool IsActive
    {
        get => _isActive;
        private set
        {
            if (!Set(ref _isActive, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(StartStopLabel));
        }
    }

    public bool IsIdle => !IsActive;
    public string StartStopLabel => IsActive ? "Stop mapping" : "Start mapping";

    private string _statusSummary = "Stopped";
    /// <summary>One line for the tray tooltip.</summary>
    public string StatusSummary { get => _statusSummary; private set => Set(ref _statusSummary, value); }

    // ---- Setup banner ----

    private bool _showBanner, _bannerShowsDownload;
    private string _bannerTitle = "", _bannerText = "";
    public bool ShowBanner { get => _showBanner; private set => Set(ref _showBanner, value); }
    public bool BannerShowsDownload { get => _bannerShowsDownload; private set => Set(ref _bannerShowsDownload, value); }
    public string BannerTitle { get => _bannerTitle; private set => Set(ref _bannerTitle, value); }
    public string BannerText { get => _bannerText; private set => Set(ref _bannerText, value); }

    // ---- Controller slot ----

    public int SlotIndex
    {
        get => _settings.Slot is int s ? s + 1 : 0;
        set
        {
            int? slot = value <= 0 ? null : value - 1;
            if (slot == _settings.Slot) return;
            _settings.Slot = slot;
            SaveSettings();
            _previewSlot = -1;
            _nextScan = 0;
            OnPropertyChanged();
        }
    }

    // ---- Sticks, triggers, rumble (all part of the profile) ----

    private bool _swapSticks, _leftInvertX, _leftInvertY, _rightInvertX, _rightInvertY, _rumbleEnabled;
    private int _leftDeadzone, _rightDeadzone, _triggerThreshold, _rumbleStrength;

    public bool SwapSticks { get => _swapSticks; set { if (Set(ref _swapSticks, value)) ProfileEdited(); } }
    public bool LeftInvertX { get => _leftInvertX; set { if (Set(ref _leftInvertX, value)) ProfileEdited(); } }
    public bool LeftInvertY { get => _leftInvertY; set { if (Set(ref _leftInvertY, value)) ProfileEdited(); } }
    public bool RightInvertX { get => _rightInvertX; set { if (Set(ref _rightInvertX, value)) ProfileEdited(); } }
    public bool RightInvertY { get => _rightInvertY; set { if (Set(ref _rightInvertY, value)) ProfileEdited(); } }
    public bool RumbleEnabled { get => _rumbleEnabled; set { if (Set(ref _rumbleEnabled, value)) ProfileEdited(); } }

    public int LeftDeadzone
    {
        get => _leftDeadzone;
        set { if (Set(ref _leftDeadzone, Math.Clamp(value, 0, Profile.MaxDeadzone))) { ProfileEdited(); OnDeadzoneChanged(); } }
    }

    public int RightDeadzone
    {
        get => _rightDeadzone;
        set { if (Set(ref _rightDeadzone, Math.Clamp(value, 0, Profile.MaxDeadzone))) { ProfileEdited(); OnDeadzoneChanged(); } }
    }

    public int TriggerThreshold
    {
        get => _triggerThreshold;
        set { if (Set(ref _triggerThreshold, Math.Clamp(value, 0, Profile.MaxThreshold))) ProfileEdited(); }
    }

    public int RumbleStrength
    {
        get => _rumbleStrength;
        set { if (Set(ref _rumbleStrength, Math.Clamp(value, 0, 100))) ProfileEdited(); }
    }

    // Deadzone rings on the stick previews.
    public double LeftDeadzoneSize => StickBox * LeftDeadzone / 100.0;
    public double LeftDeadzoneOffset => (StickBox - LeftDeadzoneSize) / 2;
    public double RightDeadzoneSize => StickBox * RightDeadzone / 100.0;
    public double RightDeadzoneOffset => (StickBox - RightDeadzoneSize) / 2;

    private void OnDeadzoneChanged()
    {
        OnPropertyChanged(nameof(LeftDeadzoneSize));
        OnPropertyChanged(nameof(LeftDeadzoneOffset));
        OnPropertyChanged(nameof(RightDeadzoneSize));
        OnPropertyChanged(nameof(RightDeadzoneOffset));
    }

    // ---- Live preview ----

    private bool _nothingPressed = true;
    private double _leftRawX, _leftRawY, _leftOutX, _leftOutY, _rightRawX, _rightRawY, _rightOutX, _rightOutY;
    private int _l2Value, _r2Value;

    /// <summary>The DualSense buttons currently held, shown as chips.</summary>
    public ObservableCollection<Option<DsOutput>> PressedOutputs { get; } = [];
    public bool NothingPressed { get => _nothingPressed; private set => Set(ref _nothingPressed, value); }
    public double LeftRawX { get => _leftRawX; private set => Set(ref _leftRawX, value); }
    public double LeftRawY { get => _leftRawY; private set => Set(ref _leftRawY, value); }
    public double LeftOutX { get => _leftOutX; private set => Set(ref _leftOutX, value); }
    public double LeftOutY { get => _leftOutY; private set => Set(ref _leftOutY, value); }
    public double RightRawX { get => _rightRawX; private set => Set(ref _rightRawX, value); }
    public double RightRawY { get => _rightRawY; private set => Set(ref _rightRawY, value); }
    public double RightOutX { get => _rightOutX; private set => Set(ref _rightOutX, value); }
    public double RightOutY { get => _rightOutY; private set => Set(ref _rightOutY, value); }
    public int L2Value { get => _l2Value; private set => Set(ref _l2Value, value); }
    public int R2Value { get => _r2Value; private set => Set(ref _r2Value, value); }

    // ---- Keyboard & mouse ----

    private bool _kbmEnabled, _invertMouseY;
    private MouseTarget _mouseTarget;
    private int _mouseSensitivity, _mouseCurve, _mouseAntiDeadzone;
    private CaptureState? _capture;
    private bool _captureNoticeShown;
    private KbmRowViewModel? _listeningRow;
    private bool _listeningHotkey;
    private string _kbmNote = "";
    private StatusLevel _kbmLevel;
    private string _kbmText = "Off";

    /// <summary>A short notice for the tray (title, text), e.g. the first time capture turns on.</summary>
    public event Action<string, string>? Notify;

    public ObservableCollection<KbmRowViewModel> KbmRows { get; } = [];
    public IReadOnlyList<Option<MouseTarget>> MouseTargetOptions => Labels.MouseTargets;

    public bool KbmEnabled
    {
        get => _kbmEnabled;
        set { if (Set(ref _kbmEnabled, value)) { ProfileEdited(); UpdateKbmStatus(); } }
    }

    public MouseTarget MouseTarget
    {
        get => _mouseTarget;
        set { if (Set(ref _mouseTarget, value)) { ProfileEdited(); OnPropertyChanged(nameof(MouseEnabled)); } }
    }

    public bool MouseEnabled => MouseTarget != MouseTarget.Off;

    public int MouseSensitivity
    {
        get => _mouseSensitivity;
        set { if (Set(ref _mouseSensitivity, Math.Clamp(value, 1, 100))) ProfileEdited(); }
    }

    public int MouseCurve
    {
        get => _mouseCurve;
        set { if (Set(ref _mouseCurve, Math.Clamp(value, 0, 100))) ProfileEdited(); }
    }

    public int MouseAntiDeadzone
    {
        get => _mouseAntiDeadzone;
        set { if (Set(ref _mouseAntiDeadzone, Math.Clamp(value, 0, KbmSettings.MaxAntiDeadzone))) ProfileEdited(); }
    }

    public bool InvertMouseY { get => _invertMouseY; set { if (Set(ref _invertMouseY, value)) ProfileEdited(); } }

    /// <summary>App-wide, not per profile.</summary>
    public bool AutoCaptureRemotePlay
    {
        get => _settings.AutoCaptureRemotePlay;
        set
        {
            if (value == _settings.AutoCaptureRemotePlay) return;
            _settings.AutoCaptureRemotePlay = value;
            SaveSettings();
            ApplyCaptureOptions();
            OnPropertyChanged();
            OnPropertyChanged(nameof(CaptureHint));
            UpdateKbmStatus();
        }
    }

    private string HotkeyName => KeyNames.Name(_settings.CaptureHotkeyCode) ?? "F8";
    private string HotkeyDisplay => KeyNames.Display(HotkeyName);

    public string HotkeyText => _listeningHotkey ? "Press a key…" : HotkeyDisplay;

    public string CaptureHint => AutoCaptureRemotePlay
        ? "While PS Remote Play is the window in front, the keyboard and mouse drive the DualSense and the cursor stays hidden. " +
          $"Alt+Tab away to get them back, or press {HotkeyDisplay} to pause and resume."
        : $"Press {HotkeyDisplay} to start capturing the keyboard and mouse, and again to stop. Switching to xbox2ps releases them too.";

    /// <summary>Feedback after binding a key ("Space moved from Cross to Circle.").</summary>
    public string KbmNote { get => _kbmNote; private set { if (Set(ref _kbmNote, value)) OnPropertyChanged(nameof(HasKbmNote)); } }
    public bool HasKbmNote => _kbmNote.Length > 0;

    public StatusLevel KbmLevel { get => _kbmLevel; private set => Set(ref _kbmLevel, value); }
    public string KbmText { get => _kbmText; private set => Set(ref _kbmText, value); }

    public bool IsListening => _listeningRow is not null || _listeningHotkey;
    public bool IsListeningForHotkey => _listeningHotkey;
    public KbmRowViewModel? ListeningRow => _listeningRow;

    private void ApplyCaptureOptions() => _bridge.SetCaptureOptions(_settings.CaptureHotkeyCode, _settings.AutoCaptureRemotePlay);

    private void OnCapture(CaptureState? capture)
    {
        _capture = capture;
        UpdateKbmStatus();
        if (capture == CaptureState.Captured && !_captureNoticeShown)
        {
            _captureNoticeShown = true;
            Notify?.Invoke("Keyboard and mouse captured",
                $"They now drive the DualSense. Press {HotkeyDisplay} or Alt+Tab to get them back.");
        }
    }

    private void UpdateKbmStatus()
    {
        (KbmLevel, KbmText) = (_kbmEnabled, _capture) switch
        {
            (false, _) => (StatusLevel.Off, "Off"),
            (true, CaptureState.Captured) => (StatusLevel.Ok, "Captured"),
            (true, CaptureState.Paused) when AutoCaptureRemotePlay => (StatusLevel.Warn, $"Paused ({HotkeyDisplay})"),
            (true, CaptureState.Paused) => (StatusLevel.Off, $"Press {HotkeyDisplay} to capture"),
            (true, CaptureState.Waiting) => (StatusLevel.Off, "Waiting for Remote Play"),
            _ => (StatusLevel.Off, IsActive ? "Starting…" : "Starts with mapping"),
        };
    }

    public void StartListening(KbmRowViewModel row)
    {
        CancelListening();
        KbmNote = "";
        _listeningRow = row;
        row.IsListening = true;
        _listenTimer.Start();
    }

    public void StartListeningHotkey()
    {
        CancelListening();
        KbmNote = "";
        _listeningHotkey = true;
        OnPropertyChanged(nameof(HotkeyText));
        _listenTimer.Start();
    }

    public void CancelListening()
    {
        _listenTimer.Stop();
        if (_listeningRow is { } row)
        {
            row.IsListening = false;
            _listeningRow = null;
        }
        if (_listeningHotkey)
        {
            _listeningHotkey = false;
            OnPropertyChanged(nameof(HotkeyText));
        }
    }

    /// <summary>A key or mouse button pressed in the window while waiting for one.</summary>
    /// <param name="code">A <see cref="KeyNames"/> code (virtual-key code, or a wheel code).</param>
    public void HandleKey(int code)
    {
        if (!IsListening) return;
        bool forHotkey = _listeningHotkey;
        var row = _listeningRow;
        CancelListening();

        string? name = KeyNames.Name(code);
        if (forHotkey)
        {
            if (name is null || KeyNames.IsMouse(code)) KbmNote = "The hotkey has to be a keyboard key.";
            else SetHotkey(name);
            return;
        }

        if (row is null) return;
        if (name is null)
        {
            KbmNote = "That key can't be used. Pick another one.";
            return;
        }
        if (code == _settings.CaptureHotkeyCode)
        {
            KbmNote = $"{HotkeyDisplay} is the capture hotkey. Pick another key, or change the hotkey.";
            return;
        }

        KbmNote = "";
        foreach (var other in KbmRows)
        {
            if (other == row || !string.Equals(other.Key, name, StringComparison.OrdinalIgnoreCase)) continue;
            other.Key = "";
            KbmNote = $"{KeyNames.Display(name)} moved from {other.Display.Label} to {row.Display.Label}.";
        }
        row.Key = name;
        ProfileEdited();
    }

    private void SetHotkey(string name)
    {
        _settings.CaptureHotkey = name;
        SaveSettings();
        ApplyCaptureOptions();
        foreach (var row in KbmRows)
        {
            if (!string.Equals(row.Key, name, StringComparison.OrdinalIgnoreCase)) continue;
            row.Key = "";
            KbmNote = $"{KeyNames.Display(name)} is now the hotkey, so it no longer presses {row.Display.Label}.";
            ProfileEdited();
        }
        OnPropertyChanged(nameof(HotkeyText));
        OnPropertyChanged(nameof(CaptureHint));
        UpdateKbmStatus();
    }

    public void ClearKey(KbmRowViewModel row)
    {
        CancelListening();
        KbmNote = "";
        if (!row.HasKey) return;
        row.Key = "";
        ProfileEdited();
    }

    public void ResetKeys()
    {
        CancelListening();
        var defaults = KbmSettings.DefaultBindings();
        string hotkey = HotkeyName;
        foreach (var row in KbmRows)
        {
            string key = defaults.TryGetValue(row.Action, out var k) ? k : "";
            row.Key = string.Equals(key, hotkey, StringComparison.OrdinalIgnoreCase) ? "" : key;
        }
        KbmNote = "";
        ProfileEdited();
    }

    // ---- Options ----

    private bool _launchWithWindows;

    public bool StartMappingOnLaunch
    {
        get => _settings.StartMappingOnLaunch;
        set { _settings.StartMappingOnLaunch = value; SaveSettings(); OnPropertyChanged(); }
    }

    public bool StartMinimized
    {
        get => _settings.StartMinimized;
        set { _settings.StartMinimized = value; SaveSettings(); OnPropertyChanged(); }
    }

    public bool CloseToTray
    {
        get => _settings.CloseToTray;
        set { _settings.CloseToTray = value; SaveSettings(); OnPropertyChanged(); }
    }

    public bool LaunchWithWindows
    {
        get => _launchWithWindows;
        set
        {
            try
            {
                Autostart.Set(value);
                _launchWithWindows = value;
            }
            catch (Exception ex)
            {
                AddLog($"Couldn't change \"Launch with Windows\": {ex.Message}");
            }
            OnPropertyChanged();
        }
    }

    public string VersionText => $"Version {AppInfo.Version}";
    public bool ShowProjectLink => AppInfo.ProjectUrl.Length > 0;
    public bool ShowDonate => AppInfo.DonateUrl.Length > 0;

    // ---- Profiles ----

    public string SelectedProfileName
    {
        get => _profile.Name;
        set
        {
            if (value is null || value == _profile.Name) return;
            SaveNow();
            LoadProfile(value);
        }
    }

    private void LoadProfile(string name)
    {
        _saveTimer.Stop();
        CancelListening();
        KbmNote = "";
        _profile = ProfileStore.Load(name);
        LoadIntoUi(_profile);
        _settings.ActiveProfile = name;
        SaveSettings();
        OnPropertyChanged(nameof(SelectedProfileName));
    }

    /// <summary>Puts a profile's values into the controls without them counting as edits.</summary>
    private void LoadIntoUi(Profile p)
    {
        _loading = true;
        try
        {
            foreach (var row in Buttons)
                row.Load(p.Buttons.TryGetValue(row.Input, out var o) ? o : DsOutput.None);
            Combos.Clear();
            foreach (var c in p.Combos) Combos.Add(ComboViewModel.From(c, ProfileEdited));
            SwapSticks = p.SwapSticks;
            LeftInvertX = p.LeftStick.InvertX;
            LeftInvertY = p.LeftStick.InvertY;
            LeftDeadzone = p.LeftStick.Deadzone;
            RightInvertX = p.RightStick.InvertX;
            RightInvertY = p.RightStick.InvertY;
            RightDeadzone = p.RightStick.Deadzone;
            TriggerThreshold = p.TriggerThreshold;
            RumbleEnabled = p.RumbleEnabled;
            RumbleStrength = p.RumbleStrength;

            var k = p.Kbm;
            foreach (var row in KbmRows)
                row.Key = k.Bindings.TryGetValue(row.Action, out var key) ? key : "";
            KbmEnabled = k.Enabled;
            MouseTarget = k.Mouse;
            MouseSensitivity = k.MouseSensitivity;
            MouseCurve = k.MouseCurve;
            MouseAntiDeadzone = k.MouseAntiDeadzone;
            InvertMouseY = k.InvertMouseY;
        }
        finally { _loading = false; }
        _compiled = CompiledProfile.From(p);
        _bridge.SetProfile(p);
    }

    private Profile BuildProfile() => new Profile
    {
        Name = _profile.Name,
        Buttons = Buttons.ToDictionary(r => r.Input, r => r.Output),
        Combos = Combos.Select(c => c.ToCombo()).ToList(),
        SwapSticks = SwapSticks,
        LeftStick = new StickSettings { InvertX = LeftInvertX, InvertY = LeftInvertY, Deadzone = LeftDeadzone },
        RightStick = new StickSettings { InvertX = RightInvertX, InvertY = RightInvertY, Deadzone = RightDeadzone },
        TriggerThreshold = TriggerThreshold,
        RumbleEnabled = RumbleEnabled,
        RumbleStrength = RumbleStrength,
        Kbm = new KbmSettings
        {
            Enabled = KbmEnabled,
            Bindings = KbmRows.ToDictionary(r => r.Action, r => r.Key),
            Mouse = MouseTarget,
            MouseSensitivity = MouseSensitivity,
            MouseCurve = MouseCurve,
            MouseAntiDeadzone = MouseAntiDeadzone,
            InvertMouseY = InvertMouseY,
        },
    }.Normalize();

    /// <summary>Any control changed: apply now (also while mapping), save shortly.</summary>
    private void ProfileEdited()
    {
        if (_loading) return;
        _profile = BuildProfile();
        _compiled = CompiledProfile.From(_profile);
        _bridge.SetProfile(_profile);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        _saveTimer.Stop();
        try { ProfileStore.Save(_profile); }
        catch (Exception ex) { AddLog($"Couldn't save profile \"{_profile.Name}\": {ex.Message}"); }
    }

    private void SaveSettings()
    {
        try { _settings.Save(); }
        catch (Exception ex) { AddLog($"Couldn't save settings: {ex.Message}"); }
    }

    private void InsertName(string name)
    {
        int i = 0;
        while (i < ProfileNames.Count && string.Compare(ProfileNames[i], name, StringComparison.OrdinalIgnoreCase) < 0) i++;
        ProfileNames.Insert(i, name);
    }

    /// <returns>An error message, or null.</returns>
    private static string? CheckNewName(string name, string? current = null)
    {
        if (ProfileStore.ValidateName(name) is string problem) return problem;
        bool sameAsCurrent = current is not null && string.Equals(name.Trim(), current, StringComparison.OrdinalIgnoreCase);
        if (!sameAsCurrent && ProfileStore.Exists(name.Trim())) return "A profile with that name already exists.";
        return null;
    }

    public string? NewProfile(string name)
    {
        if (CheckNewName(name) is string error) return error;
        name = name.Trim();
        SaveNow();
        ProfileStore.Save(Profile.CreateDefault(name));
        InsertName(name);
        LoadProfile(name);
        return null;
    }

    public string? DuplicateProfile(string name)
    {
        if (CheckNewName(name) is string error) return error;
        name = name.Trim();
        SaveNow();
        var copy = _profile.Clone();
        copy.Name = name;
        ProfileStore.Save(copy);
        InsertName(name);
        LoadProfile(name);
        return null;
    }

    public string? RenameProfile(string name)
    {
        if (CheckNewName(name, _profile.Name) is string error) return error;
        name = name.Trim();
        if (name == _profile.Name) return null;
        string old = _profile.Name;
        SaveNow();
        ProfileStore.Rename(old, name);
        int index = ProfileNames.IndexOf(old);
        _profile.Name = name;
        if (index >= 0) ProfileNames.RemoveAt(index);
        InsertName(name);
        _settings.ActiveProfile = name;
        SaveSettings();
        OnPropertyChanged(nameof(SelectedProfileName));
        return null;
    }

    public string? DeleteProfile()
    {
        if (ProfileNames.Count <= 1) return "You need at least one profile.";
        _saveTimer.Stop();
        string name = _profile.Name;
        ProfileStore.Delete(name);
        ProfileNames.Remove(name);
        LoadProfile(ProfileNames[0]);
        return null;
    }

    public string? ImportProfile(string path)
    {
        Profile p;
        try { p = Profile.FromJson(File.ReadAllText(path)); }
        catch (Exception ex) { return $"That file isn't an xbox2ps profile.\n\n{ex.Message}"; }

        string wanted = ProfileStore.ValidateName(p.Name) is null && p.Name != "Default"
            ? p.Name
            : Path.GetFileNameWithoutExtension(path);
        p.Name = ProfileStore.UniqueName(wanted);
        SaveNow();
        ProfileStore.Save(p);
        InsertName(p.Name);
        LoadProfile(p.Name);
        return null;
    }

    public string? ExportProfile(string path)
    {
        try
        {
            SaveNow();
            File.WriteAllText(path, _profile.ToJson());
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>True the first time only, so the "still running in the tray" hint shows once.</summary>
    public bool TakeTrayHint()
    {
        if (_settings.TrayHintShown) return false;
        _settings.TrayHintShown = true;
        SaveSettings();
        return true;
    }

    public void ResetProfile()
    {
        CancelListening();
        KbmNote = "";
        _profile = Profile.CreateDefault(_profile.Name);
        LoadIntoUi(_profile);
        SaveNow();
    }

    public string CurrentProfileName => _profile.Name;

    public void AddCombo()
    {
        Combos.Add(ComboViewModel.From(new Combo(XboxInput.LB, XboxInput.RB, DsOutput.Touchpad), ProfileEdited));
        ProfileEdited();
    }

    public void RemoveCombo(ComboViewModel combo)
    {
        if (Combos.Remove(combo)) ProfileEdited();
    }

    // ---- Mapping on/off ----

    public void StartMapping()
    {
        if (_bridge.IsActive) return;
        _lastError = null;
        _bridge.SetProfile(_profile);
        _bridge.Start(_settings.Slot);
        IsActive = true;
        UpdateStatus();
    }

    public Task StopMappingAsync() => Task.Run(_bridge.Stop);

    public async void ToggleMapping()
    {
        if (IsActive) await StopMappingAsync();
        else StartMapping();
    }

    public void Retry()
    {
        _viiperInstalled = Viiper.IsInstalled;
        ProbeViiper();
        if (!IsActive && _lastError is not null) StartMapping();
    }

    /// <summary>Stops mapping and saves; call once when the app exits.</summary>
    public void Shutdown()
    {
        _liveTimer.Stop();
        _probeTimer.Stop();
        SaveNow();
        _bridge.Stop();
    }

    private void OnBridgeState(BridgeState state, string? message)
    {
        if (state == BridgeState.Error) _lastError = message;
        IsActive = state is BridgeState.Starting or BridgeState.WaitingForController or BridgeState.Running;
        if (state is BridgeState.Running or BridgeState.WaitingForController) _viiperReachable = true;
        UpdateStatus();
    }

    // ---- Status ----

    private void ProbeViiper()
    {
        if (_probing || _bridge.State == BridgeState.Running) return;
        _probing = true;
        Task.Run(() => (installed: Viiper.IsInstalled, reachable: Viiper.Ping()))
            .ContinueWith(t => _ui.BeginInvoke(() =>
            {
                _probing = false;
                if (t.IsCompletedSuccessfully) (_viiperInstalled, _viiperReachable) = t.Result;
                UpdateStatus();
            }));
    }

    private void UpdateStatus()
    {
        var state = _bridge.State;

        if (state is BridgeState.Running or BridgeState.WaitingForController || _viiperReachable)
        { ViiperLevel = StatusLevel.Ok; ViiperText = "Running"; }
        else if (!_viiperInstalled)
        { ViiperLevel = StatusLevel.Bad; ViiperText = "Not installed"; }
        else
        { ViiperLevel = StatusLevel.Warn; ViiperText = "Not running"; }

        bool full = _bridge.IsFullDualSense != false;
        (PadLevel, PadText, StatusSummary) = state switch
        {
            BridgeState.Starting => (StatusLevel.Warn, "Starting…", "Starting…"),
            BridgeState.WaitingForController => (StatusLevel.Warn, "Waiting for controller", "Waiting for an Xbox controller"),
            BridgeState.Running when full => (StatusLevel.Ok, "Active", _bridge.CurrentSlot >= 0
                ? $"Mapping controller {_bridge.CurrentSlot + 1} to the virtual DualSense"
                : "Mapping the keyboard and mouse to the virtual DualSense"),
            BridgeState.Running => (StatusLevel.Warn, "Active (basic)", "Mapping, but PS Remote Play needs a newer VIIPER"),
            BridgeState.Error => (StatusLevel.Bad, "Error", $"Error: {_lastError}"),
            _ => (StatusLevel.Off, "Off", "Stopped"),
        };

        if (!_viiperInstalled && !_viiperReachable && state != BridgeState.Running)
        {
            ShowBanner = true;
            BannerShowsDownload = true;
            BannerTitle = "VIIPER isn't installed";
            BannerText = "xbox2ps needs VIIPER to create the virtual DualSense. Download viiper.exe (for PS Remote Play, " +
                         "the dev-snapshot build), run \"viiper.exe install\" in a terminal, then restart Windows.";
        }
        else if (state == BridgeState.Error)
        {
            ShowBanner = true;
            BannerShowsDownload = !_viiperInstalled;
            BannerTitle = "Couldn't start mapping";
            BannerText = _lastError + " Check that VIIPER is installed and its server is running, then press Retry.";
        }
        else if (_bridge.IsFullDualSense == false)
        {
            ShowBanner = true;
            BannerShowsDownload = true;
            BannerTitle = "Update VIIPER for PS Remote Play";
            BannerText = "Your VIIPER version only offers the basic DualSense, which PS Remote Play doesn't detect. " +
                         "Install a VIIPER build with the full DualSense (currently the dev snapshot), then restart mapping.";
        }
        else
        {
            ShowBanner = false;
        }

        UpdateKbmStatus();
    }

    // ---- Live preview ----

    private void LiveTick()
    {
        XInput.XINPUT_STATE st = default;
        bool ok = false;
        int slot = _bridge.CurrentSlot;
        if (slot >= 0)
        {
            ok = XInput.TryGetState((uint)slot, out st);
        }
        else
        {
            // Scanning empty slots is slow, so only rescan once a second.
            if (_previewSlot >= 0 && (_settings.Slot is null || _settings.Slot == _previewSlot))
                ok = XInput.TryGetState((uint)_previewSlot, out st);
            if (!ok)
            {
                _previewSlot = -1;
                long now = Environment.TickCount64;
                if (now >= _nextScan)
                {
                    _nextScan = now + 1000;
                    _previewSlot = XInput.FindController(_settings.Slot);
                    if (_previewSlot >= 0) ok = XInput.TryGetState((uint)_previewSlot, out st);
                }
            }
            slot = _previewSlot;
        }

        ControllerLevel = ok ? StatusLevel.Ok : StatusLevel.Off;
        ControllerText = ok ? $"Controller {slot + 1}" : "Not connected";

        // The controller side: highlighted rows and combos, and the grey stick dots.
        uint packetNumber = ok ? st.dwPacketNumber : uint.MaxValue;
        if (packetNumber != _lastPreviewPacket || !ReferenceEquals(_lastPreviewCompiled, _compiled))
        {
            _lastPreviewPacket = packetNumber;
            _lastPreviewCompiled = _compiled;
            if (ok) Mapper.Build(_controllerPreview, st.Gamepad, _compiled);
            else Mapper.Neutral(_controllerPreview);
            ShowController(ok ? st.Gamepad : default, ok);
        }

        // The DualSense side: while mapping, exactly what was last sent (keyboard and mouse
        // included); otherwise what the controller alone would send.
        if (!_bridge.TryCopyOutput(_preview)) Array.Copy(_controllerPreview, _preview, Mapper.PacketSize);
        if (!_preview.AsSpan().SequenceEqual(_shown))
        {
            _preview.CopyTo(_shown, 0);
            ShowOutput(_preview);
        }
    }

    private void ShowController(XInput.XINPUT_GAMEPAD g, bool connected)
    {
        foreach (var row in Buttons)
            row.IsHeld = connected && Mapper.IsPressed(g, row.Input, TriggerThreshold);
        foreach (var combo in Combos)
            combo.IsActive = connected && combo.First != combo.Second &&
                             Mapper.IsPressed(g, combo.First, TriggerThreshold) && Mapper.IsPressed(g, combo.Second, TriggerThreshold);

        double Raw(short v) => StickHalf + v / 32768.0 * StickHalf;
        LeftRawX = Raw(g.sThumbLX);
        LeftRawY = Raw((short)Math.Clamp(-g.sThumbLY, short.MinValue, short.MaxValue));
        RightRawX = Raw(g.sThumbRX);
        RightRawY = Raw((short)Math.Clamp(-g.sThumbRY, short.MinValue, short.MaxValue));
    }

    private void ShowOutput(byte[] packet)
    {
        var pressed = Mapper.PressedOutputs(packet);
        if (!pressed.SequenceEqual(PressedOutputs.Select(o => o.Value)))
        {
            PressedOutputs.Clear();
            foreach (var o in pressed) PressedOutputs.Add(Labels.OptionOf(o));
        }
        NothingPressed = pressed.Count == 0;

        double Out(byte b) => StickHalf + (sbyte)b / 128.0 * StickHalf;
        LeftOutX = Out(packet[0]);
        LeftOutY = Out(packet[1]);
        RightOutX = Out(packet[2]);
        RightOutY = Out(packet[3]);
        L2Value = packet[9];
        R2Value = packet[10];
    }

    // ---- Log ----

    public void AddLog(string message)
    {
        LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        while (LogLines.Count > MaxLogLines) LogLines.RemoveAt(0);
    }

    public string LogText => string.Join(Environment.NewLine, LogLines);
}

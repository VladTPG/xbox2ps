using Xbox2Ps.Core;

namespace Xbox2Ps.Tests;

public class ProfileTests
{
    [Fact]
    public void Json_RoundTrips()
    {
        var p = Profile.CreateDefault("Mine");
        p.Buttons[XboxInput.A] = DsOutput.Circle;
        p.Combos.Add(new Combo(XboxInput.LB, XboxInput.RB, DsOutput.Mute));
        p.LeftStick.Deadzone = 12;
        p.RightStick.InvertY = true;
        p.SwapSticks = true;
        p.TriggerThreshold = 80;
        p.RumbleStrength = 40;

        var back = Profile.FromJson(p.ToJson());
        Assert.Equal(p.ToJson(), back.ToJson());
        Assert.Equal(DsOutput.Circle, back.Buttons[XboxInput.A]);
    }

    [Fact]
    public void Json_UsesReadableNames()
    {
        string json = Profile.CreateDefault().ToJson();
        Assert.Contains("\"A\": \"Cross\"", json);
        Assert.Contains("\"Output\": \"Touchpad\"", json);
    }

    [Fact]
    public void MissingFields_GetDefaults()
    {
        var p = Profile.FromJson("""{ "Name": "Partial", "Buttons": { "A": "Circle" } }""");
        Assert.Equal(DsOutput.Circle, p.Buttons[XboxInput.A]);
        Assert.Equal(DsOutput.Circle, p.Buttons[XboxInput.B]);
        Assert.Equal(DsOutput.PS, p.Buttons[XboxInput.Guide]);
        Assert.Equal(17, p.Buttons.Count);
        Assert.Single(p.Combos);
        Assert.Equal(30, p.TriggerThreshold);
        Assert.True(p.RumbleEnabled);
    }

    [Fact]
    public void EmptyCombos_StayEmpty()
    {
        var p = Profile.FromJson("""{ "Combos": [] }""");
        Assert.Empty(p.Combos);
    }

    [Fact]
    public void UnknownFields_AreIgnored()
    {
        var p = Profile.FromJson("""{ "Name": "X", "SomethingNew": 42, "LeftStick": { "Future": true } }""");
        Assert.Equal("X", p.Name);
    }

    [Fact]
    public void Normalize_ClampsAndDropsBrokenCombos()
    {
        var p = Profile.FromJson("""
            {
              "TriggerThreshold": 999,
              "RumbleStrength": 150,
              "LeftStick": { "Deadzone": -5 },
              "RightStick": { "Deadzone": 80 },
              "Combos": [ { "First": "A", "Second": "A", "Output": "PS" }, { "First": "A", "Second": "B", "Output": "PS" } ]
            }
            """);
        Assert.Equal(Profile.MaxThreshold, p.TriggerThreshold);
        Assert.Equal(100, p.RumbleStrength);
        Assert.Equal(0, p.LeftStick.Deadzone);
        Assert.Equal(Profile.MaxDeadzone, p.RightStick.Deadzone);
        Assert.Single(p.Combos);
    }

    [Fact]
    public void NotAProfile_Throws()
    {
        Assert.ThrowsAny<Exception>(() => Profile.FromJson("not json"));
        Assert.ThrowsAny<Exception>(() => Profile.FromJson("""{ "Buttons": { "A": "Banana" } }"""));
    }

    [Fact]
    public void Clone_IsIndependent()
    {
        var p = Profile.CreateDefault();
        var c = p.Clone();
        c.Buttons[XboxInput.A] = DsOutput.None;
        Assert.Equal(DsOutput.Cross, p.Buttons[XboxInput.A]);
    }
}

public class ProfileStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "xbox2ps-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _previous = AppSettings.Folder;

    public ProfileStoreTests() => AppSettings.Folder = _folder;

    public void Dispose()
    {
        AppSettings.Folder = _previous;
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void SaveLoadRenameDelete()
    {
        Assert.Equal(new[] { "Default" }, ProfileStore.EnsureAny());

        var p = Profile.CreateDefault("Racing");
        p.Buttons[XboxInput.RT] = DsOutput.Cross;
        ProfileStore.Save(p);
        Assert.Equal(new[] { "Default", "Racing" }, ProfileStore.List());
        Assert.Equal(DsOutput.Cross, ProfileStore.Load("Racing").Buttons[XboxInput.RT]);

        ProfileStore.Rename("Racing", "Driving");
        Assert.Equal(new[] { "Default", "Driving" }, ProfileStore.List());
        Assert.Equal("Driving", ProfileStore.Load("Driving").Name);

        ProfileStore.Delete("Driving");
        Assert.Equal(new[] { "Default" }, ProfileStore.List());
    }

    [Fact]
    public void BrokenFile_LoadsAsDefaults()
    {
        Directory.CreateDirectory(ProfileStore.Folder);
        File.WriteAllText(Path.Combine(ProfileStore.Folder, "Bad.json"), "{ nope");
        var p = ProfileStore.Load("Bad");
        Assert.Equal("Bad", p.Name);
        Assert.Equal(DsOutput.Cross, p.Buttons[XboxInput.A]);
    }

    [Fact]
    public void UniqueName_AddsANumber()
    {
        ProfileStore.Save(Profile.CreateDefault("Mine"));
        Assert.Equal("Mine (2)", ProfileStore.UniqueName("Mine"));
        Assert.Equal("Other", ProfileStore.UniqueName("Other"));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("a/b", false)]
    [InlineData("name.", false)]
    [InlineData("Racing games", true)]
    public void ValidateName(string name, bool ok) => Assert.Equal(ok, ProfileStore.ValidateName(name) is null);

    [Fact]
    public void Settings_RoundTrip()
    {
        var s = new AppSettings { ActiveProfile = "Racing", Slot = 2, StartMinimized = true };
        s.Save();
        var back = AppSettings.Load();
        Assert.Equal("Racing", back.ActiveProfile);
        Assert.Equal(2, back.Slot);
        Assert.True(back.StartMinimized);
    }
}

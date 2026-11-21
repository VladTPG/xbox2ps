using System.Buffers.Binary;
using Xbox2Ps.Core;
using Pad = Xbox2Ps.Core.XInput.XINPUT_GAMEPAD;

namespace Xbox2Ps.Tests;

public class MouseStickTests
{
    private const double StepMs = 4;

    private static KbmSettings Settings(int sensitivity = 50, int curve = 50, int anti = 0, bool invertY = false) =>
        new() { MouseSensitivity = sensitivity, MouseCurve = curve, MouseAntiDeadzone = anti, InvertMouseY = invertY };

    /// <summary>Feeds the same movement for a number of steps and returns the last deflection.</summary>
    private static (double X, double Y) Run(ref MouseStickState s, int dx, int dy, int steps, KbmSettings k)
    {
        (double, double) last = (0, 0);
        for (int i = 0; i < steps; i++) last = MouseStick.Step(ref s, dx, dy, StepMs, k);
        return last;
    }

    private static double Length((double X, double Y) v) => Math.Sqrt(v.X * v.X + v.Y * v.Y);

    [Fact]
    public void NoMovement_StaysCentred()
    {
        var s = new MouseStickState();
        Assert.Equal((0.0, 0.0), Run(ref s, 0, 0, 50, Settings(anti: 20)));
    }

    [Fact]
    public void SmallMovement_JumpsPastTheAntiDeadzone()
    {
        var s = new MouseStickState();
        var v = MouseStick.Step(ref s, 1, 0, StepMs, Settings(anti: 20));
        Assert.InRange(v.X, 0.2, 0.3);
        Assert.Equal(0, v.Y, 9);
    }

    [Fact]
    public void FastMovement_ClampsAtFullTilt()
    {
        var s = new MouseStickState();
        var v = Run(ref s, 0, -2000, 10, Settings(anti: 20));
        Assert.Equal(-1, v.Y, 6);
        Assert.InRange(Length(v), 0.999, 1.000001);
    }

    [Fact]
    public void Direction_IsKept()
    {
        var s = new MouseStickState();
        var v = Run(ref s, 3, -4, 20, Settings());
        Assert.True(v.X > 0 && v.Y < 0);
        Assert.Equal(-0.75, v.X / v.Y, 6);
    }

    [Fact]
    public void Stopping_ReturnsToCentreWithinAFewSteps()
    {
        var s = new MouseStickState();
        Run(ref s, 40, 0, 20, Settings(anti: 20));
        var v = Run(ref s, 0, 0, 25, Settings(anti: 20)); // 100 ms
        Assert.Equal((0.0, 0.0), v);
    }

    [Fact]
    public void InvertY_FlipsVerticalOnly()
    {
        var a = new MouseStickState();
        var b = new MouseStickState();
        var normal = Run(ref a, 2, 3, 10, Settings());
        var inverted = Run(ref b, 2, 3, 10, Settings(invertY: true));
        Assert.Equal(normal.X, inverted.X, 9);
        Assert.Equal(-normal.Y, inverted.Y, 9);
    }

    [Fact]
    public void HigherSensitivity_TiltsFurther()
    {
        var a = new MouseStickState();
        var b = new MouseStickState();
        double low = Length(Run(ref a, 2, 0, 20, Settings(sensitivity: 20)));
        double high = Length(Run(ref b, 2, 0, 20, Settings(sensitivity: 80)));
        Assert.True(high > low, $"{high} should be more than {low}");
    }

    [Fact]
    public void Curve_BelowFiftyReactsMoreToSmallMovements()
    {
        var a = new MouseStickState();
        var b = new MouseStickState();
        var c = new MouseStickState();
        double soft = Length(Run(ref a, 2, 0, 20, Settings(curve: 0)));
        double linear = Length(Run(ref b, 2, 0, 20, Settings(curve: 50)));
        double fine = Length(Run(ref c, 2, 0, 20, Settings(curve: 100)));
        Assert.True(soft > linear && linear > fine, $"{soft} > {linear} > {fine}");
    }

    [Fact]
    public void OddTimings_NeverGiveNaN()
    {
        var s = new MouseStickState();
        foreach (double dt in new[] { 0.0, -5, 0.001, 1000 })
        {
            var v = MouseStick.Step(ref s, 7, -3, dt, Settings(anti: 20));
            Assert.False(double.IsNaN(v.X) || double.IsNaN(v.Y));
            Assert.InRange(Length(v), 0, 1.000001);
        }
    }
}

public class KbmMappingTests
{
    private static Profile KbmProfile(Action<KbmSettings>? edit = null)
    {
        var p = Profile.CreateDefault();
        p.Kbm.Enabled = true;
        edit?.Invoke(p.Kbm);
        return p.Normalize();
    }

    private static KbmFrame Keys(params string[] names)
    {
        var f = new KbmFrame { Captured = true };
        foreach (var n in names) f.Down[KeyNames.Code(n)] = true;
        return f;
    }

    private static byte[] Apply(KbmFrame frame, Profile? p = null, Pad pad = default, (double, double) mouse = default)
    {
        var packet = new byte[Mapper.PacketSize];
        var c = CompiledProfile.From(p ?? KbmProfile());
        Mapper.Build(packet, pad, c);
        Mapper.ApplyKbm(packet, frame, c, mouse);
        return packet;
    }

    private static uint Buttons(byte[] packet) => BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4));
    private static sbyte Axis(byte[] packet, int i) => (sbyte)packet[i];

    // An idle controller isn't exactly 0 on every axis (Y rests at -1, as in the original
    // console version), so keyboard and mouse results are checked against that resting value.
    private static int Rest(int i) => Axis(Apply(new KbmFrame()), i);
    private static int Plus(int i, int add) => Math.Clamp(Rest(i) + add, -128, 127);

    [Fact]
    public void NotCaptured_ChangesNothing()
    {
        var frame = Keys("Space", "W", "MouseLeft");
        frame.Captured = false;
        var withKeys = Apply(frame, mouse: (1, 1));
        var without = Apply(new KbmFrame());
        Assert.Equal(without, withKeys);
    }

    [Fact]
    public void KbmOff_ChangesNothing()
    {
        var off = Profile.CreateDefault().Normalize();
        Assert.Equal(Apply(new KbmFrame(), off), Apply(Keys("Space", "W"), off, mouse: (1, 0)));
    }

    [Fact]
    public void Space_PressesCross()
    {
        var p = Apply(Keys("Space"));
        Assert.Equal(Mapper.Cross, Buttons(p));
    }

    [Fact]
    public void Buttons_CombineWithTheController()
    {
        var p = Apply(Keys("Space"), pad: new Pad { wButtons = XInput.B });
        Assert.Equal(Mapper.Cross | Mapper.Circle, Buttons(p));
    }

    [Fact]
    public void LeftClick_PullsR2AllTheWay()
    {
        var p = Apply(Keys("MouseLeft"));
        Assert.Equal(Mapper.R2, Buttons(p));
        Assert.Equal(255, p[10]);
        Assert.Equal(0, p[9]);
    }

    [Fact]
    public void Triggers_TakeTheDeeperOfTheTwo()
    {
        var pad = new Pad { bRightTrigger = 100 };
        Assert.Equal(100, Apply(Keys(), pad: pad)[10]);
        Assert.Equal(255, Apply(Keys("MouseLeft"), pad: pad)[10]);
    }

    [Fact]
    public void W_PushesTheLeftStickUp()
    {
        var p = Apply(Keys("W"));
        Assert.Equal(Plus(1, -127), Axis(p, 1));
        Assert.Equal(Rest(0), Axis(p, 0));
    }

    [Fact]
    public void OppositeKeys_Cancel()
    {
        var p = Apply(Keys("W", "S", "A", "D"));
        Assert.Equal(Rest(0), Axis(p, 0));
        Assert.Equal(Rest(1), Axis(p, 1));
    }

    [Fact]
    public void Diagonals_StayOnTheCircle()
    {
        var p = Apply(Keys("W", "D"));
        Assert.Equal(Plus(0, 90), Axis(p, 0));
        Assert.Equal(Plus(1, -90), Axis(p, 1));
    }

    [Fact]
    public void Sticks_AddUpAndClamp()
    {
        var pad = new Pad { sThumbLY = short.MaxValue }; // fully up on the controller
        var p = Apply(Keys("W"), pad: pad);
        Assert.Equal(-128, Axis(p, 1));
        var q = Apply(Keys("S"), pad: pad); // keyboard down cancels most of it
        Assert.InRange(Axis(q, 1), -2, 0);
    }

    [Fact]
    public void Mouse_MovesTheChosenStick()
    {
        var right = Apply(Keys(), mouse: (0.5, -1));
        Assert.Equal(Plus(2, 64), Axis(right, 2));
        Assert.Equal(Plus(3, -127), Axis(right, 3));
        Assert.Equal(Rest(0), Axis(right, 0));

        var left = Apply(Keys(), KbmProfile(k => k.Mouse = MouseTarget.LeftStick), mouse: (0.5, -1));
        Assert.Equal(Plus(0, 64), Axis(left, 0));
        Assert.Equal(Rest(2), Axis(left, 2));

        var off = Apply(Keys(), KbmProfile(k => k.Mouse = MouseTarget.Off), mouse: (0.5, -1));
        Assert.Equal(Apply(Keys()), off);
    }

    [Fact]
    public void ArrowKeys_PressTheDpad()
    {
        var p = Apply(Keys("Up", "Left"));
        Assert.Equal(Mapper.DpadUp | Mapper.DpadLeft, p[8]);
    }

    [Fact]
    public void UnboundKeys_DoNothing()
    {
        Assert.Equal(Apply(Keys()), Apply(Keys("Z", "F12", "MouseMiddle")));
    }

    [Fact]
    public void Rebinding_Works_IncludingTheWheel()
    {
        var p = KbmProfile(k =>
        {
            k.Bindings[KbmAction.Cross] = "J";
            k.Bindings[KbmAction.R1] = "WheelUp";
            k.Bindings[KbmAction.RightStickLeft] = "Left";
        });
        Assert.Equal(0u, Buttons(Apply(Keys("Space"), p)));
        Assert.Equal(Mapper.Cross, Buttons(Apply(Keys("J"), p)));
        Assert.Equal(Mapper.R1, Buttons(Apply(Keys("WheelUp"), p)));

        var left = Apply(Keys("Left"), p); // also still the d-pad
        Assert.Equal(Plus(2, -127), Axis(left, 2));
        Assert.Equal(Mapper.DpadLeft, left[8]);
    }

    [Fact]
    public void KbmCodes_ListTheBoundKeysOnce()
    {
        var c = CompiledProfile.From(KbmProfile(k => k.Bindings[KbmAction.Triangle] = "Space"));
        var codes = c.KbmCodes.ToList();
        Assert.Equal(codes.Distinct().Count(), codes.Count);
        Assert.Contains(KeyNames.Code("Space"), codes);
        Assert.Contains(KeyNames.Code("MouseLeft"), codes);
        Assert.DoesNotContain(KeyNames.Code("Z"), codes);
    }
}

public class KbmProfileTests
{
    [Fact]
    public void Defaults_AreOff_WithAShooterLayout()
    {
        var k = Profile.CreateDefault().Normalize().Kbm;
        Assert.False(k.Enabled);
        Assert.Equal(MouseTarget.RightStick, k.Mouse);
        Assert.Equal("W", k.Bindings[KbmAction.LeftStickUp]);
        Assert.Equal("MouseLeft", k.Bindings[KbmAction.R2]);
        Assert.Equal(Enum.GetValues<KbmAction>().Length, k.Bindings.Count);
    }

    [Fact]
    public void Json_RoundTrips()
    {
        var p = Profile.CreateDefault("Keys");
        p.Kbm.Enabled = true;
        p.Kbm.Bindings[KbmAction.Cross] = "J";
        p.Kbm.Bindings[KbmAction.Mute] = "WheelDown";
        p.Kbm.Mouse = MouseTarget.LeftStick;
        p.Kbm.MouseSensitivity = 72;
        p.Kbm.MouseCurve = 30;
        p.Kbm.MouseAntiDeadzone = 12;
        p.Kbm.InvertMouseY = true;

        string json = p.ToJson();
        var back = Profile.FromJson(json);
        Assert.Equal(json, back.ToJson());
        Assert.True(back.Kbm.Enabled);
        Assert.Equal("J", back.Kbm.Bindings[KbmAction.Cross]);
        Assert.Contains("\"Cross\": \"J\"", json);
        Assert.Contains("\"Mouse\": \"LeftStick\"", json);
    }

    [Fact]
    public void A10Profile_WithoutKbm_LoadsWithKbmOff()
    {
        var p = Profile.FromJson("""{ "Name": "Old", "Buttons": { "A": "Circle" }, "SwapSticks": true }""");
        Assert.False(p.Kbm.Enabled);
        Assert.Equal("Space", p.Kbm.Bindings[KbmAction.Cross]);
        Assert.Equal(50, p.Kbm.MouseSensitivity);
    }

    [Fact]
    public void Normalize_FixesBadValues()
    {
        var p = Profile.FromJson("""
            { "Kbm": { "Enabled": true, "Bindings": { "Cross": "NotAKey", "Circle": "space", "L1": null },
                       "MouseSensitivity": 0, "MouseCurve": 500, "MouseAntiDeadzone": 90 } }
            """);
        var k = p.Kbm;
        Assert.Equal("", k.Bindings[KbmAction.Cross]);
        Assert.Equal("Space", k.Bindings[KbmAction.Circle]);
        Assert.Equal("", k.Bindings[KbmAction.L1]);
        Assert.Equal("F", k.Bindings[KbmAction.Triangle]); // missing ones get the default
        Assert.Equal(1, k.MouseSensitivity);
        Assert.Equal(100, k.MouseCurve);
        Assert.Equal(KbmSettings.MaxAntiDeadzone, k.MouseAntiDeadzone);
    }

    [Fact]
    public void Clone_CopiesKbm()
    {
        var p = Profile.CreateDefault();
        p.Kbm.Enabled = true;
        var copy = p.Clone();
        copy.Kbm.Bindings[KbmAction.Cross] = "J";
        Assert.True(copy.Kbm.Enabled);
        Assert.Equal("Space", p.Kbm.Bindings[KbmAction.Cross]);
    }
}

public class KeyNamesTests
{
    [Fact]
    public void EveryName_RoundTrips()
    {
        int count = 0;
        for (int code = 0; code < KeyNames.CodeCount; code++)
        {
            if (KeyNames.Name(code) is not string name) continue;
            count++;
            Assert.Equal(code, KeyNames.Code(name));
            Assert.Equal(code, KeyNames.Code(name.ToLowerInvariant()));
            Assert.NotEqual("—", KeyNames.Display(name));
        }
        Assert.True(count > 100, $"only {count} names");
    }

    [Fact]
    public void UnknownNames()
    {
        Assert.Equal(-1, KeyNames.Code("NotAKey"));
        Assert.Equal(-1, KeyNames.Code(null));
        Assert.Equal(-1, KeyNames.Code(""));
        Assert.Equal("—", KeyNames.Display(""));
        Assert.Null(KeyNames.Name(-1));
        Assert.Null(KeyNames.Name(KeyNames.CodeCount));
    }

    [Theory]
    [InlineData("MouseLeft", true)]
    [InlineData("MouseX2", true)]
    [InlineData("WheelDown", true)]
    [InlineData("Space", false)]
    [InlineData("F8", false)]
    public void IsMouse(string name, bool mouse) => Assert.Equal(mouse, KeyNames.IsMouse(KeyNames.Code(name)));

    [Fact]
    public void DefaultHotkey_IsF8_AndBadOnesFallBack()
    {
        Assert.Equal(0x77, new AppSettings().CaptureHotkeyCode);
        Assert.Equal(0x76, new AppSettings { CaptureHotkey = "F7" }.CaptureHotkeyCode);
        Assert.Equal(0x77, new AppSettings { CaptureHotkey = "MouseLeft" }.CaptureHotkeyCode);
        Assert.Equal(0x77, new AppSettings { CaptureHotkey = "WheelUp" }.CaptureHotkeyCode);
        Assert.Equal(0x77, new AppSettings { CaptureHotkey = "nonsense" }.CaptureHotkeyCode);
    }
}

public class KbmInputTests
{
    // Each reader makes a window on its own thread. Thread ids get reused, and a window class left
    // behind by an earlier reader used to be picked up again, calling a collected delegate and
    // killing the app. Manual mode and never switched on, so nothing is ever captured here.
    [Fact]
    public void CanBeStartedAndStoppedManyTimes()
    {
        for (int i = 0; i < 40; i++)
        {
            using (var input = new KbmInput(0x77, autoCapture: false))
            {
                Assert.Equal(CaptureState.Paused, input.State);
                Assert.False(input.IsCaptured);
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }
}

public class PointerTrackerTests
{
    private const double ParkX = 1280, ParkY = 720, Radius = 240, MaxJump = 480;

    private static PointerTracker Started(long now = 0)
    {
        var t = new PointerTracker(Radius, MaxJump);
        t.Reset(ParkX, ParkY, now);
        return t;
    }

    [Fact]
    public void FollowingClient_TurnsForeverWithoutHittingAnEdge()
    {
        // Parsec-like: when the host cursor is re-centred, the client's pointer jumps back to it.
        var t = Started();
        double x = ParkX, y = ParkY;
        long now = 0;
        int total = 0, recentres = 0;
        t.Feed(x, y, now); // the client's pointer arrives at the park point
        for (int i = 0; i < 1000; i++)
        {
            now += 4;
            x -= 10; // steady movement to the left: 10 000 px in all, far more than any screen
            var (dx, dy, recentre) = t.Feed(x, y, now);
            total += dx;
            Assert.Equal(0, dy);
            if (recentre) { recentres++; x = ParkX; y = ParkY; } // the client follows the host cursor
        }
        Assert.True(recentres > 30, $"{recentres} re-centres");
        Assert.True(t.Recentring);
        // Each re-centre drops one step while the client catches up; everything else arrives.
        Assert.InRange(total, -10_000, -10_000 + 10 * (recentres + 1));
        Assert.InRange(x, ParkX - Radius - 10, ParkX + Radius); // never strays past the radius
    }

    [Fact]
    public void NonFollowingClient_StopsRecentringAndKeepsMeasuring()
    {
        // Remote Desktop-like: the client's pointer never comes back.
        var t = Started();
        long now = 0;
        double x = ParkX;
        int total = 0, recentres = 0;
        t.Feed(x, ParkY, now);
        for (int i = 0; i < 100; i++)
        {
            now += 4;
            x += 5;
            var (dx, _, recentre) = t.Feed(x, ParkY, now);
            total += dx;
            if (recentre) recentres++;
        }
        Assert.Equal(1, recentres);
        Assert.False(t.Recentring);
        Assert.Equal(500, total);
    }

    [Fact]
    public void MovesStillOnTheirWay_CountUntilThePointerComesBack()
    {
        var t = Started();
        t.Feed(ParkX, ParkY, 0);
        long now = 0;
        double x = ParkX;
        bool asked = false;
        while (!asked) { now += 4; x += 20; asked = t.Feed(x, ParkY, now).Recentre; }
        // Two positions sent before the client saw the re-centre still count.
        Assert.Equal(20, t.Feed(x + 20, ParkY, now + 4).Dx);
        Assert.Equal(20, t.Feed(x + 40, ParkY, now + 8).Dx);
        // Back at the park point (plus a little movement): measuring starts again from here.
        Assert.Equal(0, t.Feed(ParkX + 3, ParkY, now + 12).Dx);
        Assert.Equal(7, t.Feed(ParkX + 10, ParkY, now + 16).Dx);
    }

    [Fact]
    public void FirstPositionFarAway_IsNotMovement()
    {
        var t = Started();
        Assert.Equal((0, 0, false), t.Feed(200, 200, 10)); // where the client's pointer was before capture
        Assert.Equal((0, 0, false), t.Feed(ParkX, ParkY, 20)); // then it follows the park
        Assert.Equal(-15, t.Feed(ParkX - 15, ParkY, 24).Dx);
    }

    [Fact]
    public void BigJumps_AreIgnored()
    {
        var t = Started();
        t.Feed(ParkX, ParkY, 0);
        t.Feed(ParkX - 100, ParkY, 4);
        Assert.Equal(0, t.Feed(ParkX - 100 + MaxJump + 1, ParkY, 8).Dx);
    }

    [Fact]
    public void FractionsOfAPixel_AddUp()
    {
        var t = Started();
        t.Feed(ParkX, ParkY, 0);
        int total = 0;
        for (int i = 1; i <= 10; i++) total += t.Feed(ParkX + i * 0.4, ParkY - i * 0.3, i).Dx;
        Assert.Equal(4, total);
    }
}

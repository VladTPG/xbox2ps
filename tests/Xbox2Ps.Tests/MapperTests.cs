using System.Buffers.Binary;
using Xbox2Ps.Core;
using Pad = Xbox2Ps.Core.XInput.XINPUT_GAMEPAD;

namespace Xbox2Ps.Tests;

public class MapperTests
{
    private static byte[] Build(Pad g, Profile? p = null)
    {
        var packet = new byte[Mapper.PacketSize];
        Mapper.Build(packet, g, CompiledProfile.From((p ?? Profile.CreateDefault()).Normalize()));
        return packet;
    }

    private static uint Buttons(byte[] packet) => BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4));

    private static Pad Press(params ushort[] masks)
    {
        ushort b = 0;
        foreach (var m in masks) b |= m;
        return new Pad { wButtons = b };
    }

    private static Pad RandomPad(Random r) => new()
    {
        wButtons = (ushort)r.Next(0, 0x10000),
        bLeftTrigger = (byte)r.Next(0, 256),
        bRightTrigger = (byte)r.Next(0, 256),
        sThumbLX = (short)r.Next(short.MinValue, short.MaxValue + 1),
        sThumbLY = (short)r.Next(short.MinValue, short.MaxValue + 1),
        sThumbRX = (short)r.Next(short.MinValue, short.MaxValue + 1),
        sThumbRY = (short)r.Next(short.MinValue, short.MaxValue + 1),
    };

    [Fact]
    public void DefaultProfile_MatchesTheOriginalConsoleVersion_ByteForByte()
    {
        var r = new Random(1234);
        var compiled = CompiledProfile.Default;
        var expected = new byte[Mapper.PacketSize];
        var actual = new byte[Mapper.PacketSize];

        var pads = Enumerable.Range(0, 10_000).Select(_ => RandomPad(r)).ToList();
        // Edge values the random draw might miss.
        pads.Add(default);
        pads.Add(new Pad { wButtons = 0xFFFF, bLeftTrigger = 255, bRightTrigger = 255, sThumbLX = short.MinValue, sThumbLY = short.MinValue, sThumbRX = short.MaxValue, sThumbRY = short.MaxValue });
        pads.Add(new Pad { bLeftTrigger = 30, bRightTrigger = 31 });
        pads.Add(Press(XInput.BACK, XInput.START));

        foreach (var pad in pads)
        {
            Legacy.BuildPacket(expected, pad);
            Mapper.Build(actual, pad, compiled);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Remap_SendsTheChosenButtonInstead()
    {
        var p = Profile.CreateDefault();
        p.Buttons[XboxInput.A] = DsOutput.Circle;
        uint b = Buttons(Build(Press(XInput.A), p));
        Assert.Equal(Mapper.Circle, b);
    }

    [Fact]
    public void TwoInputs_CanShareAnOutput()
    {
        var p = Profile.CreateDefault();
        p.Buttons[XboxInput.B] = DsOutput.Cross;
        Assert.Equal(Mapper.Cross, Buttons(Build(Press(XInput.A), p)));
        Assert.Equal(Mapper.Cross, Buttons(Build(Press(XInput.B), p)));
        Assert.Equal(Mapper.Cross, Buttons(Build(Press(XInput.A, XInput.B), p)));
    }

    [Fact]
    public void None_DisablesAButton()
    {
        var p = Profile.CreateDefault();
        p.Buttons[XboxInput.A] = DsOutput.None;
        Assert.Equal(0u, Buttons(Build(Press(XInput.A), p)));
    }

    [Fact]
    public void DpadAndMute_GoToTheRightPlaces()
    {
        var p = Profile.CreateDefault();
        p.Buttons[XboxInput.Y] = DsOutput.DpadLeft;
        p.Buttons[XboxInput.Guide] = DsOutput.Mute;
        var packet = Build(Press(XInput.Y, XInput.GUIDE), p);
        Assert.Equal(Mapper.DpadLeft, packet[8]);
        Assert.Equal(Mapper.Mute, Buttons(packet));
    }

    [Fact]
    public void DefaultCombo_ViewAndMenu_ClickTheTouchpadOnly()
    {
        Assert.Equal(Mapper.Touchpad, Buttons(Build(Press(XInput.BACK, XInput.START))));
        Assert.Equal(Mapper.Create, Buttons(Build(Press(XInput.BACK))));
        Assert.Equal(Mapper.Options, Buttons(Build(Press(XInput.START))));
    }

    [Fact]
    public void CustomCombo_SuppressesItsInputs_ButNotOthers()
    {
        var p = Profile.CreateDefault();
        p.Combos.Add(new Combo(XboxInput.LB, XboxInput.RB, DsOutput.PS));
        uint b = Buttons(Build(Press(XInput.LB, XInput.RB, XInput.A), p));
        Assert.Equal(Mapper.PS | Mapper.Cross, b);
    }

    [Fact]
    public void Combo_WithATrigger_UsesThePressPoint()
    {
        var p = Profile.CreateDefault();
        p.Combos = [new Combo(XboxInput.LT, XboxInput.A, DsOutput.Triangle)];
        var held = Build(new Pad { wButtons = XInput.A, bLeftTrigger = 200 }, p);
        Assert.Equal(Mapper.Triangle, Buttons(held));
        Assert.Equal(0, held[9]); // LT's own L2 is paused while the combo is held
        var light = Build(new Pad { wButtons = XInput.A, bLeftTrigger = 10 }, p);
        Assert.Equal(Mapper.Cross, Buttons(light));
        Assert.Equal(10, light[9]);
    }

    [Fact]
    public void TriggerToButton_PressesPastThePressPoint()
    {
        var p = Profile.CreateDefault();
        p.Buttons[XboxInput.RT] = DsOutput.Cross;
        var pressed = Build(new Pad { bRightTrigger = 31 }, p);
        Assert.Equal(Mapper.Cross, Buttons(pressed));
        Assert.Equal(0, pressed[10]);
        Assert.Equal(0u, Buttons(Build(new Pad { bRightTrigger = 30 }, p)));
    }

    [Fact]
    public void ButtonToTrigger_PullsItAllTheWay()
    {
        var p = Profile.CreateDefault();
        p.Buttons[XboxInput.A] = DsOutput.R2;
        var packet = Build(Press(XInput.A), p);
        Assert.Equal(Mapper.R2, Buttons(packet));
        Assert.Equal(255, packet[10]);
    }

    [Fact]
    public void SwappedTriggers_KeepTheirAnalogDepth()
    {
        var p = Profile.CreateDefault();
        p.Buttons[XboxInput.LT] = DsOutput.R2;
        p.Buttons[XboxInput.RT] = DsOutput.L2;
        var packet = Build(new Pad { bLeftTrigger = 100, bRightTrigger = 7 }, p);
        Assert.Equal(7, packet[9]);
        Assert.Equal(100, packet[10]);
        Assert.Equal(Mapper.R2, Buttons(packet));
    }

    [Fact]
    public void PressPoint_ChangesOnlyTheDigitalBit()
    {
        var p = Profile.CreateDefault();
        p.TriggerThreshold = 100;
        var packet = Build(new Pad { bLeftTrigger = 50 }, p);
        Assert.Equal(50, packet[9]);
        Assert.Equal(0u, Buttons(packet));
    }

    [Fact]
    public void Deadzone_ZeroesSmallMovements_AndStillReachesFullTilt()
    {
        var p = Profile.CreateDefault();
        p.LeftStick.Deadzone = 20;
        var small = Build(new Pad { sThumbLX = 5000, sThumbLY = 2000 }, p);
        var centre = Build(default, p);
        Assert.Equal(centre[0], small[0]);
        Assert.Equal(centre[1], small[1]);

        var full = Build(new Pad { sThumbLX = short.MaxValue }, p);
        Assert.Equal(127, (sbyte)full[0]);

        // The right stick has its own setting.
        var right = Build(new Pad { sThumbRX = 5000 }, p);
        Assert.Equal(Mapper.Axis(5000), (sbyte)right[2]);
    }

    [Fact]
    public void Invert_FlipsTheAxis()
    {
        var p = Profile.CreateDefault();
        p.LeftStick.InvertX = true;
        p.RightStick.InvertY = true;
        var packet = Build(new Pad { sThumbLX = short.MaxValue, sThumbRY = short.MaxValue }, p);
        Assert.Equal(-128, (sbyte)packet[0]);
        Assert.Equal(127, (sbyte)packet[3]); // XInput up is normally DualSense -128 (up); inverted it's down
    }

    [Fact]
    public void SwapSticks_ExchangesThem()
    {
        var p = Profile.CreateDefault();
        p.SwapSticks = true;
        var packet = Build(new Pad { sThumbLX = short.MaxValue }, p);
        Assert.Equal(127, (sbyte)packet[2]);
        Assert.Equal(0, (sbyte)packet[0]);
    }

    [Fact]
    public void Rumble_ScalesAndCanBeTurnedOff()
    {
        var p = Profile.CreateDefault();
        Assert.Equal(((ushort)(100 * 257), (ushort)(200 * 257)), Mapper.Motors(200, 100, CompiledProfile.From(p)));

        p.RumbleStrength = 50;
        Assert.Equal(((ushort)(100 * 257 / 2), (ushort)(200 * 257 / 2)), Mapper.Motors(200, 100, CompiledProfile.From(p)));

        p.RumbleEnabled = false;
        Assert.Equal(((ushort)0, (ushort)0), Mapper.Motors(200, 100, CompiledProfile.From(p)));
    }

    [Fact]
    public void PressedOutputs_ReadsThePacketBack()
    {
        var packet = Build(Press(XInput.A, XInput.DPAD_UP, XInput.GUIDE));
        Assert.Equal(new[] { DsOutput.Cross, DsOutput.PS, DsOutput.DpadUp }, Mapper.PressedOutputs(packet));
    }

    [Fact]
    public void Neutral_ReleasesEverything()
    {
        var packet = Build(new Pad { wButtons = 0xFFFF, bLeftTrigger = 255, sThumbLX = 9000 });
        Mapper.Neutral(packet);
        Assert.Equal(0u, Buttons(packet));
        Assert.Equal(0, packet[0]);
        Assert.Equal(0, packet[9]);
        Assert.Equal(-8192, BinaryPrimitives.ReadInt16LittleEndian(packet.AsSpan(31)));
    }
}

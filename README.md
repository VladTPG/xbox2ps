<p align="center"><img src="docs/icon.png" width="96" alt=""></p>

# xbox2ps

**Use an Xbox controller in PS Remote Play on Windows.**

Sony's PS Remote Play app for PC only accepts PlayStation controllers. xbox2ps reads your Xbox controller and plays it through a virtual DualSense (PS5 controller), so Remote Play sees a real-looking PlayStation pad. It works with games that want a DualSense too.

The older free tools stopped working with Remote Play in 2023. Remote Play 6 and later can't use their ViGEm virtual DualShock 4, and ViGEm has been retired. xbox2ps uses [VIIPER](https://github.com/Alia5/VIIPER) instead. VIIPER emulates a complete USB DualSense, audio interfaces and all, and current Remote Play versions detect it.

## Features

- Every Xbox button, the d-pad, both sticks and both analog triggers are mapped to the DualSense.
- **Rumble:** the game's rumble is sent to your Xbox controller.
- **Remap anything.** Any Xbox button can press any DualSense button, including PS, Touchpad click and Mute.
- **Combos.** Hold two buttons together to press something else. View + Menu clicks the touchpad by default.
- **Sticks and triggers:**
  - Stick deadzones, invert per axis, and swap the left and right sticks.
  - An adjustable trigger press point.
  - A rumble strength setting.
- **Profiles:** create, duplicate, rename, import and export them. Changes apply instantly, even while you play.
- **Live preview:** see exactly what the DualSense is pressing.
- **Runs in the tray.** It can launch with Windows and start mapping on its own.

## Setup

You need Windows 10 or 11 and an Xbox controller (Xbox One, Series X|S, or Xbox 360), on USB or Bluetooth.

### 1. Install VIIPER (once)

VIIPER creates the virtual DualSense. It's a separate project, installed separately.

1. Download `viiper.exe` from [VIIPER's releases](https://github.com/Alia5/VIIPER/releases).
   > **For PS Remote Play, use the `dev-snapshot` release.** For now only that build includes the full USB DualSense that Remote Play detects. Older stable versions offer a simpler DualSense that Remote Play ignores, and xbox2ps warns you if that's what you have.
2. Open a terminal in the folder where you saved it and run:
   ```powershell
   .\viiper.exe install
   ```
   This installs VIIPER's USB/IP driver (usbip-win2) and sets VIIPER to start with Windows.
3. **Restart Windows.** The driver needs a restart before it works.

### 2. Run xbox2ps

1. Download `xbox2ps.exe` from this project's releases and run it. It's a single file and needs no installer. Windows SmartScreen may warn you because the file isn't code-signed: choose **More info → Run anyway**.
2. Connect your Xbox controller.
3. Press **Start mapping**. All three status dots should turn green:

   | Status | Green means |
   |---|---|
   | **VIIPER** | The VIIPER server is running. |
   | **Xbox** | Your controller is connected. |
   | **DualSense** | The virtual DualSense is plugged in. |

4. Open PS Remote Play and connect to your console. It sees the controller as a DualSense.

## Remapping

| Tab | What it does |
|---|---|
| **Buttons** | Pick what each Xbox input presses on the DualSense. Press a button on your controller to highlight its row. |
| **Combos** | Two buttons held together press a third. While a combo is held, its two buttons don't do their usual job. |
| **Sticks & triggers** | Deadzones, inverting, swapping sticks, the trigger press point, and rumble. |
| **Options** | Start-up behaviour, tray behaviour and launch with Windows. |

Every change applies right away and saves on its own. Use the profile bar at the bottom to keep different layouts for different games. Use **Export** to share one as a `.json` file.

Your profiles and settings are saved in `%APPDATA%\xbox2ps`.

## Troubleshooting

**Remote Play doesn't see the controller**
- The DualSense status should say **Active**. If it says **Active (basic)**, your VIIPER version is too old. Install the `dev-snapshot` build from step 1, restart, and start mapping again.
- Start xbox2ps before Remote Play, or restart Remote Play after mapping has started.

**The Xbox status says "Not connected"**
- Check that the controller works in Windows. Search for "Set up USB game controllers" and open it.
- After you install or remove controller drivers, a restart often fixes a Bluetooth controller that pairs but sends nothing.

**VIIPER shows "Not installed" or "Not running"**
- xbox2ps looks for VIIPER at `%LOCALAPPDATA%\VIIPER\viiper.exe` and starts its server if it isn't running. If VIIPER is somewhere else, start it yourself with `viiper.exe server`, then press **Retry**.

**Games see two controllers**
- Some games read the Xbox controller and the virtual DualSense at the same time. In-game, pick the DualSense. Remote Play itself ignores the Xbox pad.

The **Log** tab shows what xbox2ps is doing. **Copy log** is handy when you report a problem.

## Building from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build
dotnet test
dotnet publish src/xbox2ps -c Release -o dist
```

`dist\xbox2ps.exe` is a self-contained single file and doesn't need .NET installed.

| Project | What's in it |
|---|---|
| `src/Xbox2Ps.Core` | XInput, the VIIPER client, profiles, the mapper and the mapping loop. No UI. |
| `src/xbox2ps` | The WPF app. |
| `tests/Xbox2Ps.Tests` | The tests, including one that checks the default profile matches the original console version byte for byte. |

## Support the project

xbox2ps is free. If it saved you from buying a DualSense, consider supporting development: *(donation link coming soon)*.

## Credits and license

- **xbox2ps** is released under the [MIT license](LICENSE).
- **[VIIPER](https://github.com/Alia5/VIIPER)** by Alia5 provides the virtual USB DualSense. It's licensed under GPL-3.0, installed separately and not bundled with xbox2ps. xbox2ps talks to it over its local network API.

xbox2ps isn't affiliated with, endorsed by or sponsored by Sony Interactive Entertainment or Microsoft. PlayStation, DualSense and PS Remote Play are trademarks of Sony Interactive Entertainment. Xbox is a trademark of Microsoft.

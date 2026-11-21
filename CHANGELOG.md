# Changelog

## 1.1.0

- **Keyboard and mouse.** Play through Remote Play with a keyboard and mouse, with or without an Xbox controller; both can be used at once.
  - xbox2ps captures the keyboard and mouse while PS Remote Play is the window in front, and releases them when you switch away. F8 (changeable) pauses and resumes capture. Capture can also be left entirely to the hotkey.
  - The mouse moves the right stick (or the left, or neither), with sensitivity, anti-deadzone, response curve and invert settings.
  - Every DualSense button and stick direction can be bound to a key, a mouse button or the wheel. The default is a shooter layout: WASD, mouse aim, clicks on R2/L2.
  - While captured, the cursor is hidden and mapped keys and clicks don't reach Remote Play. Alt, the Windows key and Ctrl+Alt+Del always work.
  - Works when you play on this PC from another computer through Parsec or Remote Desktop, including apps that send the mouse as pointer positions.
  - Saved per profile and off by default, so 1.0 profiles behave exactly as before.
- A **Keyboard** status in the header: captured, paused, or waiting for Remote Play.
- The live preview shows what's actually sent to the DualSense, keyboard and mouse included.

## 1.0.0

First public release.

- A Windows app (it was a console window) with a tray icon. Mapping keeps running with the window closed.
- **Remap any button.** Each of the 17 Xbox inputs can press any DualSense button, including Mute and the touchpad click. Cross, Circle, Square and Triangle show their PlayStation symbols in their usual colours.
- **Combos.** Two buttons held together press something else. View + Menu clicks the touchpad by default.
- **Sticks:**
  - Swap the left and right sticks.
  - Invert each axis.
  - Set a deadzone per stick.
- **Triggers:** an adjustable press point. L2/R2 always get the full analog depth.
- **Rumble:** a switch and a strength setting.
- **Profiles:** switch, duplicate, rename, import and export them. Edits save on their own.
- **Live preview:** shows what the DualSense is pressing and where the sticks are.
- **Options:**
  - Start mapping when the app opens.
  - Start minimized.
  - Launch with Windows.
  - Choose which controller to use.
- **Setup help** when VIIPER is missing, not running, or too old for PS Remote Play.
- **Same feel out of the box:** the default profile produces exactly the same input as the earlier console version, checked by a test over 10,000 random controller states.

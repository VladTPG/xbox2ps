namespace Xbox2Ps.Core;

/// <summary>Smoothed mouse velocity carried from one step to the next.</summary>
public struct MouseStickState
{
    public double Vx, Vy;
}

/// <summary>
/// Turns mouse movement into a stick deflection: mouse speed sets how far the stick tilts,
/// in the direction the mouse moves. Pure maths, so it's tested without a mouse.
/// </summary>
public static class MouseStick
{
    /// <summary>At sensitivity 50, 3 counts per millisecond (a brisk flick on an 800 DPI mouse) is full tilt.</summary>
    internal const double FullTiltCountsPerMs = 3.0;

    /// <summary>Time constant of the smoothing: short enough that the stick settles quickly when the mouse stops.</summary>
    internal const double SmoothingMs = 6.0;

    /// <summary>Below this much tilt (before the anti-deadzone) the stick returns to centre.</summary>
    internal const double RestThreshold = 0.02;

    /// <param name="dx">Mouse counts moved right since the last step.</param>
    /// <param name="dy">Mouse counts moved down since the last step.</param>
    /// <param name="dtMs">Time since the last step.</param>
    /// <returns>Deflection, each axis -1..1, + = right / down.</returns>
    public static (double X, double Y) Step(ref MouseStickState s, int dx, int dy, double dtMs, KbmSettings k)
    {
        dtMs = Math.Clamp(dtMs, 0.5, 100);
        double keep = Math.Exp(-dtMs / SmoothingMs);
        s.Vx = s.Vx * keep + dx / dtMs * (1 - keep);
        s.Vy = s.Vy * keep + dy / dtMs * (1 - keep);

        double speed = Math.Sqrt(s.Vx * s.Vx + s.Vy * s.Vy);
        double gain = k.MouseSensitivity / 50.0 / FullTiltCountsPerMs;
        double tilt = Math.Min(1, speed * gain);
        if (tilt < RestThreshold)
        {
            s.Vx = s.Vy = 0;
            return (0, 0);
        }

        // Curve: 50 is linear; below reacts more to small movements, above gives finer small movements.
        tilt = Math.Pow(tilt, Math.Pow(2, (k.MouseCurve - 50) / 50.0));
        double anti = k.MouseAntiDeadzone / 100.0;
        tilt = anti + (1 - anti) * tilt;

        double x = s.Vx / speed * tilt, y = s.Vy / speed * tilt;
        if (k.InvertMouseY) y = -y;
        return (x, y);
    }
}

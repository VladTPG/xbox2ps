namespace Xbox2Ps.Core;

/// <summary>
/// Turns pointer positions into mouse movement. Remote-control apps such as Parsec send the mouse
/// as "the pointer is here" rather than "it moved this far". The parked cursor is the reference:
/// when the pointer strays too far from it, the caller moves the host cursor back (re-centres).
/// Parsec mirrors host cursor moves on the client, so the client's pointer comes back too and
/// turning never runs into the edge of the screen. If the client doesn't follow, re-centring stops
/// and movement is simply the difference between positions. Pure logic, so it's tested without Windows.
/// </summary>
public sealed class PointerTracker
{
    /// <summary>How long to wait for the client's pointer to come back after a re-centre.</summary>
    public const long SyncTimeoutMs = 200;

    private readonly double _radius, _maxJump;
    private double _parkX, _parkY, _lastX, _lastY, _restX, _restY;
    private bool _hasLast, _awaitingSync, _recentreWorks = true;
    private long _syncSince;

    /// <param name="radius">How far (pixels) the pointer may stray from the park point before a re-centre.</param>
    /// <param name="maxJump">A bigger step between two positions is the pointer being placed, not moved.</param>
    public PointerTracker(double radius, double maxJump)
    {
        _radius = radius;
        _maxJump = maxJump;
    }

    /// <summary>Whether re-centring is still in use (false once the client didn't follow one).</summary>
    public bool Recentring => _recentreWorks;

    /// <summary>Starts over with the cursor just parked at (x, y), as when capture begins.</summary>
    public void Reset(double parkX, double parkY, long nowMs)
    {
        _parkX = parkX;
        _parkY = parkY;
        _hasLast = false;
        _restX = _restY = 0;
        _recentreWorks = true;
        // Parking the cursor is itself a re-centre: the client's pointer should come to the park point.
        _awaitingSync = true;
        _syncSince = nowMs;
    }

    /// <summary>The host cursor was nudged for another reason; the client's pointer may jump to the park point.</summary>
    public void ExpectSync(long nowMs)
    {
        _awaitingSync = true;
        _syncSince = nowMs;
    }

    /// <summary>The host cursor was moved to a new park point (after <see cref="Feed"/> asked for a re-centre).</summary>
    public void MovePark(double x, double y)
    {
        _parkX = x;
        _parkY = y;
    }

    /// <summary>One reported pointer position, in screen pixels.</summary>
    /// <returns>Movement to apply, and whether to re-centre the host cursor now.</returns>
    public (int Dx, int Dy, bool Recentre) Feed(double x, double y, long nowMs)
    {
        if (_awaitingSync)
        {
            if (Math.Abs(x - _parkX) <= _radius / 2 && Math.Abs(y - _parkY) <= _radius / 2)
            {
                // The client's pointer came back: start measuring from here.
                _awaitingSync = false;
                _hasLast = true;
                _lastX = x;
                _lastY = y;
                _restX = _restY = 0;
                return (0, 0, false);
            }
            if (nowMs - _syncSince > SyncTimeoutMs)
            {
                // It didn't come back: this client doesn't follow the host cursor.
                _awaitingSync = false;
                _recentreWorks = false;
            }
        }

        int dx = 0, dy = 0;
        if (_hasLast)
        {
            double mx = x - _lastX + _restX, my = y - _lastY + _restY;
            if (Math.Abs(mx) < _maxJump && Math.Abs(my) < _maxJump)
            {
                dx = (int)Math.Round(mx);
                dy = (int)Math.Round(my);
                _restX = mx - dx;
                _restY = my - dy;
            }
            else _restX = _restY = 0;
        }
        _hasLast = true;
        _lastX = x;
        _lastY = y;

        bool recentre = _recentreWorks && !_awaitingSync &&
                        (Math.Abs(x - _parkX) > _radius || Math.Abs(y - _parkY) > _radius);
        if (recentre)
        {
            _awaitingSync = true;
            _syncSince = nowMs;
        }
        return (dx, dy, recentre);
    }
}

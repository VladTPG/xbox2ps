using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Xbox2Ps.Core;

public sealed class ViiperException(string message) : Exception(message);

/// <summary>
/// Client for VIIPER's TCP API (https://github.com/Alia5/VIIPER).
/// One request per connection: "path[ payload]\0", a JSON reply, then the server closes.
/// </summary>
public static class Viiper
{
    public const string Host = "127.0.0.1";
    public const int ApiPort = 3242;
    public const string ReleasesUrl = "https://github.com/Alia5/VIIPER/releases";

    /// <summary>Where VIIPER's own installer puts it.</summary>
    public static string ExePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VIIPER", "viiper.exe");

    public static bool IsInstalled => File.Exists(ExePath);

    private static TcpClient Connect(int timeoutMs)
    {
        var c = new TcpClient { NoDelay = true, ReceiveTimeout = 5000, SendTimeout = 5000 };
        try
        {
            // A refused connection to localhost can take ~2 s on Windows, so bound it.
            if (!c.ConnectAsync(Host, ApiPort).Wait(timeoutMs))
                throw new ViiperException($"Timed out connecting to VIIPER on port {ApiPort}.");
            return c;
        }
        catch (AggregateException ae) when (ae.InnerException is not null)
        {
            c.Dispose();
            throw ae.InnerException;
        }
        catch
        {
            c.Dispose();
            throw;
        }
    }

    public static string Request(string line, int connectTimeoutMs = 3000)
    {
        using var c = Connect(connectTimeoutMs);
        var s = c.GetStream();
        s.Write(Encoding.UTF8.GetBytes(line + "\0"));
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        string reply = Encoding.UTF8.GetString(ms.ToArray()).Trim('\0', '\r', '\n', ' ');
        using var doc = JsonDocument.Parse(reply.Length == 0 ? "{}" : reply);
        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Number && st.GetInt32() >= 400)
        {
            string detail = doc.RootElement.TryGetProperty("detail", out var d) ? d.ToString() : reply;
            throw new ViiperException($"VIIPER: {line.Split(' ')[0]} failed: {detail}");
        }
        return reply;
    }

    public static bool Ping(int timeoutMs = 1000)
    {
        try { Request("ping", timeoutMs); return true; } catch { return false; }
    }

    /// <summary>Make sure the server is up; start it if it's installed but not running.</summary>
    public static bool EnsureServer(Action<string>? log = null)
    {
        if (Ping()) return true;
        if (!IsInstalled) return false;
        log?.Invoke("Starting the VIIPER server...");
        Process.Start(new ProcessStartInfo(ExePath, "server") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized });
        for (int i = 0; i < 20; i++)
        {
            Thread.Sleep(250);
            if (Ping()) return true;
        }
        return false;
    }

    public static int CreateBus()
    {
        using var doc = JsonDocument.Parse(Request("bus/create"));
        return doc.RootElement.GetProperty("busId").GetInt32();
    }

    public static string AddDevice(int busId, string type)
    {
        using var doc = JsonDocument.Parse(Request($"bus/{busId}/add {{\"type\":\"{type}\"}}"));
        return doc.RootElement.GetProperty("devId").ToString();
    }

    public static void RemoveBus(int busId) => Request($"bus/remove {busId}");

    /// <summary>Opens the device's input stream: write 33-byte input packets, read feedback packets.</summary>
    public static TcpClient OpenDeviceStream(int busId, string devId)
    {
        var c = Connect(3000);
        c.ReceiveTimeout = 0; // feedback arrives only when a game sends some
        c.GetStream().Write(Encoding.ASCII.GetBytes($"bus/{busId}/{devId}\0"));
        return c;
    }
}

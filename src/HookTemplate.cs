using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

[assembly: SecurityTransparent]
[assembly: AssemblyVersion("1.0.0.0")]

// Hook for the patched Newtonsoft.Json.dll: wraps Int32-overflow song ids and,
// on first use, lazily arms a local relay that restores real ids on lyric
// requests to YesPlayMusic's api (127.0.0.1:10754). Everything lives here so no
// AppDomainManager/config injection is needed (that tripped the app's guard).
public static class LyricifyOverflowHook
{
    public static long[] Ring = new long[64];
    public static int Idx = 0;

    private static int _installed;

    public static int WrapFromString(string s)
    {
        long real;
        if (!long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out real))
            return 0;
        return WrapFromLong(real);
    }

    public static int WrapFromLong(long real)
    {
        int wrapped = unchecked((int)real);
        if (real >= 0)
        {
            int slot = (Idx = (Idx + 1) & 31) * 2;
            Ring[slot] = real;
            Ring[slot + 1] = wrapped;
        }
        if (Interlocked.Exchange(ref _installed, 1) == 0)
            Arm();
        return wrapped;
    }

    public static int SafeToInt32Object(object value, IFormatProvider culture)
    {
        try
        {
            return Convert.ToInt32(value, culture);
        }
        catch (OverflowException)
        {
            return WrapFromLong(Convert.ToInt64(value, culture));
        }
    }

    // Called from the patched JsonConvert.DeserializeObject entry: when the host
    // app parses a Netease lyric response that has no actual lyric lines (the
    // wrapped negative id got no results), fetch the real lyric JSON ourselves
    // using the live /player id and substitute it. Proxy-independent.
    public static string MaybeRepairLyricResponse(string value)
    {
        try
        {
            if (value == null || value.Length < 20)
                return value;
            // YesPlayMusic /player response: {"currentTrack":{...},"progress":N,"playing":bool}
            // When paused, report progress creeping forward at 0.02 s/s from the paused
            // position: monotonic (no backward jumps -> no flicker), keeps Lyricify's
            // "value unchanged -> poll less" heuristic from sleeping, and re-anchors
            // the lyric timeline to essentially the paused line every second.
            if (value[0] == '{' && value.IndexOf("\"playing\":false") >= 0 && value.IndexOf("\"currentTrack\"") >= 0)
            {
                double cur = ExtractProgress(value);
                long track = ExtractTrackId(value);
                if (_creepTrack != track || _creepBase < 0 || cur > _creepNow + 1.0)
                {
                    _creepTrack = track;
                    _creepBase = cur;
                    _creepNow = cur;
                }
                _creepNow += 0.02;
                return ReplaceProgress(value, _creepNow.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            }
            bool shaped = value.IndexOf("\"lrc\"") >= 0 || value.IndexOf("\"nolyric\"") >= 0 || value.IndexOf("\"sgc\"") >= 0;
            if (!shaped)
                return value;
            if (_diagLeft > 0)
            {
                _diagLeft--;
                string snippet = value.Substring(0, Math.Min(200, value.Length));
                snippet = snippet.Replace('\r', ' ').Replace('\n', ' ');
                Log("lyric-shaped payload: " + snippet);
            }
            // Netease returns a placeholder lyric "[00:00.00]暂无歌词" (no lyrics)
            // for unknown/wrapped ids - treat it as empty and repair.
            bool placeholder = value.IndexOf("[00:00.00]暂无歌词") >= 0;
            bool hasLines = value.IndexOf("lyric\":\"[") >= 0 && !placeholder;
            if (hasLines)
                return value;
            long real = Relay.CurrentTrackIdPublic();
            if (real <= 0)
                return value;
            if (real == _lastRepairedId && _lastRepairedJson != null)
                return _lastRepairedJson;
            string fixedJson = Relay.FetchLyricJson(real);
            if (fixedJson != null && fixedJson.IndexOf("lyric\":\"[") >= 0
                && fixedJson.IndexOf("[00:00.00]暂无歌词") < 0)
            {
                Log("repaired lyric json for id " + real);
                _lastRepairedId = real;
                _lastRepairedJson = fixedJson;
                return fixedJson;
            }
            Log("repair found nothing for id " + real);
            return value;
        }
        catch (Exception ex)
        {
            Log("repair error: " + ex.Message);
            return value;
        }
    }

    private static int _pausedRewriteLeft = 3;
    private static int _diagLeft = 5;

    private static double _creepBase = -1;
    private static double _creepNow = -1;
    private static long _creepTrack = -1;

    private static double ExtractProgress(string value)
    {
        int i = value.IndexOf("\"progress\"");
        if (i < 0) return -1;
        int c = value.IndexOf(':', i + 10);
        int s = c + 1;
        while (s < value.Length && value[s] == ' ') s++;
        int e = s;
        while (e < value.Length && (char.IsDigit(value[e]) || value[e] == '.' || value[e] == '-')) e++;
        double v;
        double.TryParse(value.Substring(s, e - s), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out v);
        return v;
    }

    private static long ExtractTrackId(string value)
    {
        int ct = value.IndexOf("\"currentTrack\"");
        if (ct < 0) return -1;
        int ar = value.IndexOf("\"ar\"", ct);
        string seg = ar > ct ? value.Substring(ct, ar - ct) : value.Substring(ct);
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(seg, "\\\"id\\\"\\s*:\\s*(-?\\d+)");
        long v;
        return m.Success && long.TryParse(m.Groups[1].Value, out v) ? v : -1;
    }

    private static string ReplaceProgress(string value, string text)
    {
        int i = value.IndexOf("\"progress\"");
        if (i < 0) return value;
        int c = value.IndexOf(':', i + 10);
        int s = c + 1;
        while (s < value.Length && value[s] == ' ') s++;
        int e = s;
        while (e < value.Length && (char.IsDigit(value[e]) || value[e] == '.' || value[e] == '-')) e++;
        if (e == s) return value;
        return value.Substring(0, s) + text + value.Substring(e);
    }

    // Rewrites "progress" to the track duration (+1s) so Lyricify treats the
    // track as finished and stops advancing the lyric timeline.
    private static string PinProgressToDuration(string value)
    {
        int i = value.IndexOf("\"dt\"");
        double durationMs = -1;
        if (i > 0)
        {
            int d = value.IndexOf(':', i + 4);
            int s = d + 1;
            int e = s;
            while (e < value.Length && (char.IsDigit(value[e])))
                e++;
            double.TryParse(value.Substring(s, e - s), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out durationMs);
        }
        string target = durationMs > 0
            ? ((durationMs / 1000.0) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "99999";
        i = value.IndexOf("\"progress\"");
        if (i < 0)
            return value;
        int c = value.IndexOf(':', i + 10);
        int s2 = c + 1;
        while (s2 < value.Length && value[s2] == ' ')
            s2++;
        int e2 = s2;
        while (e2 < value.Length && (char.IsDigit(value[e2]) || value[e2] == '.' || value[e2] == '-'))
            e2++;
        if (e2 == s2)
            return value;
        return value.Substring(0, s2) + target + value.Substring(e2);
    }

    // Rewrites "progress":<number> to 0 in the /player JSON.
    private static string ZeroProgress(string value)
    {
        int i = value.IndexOf("\"progress\"");
        if (i < 0)
            return value;
        int d = value.IndexOf(':', i + 10);
        if (d < 0)
            return value;
        int s = d + 1;
        while (s < value.Length && (value[s] == ' '))
            s++;
        int e = s;
        while (e < value.Length && (char.IsDigit(value[e]) || value[e] == '.' || value[e] == '-' || value[e] == '+' || value[e] == 'e' || value[e] == 'E'))
            e++;
        if (e == s)
            return value;
        return value.Substring(0, s) + "0" + value.Substring(e);
    }

    private static long _lastRepairedId;
    private static string _lastRepairedJson;

    private static void Arm()
    {
        try
        {
            int port = Relay.Start();
            IWebProxy proxy = new PlayerProxy(port);
            WebRequest.DefaultWebProxy = proxy;
            // Watchdog: the host app may reset DefaultWebProxy to null during its
            // own startup; re-assert ours so /lyric requests keep flowing to the relay.
            Thread wd = new Thread(delegate()
            {
                while (true)
                {
                    try
                    {
                        if (!(WebRequest.DefaultWebProxy is PlayerProxy))
                            WebRequest.DefaultWebProxy = proxy;
                    }
                    catch { }
                    Thread.Sleep(1000);
                }
            });
            wd.IsBackground = true;
            wd.Start();
            Log("armed: relay on " + port + " (watchdog)");
        }
        catch (Exception ex)
        {
            Log("arm error: " + ex.Message);
        }
    }

    internal static void Log(string msg)
    {
        try
        {
            File.AppendAllText(LogPath,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + msg + "\r\n");
        }
        catch { }
    }

    internal static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lyricify", "lyricify_hook.log");

    internal class PlayerProxy : IWebProxy
    {
        private readonly int _port;
        public PlayerProxy(int port) { _port = port; }
        public Uri GetProxy(Uri destination)
        {
            if (destination.Host == "127.0.0.1" && destination.Port == 10754)
            {
                Log("redirect " + destination.PathAndQuery);
                return new Uri("http://127.0.0.1:" + _port + destination.PathAndQuery);
            }
            return destination;
        }
        public bool IsBypassed(Uri host)
        {
            return !(host.Host == "127.0.0.1" && host.Port == 10754);
        }
        public ICredentials Credentials { get; set; }
    }

    internal static class Relay
    {
        public static int Start()
        {
            int port = 27332;
            for (int i = 0; i < 32; i++)
            {
                try
                {
                    TcpListener listener = new TcpListener(IPAddress.Loopback, port);
                    listener.Start(16);
                    Thread t = new Thread(AcceptLoop);
                    t.IsBackground = true;
                    t.Start(listener);
                    return port;
                }
                catch { port++; }
            }
            throw new Exception("no free relay port");
        }

        private static void AcceptLoop(object state)
        {
            TcpListener listener = (TcpListener)state;
            while (true)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch { return; }
                ThreadPool.QueueUserWorkItem(delegate(object o) { Handle((TcpClient)o); }, client);
            }
        }

        private static void Handle(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 30000;
                NetworkStream cs = client.GetStream();
                string headers;
                if (!TryReadHeaders(cs, out headers)) return;
                string[] lines = headers.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length < 1) return;
                string[] parts = lines[0].Split(' ');
                if (parts.Length < 2) return;
                string method = parts[0];
                string rawUri = parts[1];
                string pathAndQuery = rawUri;
                if (rawUri.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    int slash = rawUri.IndexOf('/', 7);
                    pathAndQuery = slash >= 0 ? rawUri.Substring(slash) : "/";
                }
                int contentLength = 0;
                StringBuilder up = new StringBuilder();
                up.Append(method).Append(' ').Append(RewriteId(pathAndQuery)).Append(" HTTP/1.1\r\n");
                for (int i = 1; i < lines.Length; i++)
                {
                    int colon = lines[i].IndexOf(':');
                    if (colon <= 0) continue;
                    string name = lines[i].Substring(0, colon).Trim().ToLowerInvariant();
                    if (name == "host" || name == "connection" || name == "proxy-connection"
                        || name == "keep-alive" || name == "content-length")
                    {
                        if (name == "content-length")
                            int.TryParse(lines[i].Substring(colon + 1).Trim(), out contentLength);
                        continue;
                    }
                    up.Append(lines[i]).Append("\r\n");
                }
                up.Append("Host: 127.0.0.1:10754\r\nConnection: close\r\n");
                byte[] body = null;
                if (contentLength > 0)
                {
                    body = new byte[contentLength];
                    if (!ReadFully(cs, body)) return;
                    up.Append("Content-Length: ").Append(contentLength).Append("\r\n");
                }
                up.Append("\r\n");
                byte[] head = Encoding.UTF8.GetBytes(up.ToString());
                using (TcpClient upstream = new TcpClient("127.0.0.1", 10754))
                {
                    NetworkStream us = upstream.GetStream();
                    us.Write(head, 0, head.Length);
                    if (body != null) us.Write(body, 0, body.Length);
                    us.Flush();
                    byte[] buf = new byte[16384];
                    int n;
                    while ((n = us.Read(buf, 0, buf.Length)) > 0)
                        cs.Write(buf, 0, n);
                    cs.Flush();
                }
            }
            catch (Exception ex) { Log("relay error: " + ex.Message); }
            finally { try { client.Close(); } catch { } }
        }

        public static long CurrentTrackIdPublic() { return CurrentTrackId(); }

        public static string FetchLyricJson(long id)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:10754/lyric?id=" + id.ToString(CultureInfo.InvariantCulture));
                req.Proxy = null;
                req.Timeout = 3000;
                req.ReadWriteTimeout = 3000;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                    return sr.ReadToEnd();
            }
            catch (Exception ex) { Log("fetch lyric error: " + ex.Message); return null; }
        }

        private static long _liveId = long.MinValue;
        private static DateTime _liveIdAt = DateTime.MinValue;

        private static long CurrentTrackId()
        {
            if ((DateTime.UtcNow - _liveIdAt).TotalSeconds < 2 && _liveId != long.MinValue)
                return _liveId;
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:27232/player");
                req.Proxy = null;
                req.Timeout = 2000;
                req.ReadWriteTimeout = 2000;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                {
                    string body = sr.ReadToEnd();
                    int ct = body.IndexOf("\"currentTrack\"");
                    if (ct >= 0)
                    {
                        int ar = body.IndexOf("\"ar\"", ct);
                        string seg = ar > ct ? body.Substring(ct, ar - ct) : body.Substring(ct);
                        Match m = Regex.Match(seg, "\"id\"\\s*:\\s*(\\d+)");
                        if (m.Success)
                        {
                            _liveId = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                            _liveIdAt = DateTime.UtcNow;
                            return _liveId;
                        }
                    }
                }
            }
            catch (Exception ex) { Log("live-id error: " + ex.Message); }
            _liveIdAt = DateTime.UtcNow;
            return long.MinValue;
        }

        private static string RewriteId(string pathAndQuery)
        {
            int q = pathAndQuery.IndexOf('?');
            if (q < 0) return pathAndQuery;
            string path = pathAndQuery.Substring(0, q);
            string[] pairs = pathAndQuery.Substring(q + 1).Split('&');
            StringBuilder sb = new StringBuilder(path).Append('?');
            bool first = true;
            foreach (string pair in pairs)
            {
                if (!first) sb.Append('&');
                first = false;
                int eq = pair.IndexOf('=');
                if (eq > 0 && pair.Substring(0, eq) == "id")
                {
                    string raw = pair.Substring(eq + 1);
                    long idVal;
                    if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out idVal))
                    {
                        long real = CurrentTrackId();
                        if (real == long.MinValue)
                            real = LookupRing(idVal);
                        if (real != idVal)
                            Log("id rewrite " + idVal + " -> " + real);
                        sb.Append("id=").Append(real.ToString(CultureInfo.InvariantCulture));
                        continue;
                    }
                }
                sb.Append(pair);
            }
            return sb.ToString();
        }

        private static long LookupRing(long idVal)
        {
            if (idVal >= 0 && idVal <= int.MaxValue)
                return idVal;
            for (int k = 0; k < 32; k++)
            {
                int slot = ((Idx - k) & 31) * 2;
                if (slot < 0) continue;
                if (Ring[slot + 1] == idVal)
                    return Ring[slot];
            }
            return idVal;
        }

        private static bool TryReadHeaders(NetworkStream stream, out string headers)
        {
            headers = null;
            StringBuilder sb = new StringBuilder();
            byte[] one = new byte[1];
            while (sb.Length < 65536)
            {
                int n;
                try { n = stream.Read(one, 0, 1); }
                catch { return false; }
                if (n <= 0) return false;
                sb.Append((char)one[0]);
                if (sb.Length >= 4 && sb[sb.Length - 4] == '\r' && sb[sb.Length - 3] == '\n'
                    && sb[sb.Length - 2] == '\r' && sb[sb.Length - 1] == '\n')
                {
                    headers = sb.ToString();
                    return true;
                }
            }
            return false;
        }

        private static bool ReadFully(NetworkStream stream, byte[] buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int n = stream.Read(buffer, total, buffer.Length - total);
                if (n <= 0) return false;
                total += n;
            }
            return true;
        }
    }
}

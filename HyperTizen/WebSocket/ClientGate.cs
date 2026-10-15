using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HyperTizen.WebSocket
{
    /// <summary>
    /// Decides who may use the settings server on port 8086. Only the TV itself should: HyperTizenUI runs
    /// on the TV inside TizenBrew, but the port listens on every interface. A client is the TV when its
    /// address is loopback, equals the server end of its own connection (the TV connecting to its own
    /// network address), or is one of the TV's interface addresses. Another host on the LAN cannot
    /// complete a TCP connection with a forged source address, so checking it before the WebSocket is
    /// accepted keeps every command (ReadConfig, SetConfig, ScanSSDP, tests...) away from other devices.
    /// </summary>
    public static class ClientGate
    {
        /// <summary>
        /// Off in the diagnostic build: every connection is classified and recorded, nobody is turned
        /// away, until the real TV UI is confirmed to be classified as the TV.
        /// </summary>
        public static volatile bool Enforce = false;

        /// <summary>Tests only: replaces the decision for connections made in tests.</summary>
        internal static Func<IPEndPoint, IPEndPoint, Decision> DecideOverride = null;

        public struct Decision
        {
            public bool Allowed;
            public string Reason;
        }

        public static Decision Decide(IPEndPoint remote, IPEndPoint local)
        {
            Func<IPEndPoint, IPEndPoint, Decision> over = DecideOverride;
            return over != null ? over(remote, local) : Classify(remote, local, OwnAddresses());
        }

        /// <summary>IPv4-mapped IPv6 (::ffff:a.b.c.d, as a dual-stack listener reports IPv4 clients) becomes plain IPv4.</summary>
        public static IPAddress Normalize(IPAddress a)
        {
            if (a == null) return null;
            return a.AddressFamily == AddressFamily.InterNetworkV6 && a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
        }

        public static Decision Classify(IPEndPoint remote, IPEndPoint local, IList<IPAddress> own)
        {
            IPAddress r = Normalize(remote?.Address);
            if (r == null) return new Decision { Reason = "no client address" };
            if (IsUnspecified(r)) return new Decision { Reason = "unspecified client address " + r };
            if (IPAddress.IsLoopback(r)) return new Decision { Allowed = true, Reason = "loopback" };

            IPAddress l = Normalize(local?.Address);
            if (l != null && !IsUnspecified(l) && Same(r, l)) return new Decision { Allowed = true, Reason = "the TV's own address (same as the server end)" };

            if (own != null)
                foreach (IPAddress o in own)
                    if (o != null && Same(r, Normalize(o))) return new Decision { Allowed = true, Reason = "one of the TV's interface addresses" };

            return new Decision { Reason = "another device" };
        }

        private static bool IsUnspecified(IPAddress a)
        {
            return a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any) || a.Equals(IPAddress.None) || a.Equals(IPAddress.IPv6None);
        }

        /// <summary>Same address bytes; an IPv6 scope id (%eth0) doesn't make a different host.</summary>
        private static bool Same(IPAddress a, IPAddress b)
        {
            if (a.AddressFamily != b.AddressFamily) return false;
            byte[] x = a.GetAddressBytes(), y = b.GetAddressBytes();
            for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) return false;
            return true;
        }

        // ---- the TV's own addresses (best effort; the same-address rule covers the usual case) ----

        private static IPAddress[] _own = new IPAddress[0];
        private static DateTime _ownAt = DateTime.MinValue;
        private static string _ownError;

        public static IPAddress[] OwnAddresses()
        {
            if ((DateTime.UtcNow - _ownAt).TotalSeconds < 30) return _own;
            try
            {
                var list = new List<IPAddress>();
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus == OperationalStatus.Down) continue; // Linux reports some live links as Unknown
                    foreach (UnicastIPAddressInformation u in ni.GetIPProperties().UnicastAddresses) list.Add(Normalize(u.Address));
                }
                _own = list.ToArray();
                _ownError = null;
            }
            catch (Exception e)
            {
                _ownError = e.GetType().Name + ": " + e.Message;
            }
            _ownAt = DateTime.UtcNow;
            return _own;
        }

        // ---- record: recent connections and refused sources, bounded so a flood can't grow memory ----

        private const int RecentMax = 12, RefusedMax = 16;
        private static readonly object _lock = new object();
        private static readonly Queue<Dictionary<string, object>> _recent = new Queue<Dictionary<string, object>>();
        private static readonly Dictionary<string, Refused> _refused = new Dictionary<string, Refused>();
        private static long _refusedOther;
        internal static int LogLines; // TV log lines written for refused sources (tests check the rate limit)

        private sealed class Refused
        {
            public long Count, Unlogged;
            public DateTime Last, LastLogged;
        }

        public static void Record(IPEndPoint remote, IPEndPoint local, string host, string origin, Decision d)
        {
            string address = Normalize(remote?.Address)?.ToString() ?? "(none)";
            DateTime now = DateTime.UtcNow;
            lock (_lock)
            {
                _recent.Enqueue(new Dictionary<string, object>
                {
                    { "time", now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "Z" },
                    { "remote", remote?.ToString() },
                    { "local", local?.ToString() },
                    { "host", Clip(host) },
                    { "origin", Clip(origin) },
                    { "reason", d.Reason },
                    { "allowed", d.Allowed },
                });
                while (_recent.Count > RecentMax) _recent.Dequeue();

                if (d.Allowed) return;
                Refused r;
                if (!_refused.TryGetValue(address, out r))
                {
                    if (_refused.Count >= RefusedMax) { _refusedOther++; return; }
                    _refused[address] = r = new Refused { LastLogged = DateTime.MinValue };
                }
                r.Count++;
                r.Unlogged++;
                r.Last = now;
                // At most one TV log line per source per minute, with the count since the last one.
                if ((now - r.LastLogged).TotalSeconds >= 60)
                {
                    WriteLog((Enforce ? "refused " : "would refuse ") + r.Unlogged + " settings connection(s) from " + address + " (" + d.Reason + ")");
                    r.LastLogged = now;
                    r.Unlogged = 0;
                }
            }
        }

        private static void WriteLog(string line)
        {
            LogLines++;
            try { Tizen.Log.Warn("HyperTizen", line); } catch { }
        }

        private static string Clip(string s)
        {
            return s == null ? null : s.Length > 80 ? s.Substring(0, 80) : s;
        }

        /// <summary>What the UI shows as the connection check, from the point of view of the asking connection.</summary>
        public static Dictionary<string, object> Audit(IPEndPoint youRemote, IPEndPoint youLocal, string probeState)
        {
            Decision you = Decide(youRemote, youLocal);
            var own = new List<string>();
            foreach (IPAddress a in OwnAddresses()) own.Add(a.ToString());
            lock (_lock)
            {
                var refused = new List<Dictionary<string, object>>();
                foreach (KeyValuePair<string, Refused> kv in _refused)
                    refused.Add(new Dictionary<string, object> { { "address", kv.Key }, { "count", kv.Value.Count }, { "last", kv.Value.Last.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "Z" } });
                return new Dictionary<string, object>
                {
                    { "enforce", Enforce },
                    { "you", new Dictionary<string, object> { { "remote", youRemote?.ToString() }, { "local", youLocal?.ToString() }, { "reason", you.Reason }, { "allowed", you.Allowed } } },
                    { "own", own },
                    { "ownError", _ownError },
                    { "probe", probeState },
                    { "recent", new List<Dictionary<string, object>>(_recent) },
                    { "refused", refused },
                    { "refusedOther", _refusedOther },
                };
            }
        }

        /// <summary>Tests only.</summary>
        internal static void ResetRecord()
        {
            lock (_lock) { _recent.Clear(); _refused.Clear(); _refusedOther = 0; LogLines = 0; }
        }
    }
}

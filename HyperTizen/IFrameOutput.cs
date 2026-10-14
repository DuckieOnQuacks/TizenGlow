using System.Threading.Tasks;

namespace HyperTizen
{
    /// <summary>
    /// Where captured frames go: HyperHDR (JSON image command over WebSocket) or WLED Direct (DDP).
    /// The single capture loop hands each frame to the one active output; only one is active at a time.
    /// </summary>
    internal interface IFrameOutput
    {
        /// <summary>Shown in the UI: "HyperHDR" or "WLED Direct".</summary>
        string Label { get; }

        /// <summary>Has somewhere to send to; capture reports "no-server" until it does.</summary>
        bool Configured { get; }

        /// <summary>e.g. ws://192.168.1.118:8090 or 192.168.1.227:4048; null when not set.</summary>
        string Target { get; }

        /// <summary>Short honest state, e.g. "connected" (WebSocket) or "sending" (DDP has no connection).</summary>
        string State { get; }

        /// <summary>Builds this output's frame from the 16 capture samples (timed as "prepare").</summary>
        void Prepare(Color[] samples);

        /// <summary>Sends the prepared frame. True when it went out; network trouble returns false, never throws.</summary>
        Task<bool> SendAsync();

        /// <summary>Clears counters and per-output state (e.g. smoothing) when this output becomes active.</summary>
        void Reset();
    }
}

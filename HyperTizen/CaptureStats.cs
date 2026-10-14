using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace HyperTizen
{
    /// <summary>
    /// Live capture performance: a rolling window of completed frames plus counters since the last reset.
    /// Fed once per frame by the single capture loop and read about once a second by the config server,
    /// so a lock and a short queue (at most WindowMs x target FPS entries) are all it costs.
    /// </summary>
    public sealed class CaptureStats
    {
        public const double WindowMs = 2000;   // rates and averages cover the last 2 s
        public const double MinSpanMs = 1000;  // report nothing until 1 s of data exists

        private struct Frame
        {
            public double EndMs, CaptureMs, ImageMs, SendMs, WaitMs, FrameMs;
            public bool Sent;
        }

        private readonly Queue<Frame> _frames = new Queue<Frame>();
        private readonly object _lock = new object();
        private readonly Func<double> _clock;
        private double _resetAt;
        private long _failed;
        private double _droppedSlots;
        private int _epoch;

        public CaptureStats(Func<double> clock = null)
        {
            if (clock == null)
            {
                var sw = Stopwatch.StartNew();
                clock = () => sw.Elapsed.TotalMilliseconds;
            }
            _clock = clock;
            _resetAt = clock();
        }

        public double Now() { return _clock(); }

        /// <summary>Changes on every Reset; the loop tags its additions so a frame started before a reset is not counted after it.</summary>
        public int Epoch { get { lock (_lock) return _epoch; } }

        public void Reset()
        {
            lock (_lock)
            {
                _frames.Clear();
                _failed = 0;
                _droppedSlots = 0;
                _resetAt = _clock();
                _epoch++;
            }
        }

        /// <summary>A frame that came back from the capture API. Times in ms; frameMs is the whole loop iteration including the pacing wait.</summary>
        public void AddFrame(int epoch, double captureMs, double imageMs, double sendMs, double waitMs, double frameMs, bool sent)
        {
            lock (_lock)
            {
                if (epoch != _epoch) return;
                double now = _clock();
                _frames.Enqueue(new Frame { EndMs = now, CaptureMs = captureMs, ImageMs = imageMs, SendMs = sendMs, WaitMs = waitMs, FrameMs = frameMs, Sent = sent });
                Trim(now);
            }
        }

        /// <summary>A frame whose capture or image step threw.</summary>
        public void AddFailed(int epoch)
        {
            lock (_lock) if (epoch == _epoch) _failed++;
        }

        /// <summary>Target-FPS frame slots that passed without a capture because the previous frame ran past them.</summary>
        public void AddDropped(int epoch, double slots)
        {
            lock (_lock) if (epoch == _epoch) _droppedSlots += slots;
        }

        public Snapshot Read(int targetFps)
        {
            lock (_lock)
            {
                double now = _clock();
                Trim(now);
                double span = Math.Min(WindowMs, now - _resetAt);
                var s = new Snapshot
                {
                    targetFps = targetFps,
                    failedFrames = _failed,
                    droppedFrames = (long)Math.Floor(_droppedSlots + 1e-9),
                    windowMs = Math.Round(span),
                    performance = "measuring",
                };
                if (span < MinSpanMs) return s;

                int n = _frames.Count, sent = 0;
                double capture = 0, image = 0, send = 0, wait = 0, frame = 0;
                foreach (Frame f in _frames)
                {
                    if (f.Sent) sent++;
                    capture += f.CaptureMs; image += f.ImageMs; send += f.SendMs; wait += f.WaitMs; frame += f.FrameMs;
                }

                double fps = n * 1000 / span;
                s.captureFps = Math.Round(fps, 1);
                s.sendFps = Math.Round(sent * 1000 / span, 1);
                if (n > 0)
                {
                    s.averageCaptureMs = Math.Round(capture / n, 1);
                    s.averageImageMs = Math.Round(image / n, 1);
                    s.averageSendMs = Math.Round(send / n, 1);
                    s.averageWaitMs = Math.Round(wait / n, 1);
                    s.averageFrameMs = Math.Round(frame / n, 1);
                }

                if (span >= WindowMs)
                {
                    double interval = 1000.0 / targetFps;
                    if (fps >= targetFps * 0.9) s.performance = "target met";
                    else if (n > 0 && (capture + image + send) / n >= interval * 0.9) s.performance = "capture limited";
                    else s.performance = "below target";
                }
                return s;
            }
        }

        private void Trim(double now)
        {
            while (_frames.Count > 0 && _frames.Peek().EndMs <= now - WindowMs) _frames.Dequeue();
        }

        /// <summary>Serialized as the "captureStats" ReadConfig value. Rates and averages are null until MinSpanMs of data exists.</summary>
        public sealed class Snapshot
        {
            public string mode;
            public string resolution;
            public string performance;
            public int targetFps;
            public double? captureFps;
            public double? sendFps;
            public double? averageCaptureMs;
            public double? averageImageMs;
            public double? averageSendMs;
            public double? averageWaitMs;
            public double? averageFrameMs;
            public long failedFrames;
            public long droppedFrames;
            public double windowMs;
        }
    }
}

using System;

namespace ClientAvalonia.Models;

/// <summary>
/// Estimates the playout buffer required by a packet stream.
///
/// The RFC 3550 estimator is intentionally based on adjacent transit-time
/// differences. A cumulative arrival-minus-media-clock error would turn clock
/// drift into ever-growing "jitter", although drift is a buffer-level control
/// problem rather than a network-jitter problem.
/// </summary>
public sealed class JitterEstimator
{
    private const double RfcJitterGain = 1.0 / 16.0;
    private const double LateQuantile = 0.95;
    private const double LateHistogramHalfLifeSeconds = 10.0;
    private const double LateBucketWidthMs = 1.0;
    private const int LateBucketCount = 501;
    private const double UnderrunBoostHalfLifeSeconds = 10.0;
    private const double DefaultSchedulerSlackMs = 5.0;
    private const double JitterMeasurementDeadbandMs = 1.0;
    private const double TargetSnapThresholdMs = 0.5;

    private readonly object _sync = new();
    private readonly double[] _lateHistogram = new double[LateBucketCount];
    private long _lastMediaTimestampNs;
    private long _lastArrivalTimestampNs;
    private double _packetDurationMs;
    private double _rfcJitterMs;
    private double _lateQuantileMs;
    private double _underrunBoostMs;
    private double _smoothedTargetMs;
    private double _baseTargetMs;
    private double _lastSchedulerSlackMs = DefaultSchedulerSlackMs;

    /// <summary>Release smoothing. Increases are always applied immediately.</summary>
    public double Alpha { get; set; } = 0.90;

    /// <summary>Multiplier applied to the RFC mean absolute transit variation.</summary>
    public double JitterFactor { get; set; } = 1.5;

    /// <summary>Optional user floor for network protection; zero lets a stable stream reach the device floor.</summary>
    public double MinNetworkProtectionMs { get; set; } = 0.0;

    /// <summary>Playback device period that must be available before the device consumes audio.</summary>
    public double DeviceBufferMs { get; set; } = 30.0;

    /// <summary>Maximum total adaptive playout buffer.</summary>
    public double MaxBufferMs { get; set; } = 350.0;

    public double EstimatedJitterMs
    {
        get
        {
            lock (_sync)
            {
                return Math.Max(_lateQuantileMs, Math.Max(GetRfcProtectionLocked(), _underrunBoostMs));
            }
        }
    }

    public double RfcJitterMs
    {
        get { lock (_sync) return _rfcJitterMs; }
    }

    public double LateQuantileMs
    {
        get { lock (_sync) return _lateQuantileMs; }
    }

    public double PacketDurationMs
    {
        get { lock (_sync) return _packetDurationMs; }
    }

    public double BaseTargetMs
    {
        get { lock (_sync) return _baseTargetMs > 0.0 ? _baseTargetMs : CalculateBaseTargetLocked(_lastSchedulerSlackMs); }
    }

    public double ProtectionMs
    {
        get { lock (_sync) return GetProtectionLocked(); }
    }

    public double UnderrunBoostMs
    {
        get { lock (_sync) return _underrunBoostMs; }
    }

    public void Update(long mediaTimestampNs, long arrivalTimestampNs, double packetDurationMs = 0.0)
    {
        if (mediaTimestampNs <= 0 || arrivalTimestampNs <= 0)
        {
            return;
        }

        lock (_sync)
        {
            UpdatePacketDurationLocked(packetDurationMs);

            if (_lastMediaTimestampNs <= 0
                || _lastArrivalTimestampNs <= 0
                || mediaTimestampNs <= _lastMediaTimestampNs
                || arrivalTimestampNs <= _lastArrivalTimestampNs)
            {
                _lastMediaTimestampNs = mediaTimestampNs;
                _lastArrivalTimestampNs = arrivalTimestampNs;
                RecalculateTargetLocked(baseSchedulerSlackMs: DefaultSchedulerSlackMs, allowRelease: false);
                return;
            }

            double mediaDeltaMs = (mediaTimestampNs - _lastMediaTimestampNs) / 1_000_000.0;
            double arrivalDeltaMs = (arrivalTimestampNs - _lastArrivalTimestampNs) / 1_000_000.0;
            double transitVariationMs = arrivalDeltaMs - mediaDeltaMs;

            // RFC 3550, section 6.4.1: J <- J + (|D| - J) / 16.
            _rfcJitterMs += (Math.Abs(transitVariationMs) - _rfcJitterMs) * RfcJitterGain;

            // The 95th percentile models the late tail. Negative D means a packet
            // caught up and does not require additional protection. A fixed,
            // exponentially decayed histogram avoids per-packet allocations.
            double elapsedSeconds = (arrivalTimestampNs - _lastArrivalTimestampNs) / 1_000_000_000.0;
            DecayLateHistogramLocked(elapsedSeconds);
            AddLateVariationLocked(Math.Max(0.0, transitVariationMs));
            _lateQuantileMs = QuantileLocked(LateQuantile);

            if (_underrunBoostMs > 0.0 && elapsedSeconds > 0.0)
            {
                _underrunBoostMs *= Math.Pow(0.5, elapsedSeconds / UnderrunBoostHalfLifeSeconds);
                if (_underrunBoostMs < 0.5)
                {
                    _underrunBoostMs = 0.0;
                }
            }

            _lastMediaTimestampNs = mediaTimestampNs;
            _lastArrivalTimestampNs = arrivalTimestampNs;
            RecalculateTargetLocked(baseSchedulerSlackMs: DefaultSchedulerSlackMs, allowRelease: true);
        }
    }

    /// <summary>
    /// Feeds an actual starvation event back into the target. A target that
    /// still underruns must rise even if the observation window underestimated
    /// the late tail.
    /// </summary>
    public void ReportUnderrun(double shortageMs, double packetDurationMs = 0.0)
    {
        lock (_sync)
        {
            UpdatePacketDurationLocked(packetDurationMs);
            double currentProtectionMs = GetProtectionLocked();
            double stepMs = Math.Max(20.0, Math.Max(0.0, shortageMs));
            double baseTargetMs = CalculateBaseTargetLocked(_lastSchedulerSlackMs);
            double minimumProtectionMs = Math.Max(0.0, MinNetworkProtectionMs);
            double effectiveMaxMs = Math.Max(MaxBufferMs, baseTargetMs + minimumProtectionMs);
            double maxProtectionMs = Math.Max(minimumProtectionMs, effectiveMaxMs - baseTargetMs);
            _underrunBoostMs = Math.Min(
                maxProtectionMs,
                Math.Max(_underrunBoostMs, currentProtectionMs + stepMs));
            RecalculateTargetLocked(baseSchedulerSlackMs: _lastSchedulerSlackMs, allowRelease: false);
        }
    }

    /// <summary>
    /// Returns total queued audio required before starting/resuming playback:
    /// the device/packet floor plus adaptive network protection.
    /// </summary>
    public int GetTargetBufferMs(double packetDurationMs = 0.0, int baseSchedulerSlackMs = 5)
    {
        lock (_sync)
        {
            UpdatePacketDurationLocked(packetDurationMs);
            RecalculateTargetLocked(Math.Max(0, baseSchedulerSlackMs), allowRelease: false);
            return (int)Math.Ceiling(_smoothedTargetMs);
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _lastMediaTimestampNs = 0;
            _lastArrivalTimestampNs = 0;
            _packetDurationMs = 0.0;
            _rfcJitterMs = 0.0;
            _lateQuantileMs = 0.0;
            _underrunBoostMs = 0.0;
            _smoothedTargetMs = 0.0;
            _baseTargetMs = 0.0;
            _lastSchedulerSlackMs = DefaultSchedulerSlackMs;
            Array.Clear(_lateHistogram);
        }
    }

    private void UpdatePacketDurationLocked(double packetDurationMs)
    {
        if (!double.IsFinite(packetDurationMs) || packetDurationMs <= 0.0)
        {
            return;
        }

        packetDurationMs = Math.Clamp(packetDurationMs, 1.0, 1000.0);
        _packetDurationMs = _packetDurationMs <= 0.0
            ? packetDurationMs
            : 0.9 * _packetDurationMs + 0.1 * packetDurationMs;
    }

    private void DecayLateHistogramLocked(double elapsedSeconds)
    {
        if (elapsedSeconds <= 0.0)
        {
            return;
        }

        double decay = Math.Pow(0.5, elapsedSeconds / LateHistogramHalfLifeSeconds);
        for (int i = 0; i < _lateHistogram.Length; i++)
        {
            _lateHistogram[i] *= decay;
        }
    }

    private void AddLateVariationLocked(double positiveVariationMs)
    {
        int bucket = Math.Clamp(
            (int)Math.Round(positiveVariationMs / LateBucketWidthMs, MidpointRounding.AwayFromZero),
            0,
            _lateHistogram.Length - 1);
        _lateHistogram[bucket] += 1.0;
    }

    private double QuantileLocked(double quantile)
    {
        double totalWeight = 0.0;
        for (int i = 0; i < _lateHistogram.Length; i++)
        {
            totalWeight += _lateHistogram[i];
        }
        if (totalWeight <= 0.0)
        {
            return 0.0;
        }

        double threshold = Math.Clamp(quantile, 0.0, 1.0) * totalWeight;
        double cumulative = 0.0;
        for (int i = 0; i < _lateHistogram.Length; i++)
        {
            cumulative += _lateHistogram[i];
            if (cumulative >= threshold)
            {
                return i * LateBucketWidthMs;
            }
        }

        return (_lateHistogram.Length - 1) * LateBucketWidthMs;
    }

    private double GetRfcProtectionLocked()
    {
        return Math.Max(
            0.0,
            Math.Max(0.0, JitterFactor) * _rfcJitterMs - JitterMeasurementDeadbandMs);
    }

    private double GetProtectionLocked()
    {
        return Math.Max(
            Math.Max(0.0, MinNetworkProtectionMs),
            Math.Max(_lateQuantileMs, Math.Max(GetRfcProtectionLocked(), _underrunBoostMs)));
    }

    private double CalculateBaseTargetLocked(double baseSchedulerSlackMs)
    {
        double packetMs = Math.Max(1.0, _packetDurationMs);
        double deviceMs = double.IsFinite(DeviceBufferMs) ? Math.Max(1.0, DeviceBufferMs) : 30.0;
        return Math.Max(deviceMs, packetMs + Math.Max(0.0, baseSchedulerSlackMs));
    }

    private void RecalculateTargetLocked(double baseSchedulerSlackMs, bool allowRelease)
    {
        _lastSchedulerSlackMs = Math.Max(0.0, baseSchedulerSlackMs);
        _baseTargetMs = CalculateBaseTargetLocked(_lastSchedulerSlackMs);
        double minimumProtectionMs = Math.Max(0.0, MinNetworkProtectionMs);
        double effectiveMaxMs = Math.Max(MaxBufferMs, _baseTargetMs + minimumProtectionMs);
        double rawTargetMs = Math.Clamp(
            _baseTargetMs + GetProtectionLocked(),
            _baseTargetMs,
            effectiveMaxMs);

        if (_smoothedTargetMs <= 0.0 || rawTargetMs >= _smoothedTargetMs)
        {
            // Fast attack: protect the next packet as soon as a late tail appears.
            _smoothedTargetMs = rawTargetMs;
        }
        else if (allowRelease)
        {
            // Slow release avoids oscillating between latency and underruns.
            double alpha = Math.Clamp(Alpha, 0.0, 0.999);
            _smoothedTargetMs = alpha * _smoothedTargetMs + (1.0 - alpha) * rawTargetMs;
            if (_smoothedTargetMs - rawTargetMs < TargetSnapThresholdMs)
            {
                _smoothedTargetMs = rawTargetMs;
            }
        }

        _smoothedTargetMs = Math.Clamp(_smoothedTargetMs, _baseTargetMs, effectiveMaxMs);
    }
}

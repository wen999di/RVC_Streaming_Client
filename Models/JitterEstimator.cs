using System;

namespace ClientAvalonia.Models;

public class JitterEstimator
{
    public double Alpha { get; set; } = 0.90;
    public double JitterFactor { get; set; } = 1.5;
    public double MinBufferMs { get; set; } = 20.0;
    public double MaxBufferMs { get; set; } = 350.0;

    // How quickly a previously observed late packet stops inflating the buffer.
    private const double PeakHalfLifeSeconds = 3.0;

    private long _baseMediaTimestampNs;
    private long _baseArrivalTimestampNs;
    private long _lastArrivalTimestampNs;
    private double _avgLatenessMs;
    private double _varLatenessMs;
    private double _peakLatenessMs;
    private bool _hasStatistics;

    public double EstimatedJitterMs { get; private set; }

    public void Update(long mediaTimestampNs, long arrivalTimestampNs)
    {
        if (mediaTimestampNs <= 0 || arrivalTimestampNs <= 0)
        {
            return;
        }

        if (_baseMediaTimestampNs == 0
            || mediaTimestampNs < _baseMediaTimestampNs
            || arrivalTimestampNs < _baseArrivalTimestampNs)
        {
            _baseMediaTimestampNs = mediaTimestampNs;
            _baseArrivalTimestampNs = arrivalTimestampNs;
            _lastArrivalTimestampNs = arrivalTimestampNs;
            return;
        }

        double mediaElapsedMs = (mediaTimestampNs - _baseMediaTimestampNs) / 1_000_000.0;
        double arrivalElapsedMs = (arrivalTimestampNs - _baseArrivalTimestampNs) / 1_000_000.0;

        // The server emits each inference block as a burst of smaller packets.
        // Raw packet IAT therefore contains the normal block interval (for example
        // 250 ms) and is not network jitter. Comparing arrival progress with the
        // media timestamp makes packets inside a burst early; only packets that
        // fall behind the media clock contribute to the protection buffer.
        double latenessMs = Math.Max(0.0, arrivalElapsedMs - mediaElapsedMs);

        if (_lastArrivalTimestampNs > 0 && _peakLatenessMs > 0)
        {
            double deltaSeconds = (arrivalTimestampNs - _lastArrivalTimestampNs) / 1_000_000_000.0;
            double decayFactor = Math.Pow(0.5, deltaSeconds / PeakHalfLifeSeconds);
            _peakLatenessMs *= decayFactor;
        }
        _lastArrivalTimestampNs = arrivalTimestampNs;

        if (latenessMs > _peakLatenessMs)
        {
            _peakLatenessMs = latenessMs;
        }

        if (!_hasStatistics)
        {
            _avgLatenessMs = latenessMs;
            _varLatenessMs = 0;
            _hasStatistics = true;
        }
        else
        {
            _avgLatenessMs = Alpha * _avgLatenessMs + (1 - Alpha) * latenessMs;
            double deviation = Math.Abs(latenessMs - _avgLatenessMs);
            _varLatenessMs = Alpha * _varLatenessMs + (1 - Alpha) * deviation;
        }

        EstimatedJitterMs = Math.Max(
            _peakLatenessMs,
            _avgLatenessMs + JitterFactor * _varLatenessMs);

        if (EstimatedJitterMs < 5)
        {
            EstimatedJitterMs = 5;
        }

        double effectiveMaxBufferMs = Math.Max(MaxBufferMs, MinBufferMs);
        if (EstimatedJitterMs > effectiveMaxBufferMs)
        {
            EstimatedJitterMs = effectiveMaxBufferMs;
        }
    }

    public int GetTargetBufferMs(int baseProcessingLatency = 10)
    {
        var target = baseProcessingLatency + EstimatedJitterMs;

        if (target < MinBufferMs)
        {
            target = MinBufferMs;
        }

        double effectiveMaxBufferMs = Math.Max(MaxBufferMs, MinBufferMs);
        if (target > effectiveMaxBufferMs)
        {
            target = effectiveMaxBufferMs;
        }

        return (int)Math.Ceiling(target);
    }

    public void Reset()
    {
        _baseMediaTimestampNs = 0;
        _baseArrivalTimestampNs = 0;
        _lastArrivalTimestampNs = 0;
        _avgLatenessMs = 0;
        _varLatenessMs = 0;
        _hasStatistics = false;
        _peakLatenessMs = 0;
        EstimatedJitterMs = 0;
    }
}

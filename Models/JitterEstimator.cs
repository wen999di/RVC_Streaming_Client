using System;
using System.Diagnostics;

namespace ClientAvalonia.Models;

public class JitterEstimator
{
    public double Alpha { get; set; } = 0.90;
    public double JitterFactor { get; set; } = 1.5;
    public double MinBufferMs { get; set; } = 20.0;
    public double BlockTimeMs { get; set; }
    public double MaxBufferMs { get; set; } = 350.0;

    // Peak IAT half-life: how quickly the tracked peak decays (seconds)
    private const double PeakHalfLifeSeconds = 3.0;

    private double _avgIat;
    private double _varIat;
    private long _lastReceiveTick;
    private double _peakIatMs;
    private long _peakLastUpdateTick;

    public double EstimatedJitterMs { get; private set; }

    public void Update()
    {
        long currentTick = Stopwatch.GetTimestamp();

        if (_lastReceiveTick == 0)
        {
            _lastReceiveTick = currentTick;
            _peakLastUpdateTick = currentTick;
            return;
        }

        double deltaTicks = currentTick - _lastReceiveTick;
        double currentIatMs = deltaTicks / Stopwatch.Frequency * 1000.0;
        _lastReceiveTick = currentTick;

        // Filter only extreme outliers (< 2ms means same-burst packet, > 500ms means disconnection)
        if (currentIatMs < 2)
        {
            return;
        }

        if (currentIatMs > 500)
        {
            return;
        }

        // Decay peak IAT based on elapsed time since last peak update
        if (_peakIatMs > 0)
        {
            double deltaSeconds = (currentTick - _peakLastUpdateTick) / Stopwatch.Frequency;
            double decayFactor = Math.Pow(0.5, deltaSeconds / PeakHalfLifeSeconds);
            _peakIatMs *= decayFactor;
            if (_peakIatMs < _avgIat + 5)
            {
                _peakIatMs = _avgIat + 5;
            }
        }
        _peakLastUpdateTick = currentTick;

        // Update peak if current IAT exceeds it
        if (currentIatMs > _peakIatMs)
        {
            _peakIatMs = currentIatMs;
        }

        // EMA for average IAT
        if (_avgIat == 0)
        {
            _avgIat = currentIatMs;
        }

        _avgIat = Alpha * _avgIat + (1 - Alpha) * currentIatMs;

        double deviation = Math.Abs(currentIatMs - _avgIat);
        if (_varIat == 0)
        {
            _varIat = deviation;
        }

        _varIat = Alpha * _varIat + (1 - Alpha) * deviation;

        // Jitter estimate: use peak IAT directly as the required buffer depth.
        // When all packets arrive consistently at inter-burst interval (e.g. 250ms),
        // EMA deviation is near zero, but we still need a buffer ≥ peak IAT.
        EstimatedJitterMs = _peakIatMs;

        if (EstimatedJitterMs < 5)
        {
            EstimatedJitterMs = 5;
        }

        if (EstimatedJitterMs > MaxBufferMs)
        {
            EstimatedJitterMs = MaxBufferMs;
        }
    }

    public int GetTargetBufferMs(int baseProcessingLatency = 10)
    {
        var target = baseProcessingLatency + EstimatedJitterMs;

        if (target < MinBufferMs)
        {
            target = MinBufferMs;
        }

        if (target < BlockTimeMs + 50)
        {
            target = BlockTimeMs + 50;
        }

        if (target > MaxBufferMs)
        {
            target = MaxBufferMs;
        }

        if (BlockTimeMs > 0 && target < BlockTimeMs)
        {
            target = BlockTimeMs + 10;
        }

        return (int)target;
    }

    public void Reset()
    {
        _avgIat = 0;
        _varIat = 0;
        _lastReceiveTick = 0;
        _peakIatMs = 0;
        _peakLastUpdateTick = 0;
        EstimatedJitterMs = 0;
    }
}

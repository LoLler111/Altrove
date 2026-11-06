// Adapted from celmic (MIT, Celmic contributors): see third_party/celmic/NOTICE.md.
namespace Altrove.Core.UsbMic;

/// <summary>
/// The tablet's voice waiting for the microphone: the network side pushes mono samples, the USB side pulls through a
/// <see cref="MicPlayout"/>, which resamples and gently speeds up or slows down to hold the target latency. That
/// absorbs Wi-Fi jitter and the drift between the tablet's and the PC's clocks without clicks.
/// </summary>
public sealed class MicJitterBuffer
{
    private const int MaxBufferSeconds = 2;
    internal readonly object Lock = new();
    internal readonly Queue<float> Queue = new(48_000);
    internal int InRate = 48_000;
    internal bool Buffering = true;
    internal long Generation;

    public long Underruns { get; internal set; }

    public void Push(ReadOnlySpan<float> samples, int rate)
    {
        lock (Lock)
        {
            if (rate != InRate && rate > 0)
            {
                Queue.Clear();
                InRate = rate;
                Buffering = true;
                Generation++;
            }
            foreach (var s in samples)
                Queue.Enqueue(s);
            // Nobody records from the microphone: keep memory bounded.
            var max = InRate * MaxBufferSeconds;
            while (Queue.Count > max)
                Queue.Dequeue();
        }
    }

    public void Clear()
    {
        lock (Lock)
        {
            Queue.Clear();
            Buffering = true;
            Generation++;
        }
    }

    public double FillMs
    {
        get
        {
            lock (Lock)
                return Queue.Count * 1000.0 / InRate;
        }
    }
}

/// <summary>The output side of a <see cref="MicJitterBuffer"/>, kept by its only reader.</summary>
public sealed class MicPlayout(int outRate)
{
    private const double MaxDriftCorrection = 0.003; // ±0.3 %
    private const double DriftGain = 0.004;
    private const double FillEma = 0.05;
    private float _previous, _current;
    private double _fraction = 1.0, _averageFill;
    private long _generation = long.MaxValue;

    private void Reset()
    {
        _previous = 0;
        _current = 0;
        _fraction = 1.0;
    }

    /// <summary>Fills <paramref name="output"/> (mono), keeping about <paramref name="targetMs"/> waiting.</summary>
    public void Fill(MicJitterBuffer buffer, int targetMs, Span<float> output)
    {
        lock (buffer.Lock)
        {
            if (buffer.Generation != _generation)
            {
                _generation = buffer.Generation;
                Reset();
            }
            double inRate = buffer.InRate;
            var target = Math.Max(1.0, inRate * targetMs / 1000.0);
            var queue = buffer.Queue;
            if (buffer.Buffering)
            {
                if (queue.Count < target)
                {
                    output.Clear();
                    return;
                }
                buffer.Buffering = false;
                _averageFill = queue.Count;
            }
            // Far too much waiting (a burst after a stall): jump back to the target.
            var max = target * 2 + inRate * 0.05;
            if (queue.Count > max)
            {
                var excess = queue.Count - (int)target;
                for (var i = 0; i < excess; i++)
                    queue.Dequeue();
                _averageFill = target;
            }

            _averageFill += (queue.Count - _averageFill) * FillEma;
            var error = (_averageFill - target) / target;
            var correction = Math.Clamp(error * DriftGain, -MaxDriftCorrection, MaxDriftCorrection);
            var step = inRate / outRate * (1 + correction);

            for (var i = 0; i < output.Length; i++)
            {
                while (_fraction >= 1.0)
                {
                    if (!queue.TryDequeue(out var sample))
                    {
                        buffer.Buffering = true;
                        buffer.Underruns++;
                        Reset();
                        output[i..].Clear();
                        return;
                    }
                    _previous = _current;
                    _current = sample;
                    _fraction -= 1.0;
                }
                output[i] = _previous + (_current - _previous) * (float)_fraction;
                _fraction += step;
            }
        }
    }
}

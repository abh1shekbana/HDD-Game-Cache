using System;

namespace SmartGameCache.Core.Learning;

/// <summary>
/// A high-performance 4-bit Count-Min Frequency Sketch with periodic half-life aging.
/// Inspired by the TinyLFU admission specification (Einziger, Eytan, Friedman, Manes).
/// Space efficiency: 4 bits per counter, capable of recording frequencies from 0 to 15.
/// </summary>
public class FrequencySketch
{
    private readonly ulong[] _table;
    private readonly int _mask;
    private int _sampleSize;
    private int _resetThreshold;

    private static readonly ulong[] Seeds = new ulong[]
    {
        0x517cc1b727220a95UL,
        0x31846b8a1c9ef02bUL,
        0x789b2c3d4e5f6071UL,
        0x9a8b7c6d5e4f3021UL
    };

    public FrequencySketch(int maximumCapacity = 65536)
    {
        int size = Math.Max(1024, ToNextPowerOfTwo(maximumCapacity));
        _table = new ulong[size >> 4]; // 16 4-bit counters per 64-bit word
        _mask = size - 1;
        _resetThreshold = size * 10;
    }

    public int EstimateFrequency(string key)
    {
        ulong hash = HashKey(key);
        int min = 15;

        for (int i = 0; i < 4; i++)
        {
            ulong h = Mix(hash, Seeds[i]);
            int index = (int)(h & (ulong)_mask);
            int count = ReadCounter(index);
            if (count < min) min = count;
        }

        return min;
    }

    public void RecordAccess(string key)
    {
        ulong hash = HashKey(key);

        for (int i = 0; i < 4; i++)
        {
            ulong h = Mix(hash, Seeds[i]);
            int index = (int)(h & (ulong)_mask);
            IncrementCounter(index);
        }

        if (++_sampleSize >= _resetThreshold)
        {
            AgeCounters();
        }
    }

    private int ReadCounter(int index)
    {
        int wordIndex = index >> 4;
        int shift = (index & 0x0F) << 2;
        return (int)((_table[wordIndex] >> shift) & 0x0FUL);
    }

    private void IncrementCounter(int index)
    {
        int wordIndex = index >> 4;
        int shift = (index & 0x0F) << 2;
        ulong count = (_table[wordIndex] >> shift) & 0x0FUL;
        if (count < 15UL)
        {
            _table[wordIndex] += (1UL << shift);
        }
    }

    private void AgeCounters()
    {
        // Divide every 4-bit counter by 2 to prevent stale frequency domination
        for (int i = 0; i < _table.Length; i++)
        {
            ulong word = _table[i];
            // Mask each 4-bit counter, shift right by 1, and recombine
            ulong oddBits = (word >> 1) & 0x7777777777777777UL;
            _table[i] = oddBits;
        }
        _sampleSize >>= 1;
    }

    private static ulong HashKey(string key)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char c in key)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }
        return hash;
    }

    private static ulong Mix(ulong hash, ulong seed)
    {
        hash ^= seed;
        hash *= 0xff51afd7ed558ccdUL;
        hash ^= hash >> 33;
        hash *= 0xc4ceb9fe1a85ec53UL;
        hash ^= hash >> 33;
        return hash;
    }

    private static int ToNextPowerOfTwo(int x)
    {
        x--;
        x |= x >> 1;
        x |= x >> 2;
        x |= x >> 4;
        x |= x >> 8;
        x |= x >> 16;
        return x + 1;
    }
}


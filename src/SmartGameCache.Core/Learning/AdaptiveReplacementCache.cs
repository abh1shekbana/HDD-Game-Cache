using System;
using System.Collections.Generic;

namespace SmartGameCache.Core.Learning;

/// <summary>
/// Adaptive Replacement Cache (ARC) implementation based on Nimrod Megiddo & Dharmendra S. Modha (FAST 2003).
/// Self-tunes between recency (T1) and frequency (T2) using ghost history lists (B1, B2).
/// Time complexity: O(1) for all operations.
/// </summary>
public class AdaptiveReplacementCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private double _targetP; // Target size for T1

    // Active Cache Lists (Store Key + Value)
    private readonly LinkedList<TKey> _t1 = new();
    private readonly LinkedList<TKey> _t2 = new();

    // Ghost History Lists (Store Keys Only)
    private readonly LinkedList<TKey> _b1 = new();
    private readonly LinkedList<TKey> _b2 = new();

    // Node Lookups
    private readonly Dictionary<TKey, (LinkedListNode<TKey> Node, TValue Value)> _t1Map = new();
    private readonly Dictionary<TKey, (LinkedListNode<TKey> Node, TValue Value)> _t2Map = new();
    private readonly Dictionary<TKey, LinkedListNode<TKey>> _b1Map = new();
    private readonly Dictionary<TKey, LinkedListNode<TKey>> _b2Map = new();

    public int Capacity => _capacity;
    public double TargetP => _targetP;
    public int T1Count => _t1.Count;
    public int T2Count => _t2.Count;
    public int B1Count => _b1.Count;
    public int B2Count => _b2.Count;

    public AdaptiveReplacementCache(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _targetP = 0.0;
    }

    public bool TryGet(TKey key, out TValue? value)
    {
        if (_t1Map.TryGetValue(key, out var t1Entry))
        {
            // Hit in T1: Promote from recency list T1 to frequency list T2
            _t1.Remove(t1Entry.Node);
            _t1Map.Remove(key);

            var newNode = _t2.AddFirst(key);
            _t2Map[key] = (newNode, t1Entry.Value);

            value = t1Entry.Value;
            return true;
        }

        if (_t2Map.TryGetValue(key, out var t2Entry))
        {
            // Hit in T2: Move to MRU of T2
            _t2.Remove(t2Entry.Node);
            var newNode = _t2.AddFirst(key);
            _t2Map[key] = (newNode, t2Entry.Value);

            value = t2Entry.Value;
            return true;
        }

        value = default;
        return false;
    }

    public (bool Hit, TKey? EvictedKey) Put(TKey key, TValue value)
    {
        // Case 1: Key already in T1 or T2
        if (TryGet(key, out _))
        {
            // Update value if changed
            if (_t2Map.TryGetValue(key, out var entry))
            {
                _t2Map[key] = (entry.Node, value);
            }
            return (true, default);
        }

        TKey? evicted = default;

        // Case 2: Hit in Ghost List B1 (Evicted from T1 too soon -> increase p)
        if (_b1Map.TryGetValue(key, out var b1Node))
        {
            double delta = _b1.Count >= _b2.Count ? 1.0 : (double)_b2.Count / _b1.Count;
            _targetP = Math.Min(_capacity, _targetP + delta);

            evicted = Replace(key);

            _b1.Remove(b1Node);
            _b1Map.Remove(key);

            var t2Node = _t2.AddFirst(key);
            _t2Map[key] = (t2Node, value);
            return (false, evicted);
        }

        // Case 3: Hit in Ghost List B2 (Evicted from T2 too soon -> decrease p)
        if (_b2Map.TryGetValue(key, out var b2Node))
        {
            double delta = _b2.Count >= _b1.Count ? 1.0 : (double)_b1.Count / _b2.Count;
            _targetP = Math.Max(0.0, _targetP - delta);

            evicted = Replace(key);

            _b2.Remove(b2Node);
            _b2Map.Remove(key);

            var t2Node = _t2.AddFirst(key);
            _t2Map[key] = (t2Node, value);
            return (false, evicted);
        }

        // Case 4: Completely new item (Cache Miss & Ghost Miss)
        int l1Size = _t1.Count + _b1.Count;
        if (l1Size == _capacity)
        {
            if (_t1.Count < _capacity)
            {
                // Discard LRU of B1
                var b1Last = _b1.Last!;
                _b1.RemoveLast();
                _b1Map.Remove(b1Last.Value);
                evicted = Replace(key);
            }
            else
            {
                // Discard LRU of T1
                var t1Last = _t1.Last!;
                _t1.RemoveLast();
                _t1Map.Remove(t1Last.Value);
                evicted = t1Last.Value;
            }
        }
        else if (l1Size < _capacity)
        {
            int totalSize = _t1.Count + _t2.Count + _b1.Count + _b2.Count;
            if (totalSize >= _capacity)
            {
                if (totalSize == 2 * _capacity && _b2.Count > 0)
                {
                    var b2Last = _b2.Last!;
                    _b2.RemoveLast();
                    _b2Map.Remove(b2Last.Value);
                }
                evicted = Replace(key);
            }
        }

        var newT1Node = _t1.AddFirst(key);
        _t1Map[key] = (newT1Node, value);
        return (false, evicted);
    }

    private TKey? Replace(TKey key)
    {
        if (_t1.Count > 0 && ((_t1.Count > _targetP) || (_b2Map.ContainsKey(key) && _t1.Count == (int)_targetP)))
        {
            // Evict LRU from T1 to ghost list B1
            var t1Victim = _t1.Last!;
            _t1.RemoveLast();
            _t1Map.Remove(t1Victim.Value);

            var b1Node = _b1.AddFirst(t1Victim.Value);
            _b1Map[t1Victim.Value] = b1Node;
            return t1Victim.Value;
        }
        else if (_t2.Count > 0)
        {
            // Evict LRU from T2 to ghost list B2
            var t2Victim = _t2.Last!;
            _t2.RemoveLast();
            _t2Map.Remove(t2Victim.Value);

            var b2Node = _b2.AddFirst(t2Victim.Value);
            _b2Map[t2Victim.Value] = b2Node;
            return t2Victim.Value;
        }

        return default;
    }
}


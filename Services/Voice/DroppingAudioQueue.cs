using System;
using System.Threading;

namespace Kinectv1.Voice
{
    /// <summary>
    /// Bounded audio queue with drop-oldest policy to prevent unbounded drift.
    /// Thread-safe for single producer / single consumer pattern.
    /// </summary>
    public sealed class DroppingAudioQueue<T>
    {
        private readonly T[] _buffer;
        private readonly int _capacity;
        private int _head;
        private int _tail;
        private int _count;
        private readonly object _lock = new object();

        private long _totalEnqueued;
        private long _totalDropped;
        private DateTime _lastStatTime = DateTime.UtcNow;
        private int _droppedSinceLastStat;

        public DroppingAudioQueue(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
            _buffer = new T[capacity];
        }

        /// <summary>
        /// Current number of items in the queue.
        /// </summary>
        public int Count { get { lock (_lock) return _count; } }

        /// <summary>
        /// Total items ever enqueued.
        /// </summary>
        public long TotalEnqueued => Interlocked.Read(ref _totalEnqueued);

        /// <summary>
        /// Total items dropped due to overflow.
        /// </summary>
        public long TotalDropped => Interlocked.Read(ref _totalDropped);

        /// <summary>
        /// Enqueue an item. If queue is full, drops oldest items until there's room.
        /// </summary>
        public void Enqueue(T item)
        {
            lock (_lock)
            {
                // If full, drop oldest
                while (_count >= _capacity)
                {
                    _head = (_head + 1) % _capacity;
                    _count--;
                    Interlocked.Increment(ref _totalDropped);
                    _droppedSinceLastStat++;
                }

                _buffer[_tail] = item;
                _tail = (_tail + 1) % _capacity;
                _count++;
                Interlocked.Increment(ref _totalEnqueued);
            }
        }

        /// <summary>
        /// Try to dequeue an item.
        /// </summary>
        public bool TryDequeue(out T item)
        {
            lock (_lock)
            {
                if (_count == 0)
                {
                    item = default;
                    return false;
                }

                item = _buffer[_head];
                _buffer[_head] = default; // Allow GC
                _head = (_head + 1) % _capacity;
                _count--;
                return true;
            }
        }

        /// <summary>
        /// Clear all items from the queue.
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                Array.Clear(_buffer, 0, _buffer.Length);
                _head = 0;
                _tail = 0;
                _count = 0;
            }
        }

        /// <summary>
        /// Get statistics and optionally reset dropped counter.
        /// </summary>
        public (int depth, int droppedDelta, long totalDropped, double depthMs) GetStats(int sampleRate = 16000, int samplesPerFrame = 320)
        {
            lock (_lock)
            {
                var depth = _count;
                var dropped = _droppedSinceLastStat;
                _droppedSinceLastStat = 0;

                // Estimate depth in milliseconds
                double depthMs = depth * (samplesPerFrame / (double)sampleRate) * 1000.0;

                return (depth, dropped, TotalDropped, depthMs);
            }
        }
    }
}

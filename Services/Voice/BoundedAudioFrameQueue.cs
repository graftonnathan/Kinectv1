using System;
using System.Collections.Generic;

namespace Kinectv1
{
    internal sealed class BoundedAudioFrameQueue
    {
        private readonly object _lock = new object();
        private readonly Queue<(short[] pcm, int sampleRate, int channels)> _q;
        private readonly int _capacity;

        public int DroppedFrames { get; private set; }

        public BoundedAudioFrameQueue(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
            _q = new Queue<(short[] pcm, int sampleRate, int channels)>(capacity);
        }

        public int Count
        {
            get { lock (_lock) return _q.Count; }
        }

        public void Enqueue(short[] pcm, int sampleRate, int channels)
        {
            if (pcm == null || pcm.Length == 0) return;

            lock (_lock)
            {
                while (_q.Count >= _capacity)
                {
                    _q.Dequeue();
                    DroppedFrames++;
                }
                _q.Enqueue((pcm, sampleRate, channels));
            }
        }

        public bool TryDequeue(out short[] pcm, out int sampleRate, out int channels)
        {
            lock (_lock)
            {
                if (_q.Count == 0)
                {
                    pcm = null;
                    sampleRate = 0;
                    channels = 0;
                    return false;
                }

                var item = _q.Dequeue();
                pcm = item.pcm;
                sampleRate = item.sampleRate;
                channels = item.channels;
                return true;
            }
        }
    }
}

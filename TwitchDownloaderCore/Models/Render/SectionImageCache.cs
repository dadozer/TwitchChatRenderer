using System.Collections.Concurrent;

namespace TwitchDownloaderCore.Models.Render
{
    internal sealed class SectionImageCache : IDisposable
    {
        private const int MaxStandardHeightBuckets = 384;
        private const int MaxTallHeightBuckets = 48;
        private const int StandardHeightThreshold = 96;

        private sealed class PooledBucket
        {
            public readonly ConcurrentQueue<SectionImage> Queue = new();
            public int Count;
        }

        private readonly ConcurrentDictionary<(int Width, int Height), PooledBucket> _cache = [];

        public SectionImage Rent(int width, int height)
        {
            if (_cache.TryGetValue((width, height), out var bucket) && bucket.Queue.TryDequeue(out var image))
            {
                Interlocked.Decrement(ref bucket.Count);
                image.Canvas.Clear();
                return image;
            }

            return new SectionImage(width, height);
        }

        public void Return(SectionImage sectionImage)
        {
            if (!sectionImage.IsAllocated)
            {
                return;
            }

            var key = (sectionImage.Info.Width, sectionImage.Info.Height);
            var bucket = _cache.GetOrAdd(key, static _ => new PooledBucket());
            int cap = key.Height <= StandardHeightThreshold ? MaxStandardHeightBuckets : MaxTallHeightBuckets;

            if (Interlocked.Increment(ref bucket.Count) <= cap)
            {
                bucket.Queue.Enqueue(sectionImage);
            }
            else
            {
                Interlocked.Decrement(ref bucket.Count);
                sectionImage.Dispose();
            }
        }

        internal int GetPooledCount(int width, int height)
        {
            return _cache.TryGetValue((width, height), out var bucket) ? Math.Max(0, Volatile.Read(ref bucket.Count)) : 0;
        }

        public void Dispose()
        {
            foreach (var bucket in _cache.Values)
            {
                while (bucket.Queue.TryDequeue(out var sectionImage))
                {
                    sectionImage.Dispose();
                }
                Interlocked.Exchange(ref bucket.Count, 0);
            }
            _cache.Clear();
        }
    }
}
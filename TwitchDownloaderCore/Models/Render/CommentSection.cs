namespace TwitchDownloaderCore.Models.Render
{
    public class CommentSection
    {
        private int _released;

        public SectionImage Image { get; init; }
        public List<EmotePosition> Emotes { get; set; }
        public int CommentIndex { get; set; }
        public bool IsModeratedRendered { get; set; }
        public bool HasAnimatedEmotes { get; init; }
        public int MaxTopOverhang { get; init; }
        public int MaxBottomOverhang { get; init; }

        public bool IsReleased => Volatile.Read(ref _released) != 0;

        internal void Release(SectionImageCache cache)
        {
            if (Interlocked.Exchange(ref _released, 1) == 0 && Image.IsAllocated)
            {
                if (cache is not null)
                {
                    cache.Return(Image);
                }
                else
                {
                    Image.Dispose();
                }
            }
        }
    }
}

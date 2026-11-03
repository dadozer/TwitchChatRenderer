using SkiaSharp;

namespace TwitchDownloaderCore.Models.Render
{
    public class CommentSection
    {
        public SKImage Image { get; set; }
        public List<EmotePosition> Emotes { get; set; }
        public int CommentIndex { get; set; }
        public bool IsModeratedRendered { get; set; }
    }
}

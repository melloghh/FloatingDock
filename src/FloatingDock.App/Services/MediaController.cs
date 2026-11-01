using Windows.Media;
using Windows.Media.Control;

namespace FloatingDock.UI.Services;
/// <param name="Position">Seconds played, already advanced to "now" while playing.</param>
/// <param name="Duration">Track length in seconds, 0 when the player does not report one.</param>
/// <param name="Repeat">0 = off, 1 = repeat track, 2 = repeat list.</param>
internal sealed record MediaState(string Title,string Artist,string Source,bool Previous,bool Toggle,bool Next,bool Playing,
    double Position=0,double Duration=0,bool CanShuffle=false,bool Shuffle=false,bool CanRepeat=false,int Repeat=0);
internal interface IMediaController
{
    Task<MediaState?> ReadAsync();
    Task<bool> ControlAsync(string command);
}
/// <summary>Windows' current media session; no audio interception, injected hooks or continuous polling.</summary>
internal sealed class MediaController : IMediaController
{
    private Task<GlobalSystemMediaTransportControlsSessionManager>? _manager;
    private async Task<GlobalSystemMediaTransportControlsSession?> Current()
    {
        try { _manager??=GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask();return (await _manager).GetCurrentSession(); }
        catch { _manager=null;throw; }
    }
    public async Task<MediaState?> ReadAsync()
    {
        var session=await Current();if(session is null)return null;
        var media=await session.TryGetMediaPropertiesAsync();var playback=session.GetPlaybackInfo();var controls=playback.Controls;
        double position=0,duration=0;
        try
        {
            var timeline=session.GetTimelineProperties();
            duration=Math.Max(0,(timeline.EndTime-timeline.StartTime).TotalSeconds);
            position=Math.Max(0,(timeline.Position-timeline.StartTime).TotalSeconds);
            bool playing=playback.PlaybackStatus==GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if(playing)position+=Math.Max(0,(DateTimeOffset.Now-timeline.LastUpdatedTime).TotalSeconds*(playback.PlaybackRate??1));
            if(duration>0)position=Math.Min(position,duration);
        }
        catch { }
        int repeat=playback.AutoRepeatMode switch { MediaPlaybackAutoRepeatMode.Track=>1,MediaPlaybackAutoRepeatMode.List=>2,_=>0 };
        return new(media.Title,media.Artist,session.SourceAppUserModelId,controls.IsPreviousEnabled,controls.IsPlayPauseToggleEnabled,controls.IsNextEnabled,
            playback.PlaybackStatus==GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,position,duration,
            controls.IsShuffleEnabled,playback.IsShuffleActive==true,controls.IsRepeatEnabled,repeat);
    }
    public async Task<bool> ControlAsync(string command)
    {
        var session=await Current();if(session is null)return false;
        var playback=session.GetPlaybackInfo();
        return command switch
        {
            "previous"=>await session.TrySkipPreviousAsync(),"next"=>await session.TrySkipNextAsync(),"toggle"=>await session.TryTogglePlayPauseAsync(),
            "shuffle"=>await session.TryChangeShuffleActiveAsync(playback.IsShuffleActive!=true),
            "repeat"=>await session.TryChangeAutoRepeatModeAsync(playback.AutoRepeatMode switch { MediaPlaybackAutoRepeatMode.None=>MediaPlaybackAutoRepeatMode.List,MediaPlaybackAutoRepeatMode.List=>MediaPlaybackAutoRepeatMode.Track,_=>MediaPlaybackAutoRepeatMode.None }),
            _=>false,
        };
    }
}

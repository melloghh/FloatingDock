using Windows.Media.Control;

namespace FloatingDock.UI.Services;
internal sealed record MediaState(string Title,string Artist,string Source,bool Previous,bool Toggle,bool Next,bool Playing);
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
        return new(media.Title,media.Artist,session.SourceAppUserModelId,controls.IsPreviousEnabled,controls.IsPlayPauseToggleEnabled,controls.IsNextEnabled,playback.PlaybackStatus==GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
    }
    public async Task<bool> ControlAsync(string command)
    {
        var session=await Current();if(session is null)return false;
        return command switch { "previous"=>await session.TrySkipPreviousAsync(),"next"=>await session.TrySkipNextAsync(),"toggle"=>await session.TryTogglePlayPauseAsync(),_=>false };
    }
}

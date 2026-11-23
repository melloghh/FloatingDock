using Windows.Media;
using Windows.Media.Control;

namespace FloatingDock.Engine;

/// <param name="Position">Seconds played, already advanced to "now" while playing.</param>
/// <param name="Duration">Track length in seconds, 0 when the player does not report one.</param>
/// <param name="Repeat">0 = off, 1 = repeat track, 2 = repeat list.</param>
internal sealed record MediaState(string Title, string Artist, string Source, bool Previous, bool Toggle, bool Next, bool Playing,
    double Position = 0, double Duration = 0, bool CanShuffle = false, bool Shuffle = false, bool CanRepeat = false, int Repeat = 0);

/// <summary>Windows' current media session (GSMTC); read on demand, no audio interception, hooks or polling.</summary>
internal sealed class MediaController
{
    private Task<GlobalSystemMediaTransportControlsSessionManager>? _manager;

    private async Task<GlobalSystemMediaTransportControlsSession?> Current()
    {
        try { _manager ??= GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(); return (await _manager).GetCurrentSession(); }
        catch { _manager = null; throw; }
    }

    public async Task<MediaState?> ReadAsync()
    {
        var session = await Current();
        if (session is null) return null;
        var (title, artist) = await PropertiesOf(session);
        var playback = session.GetPlaybackInfo();
        var controls = playback.Controls;
        bool playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        double position = 0, duration = 0;
        try
        {
            var timeline = session.GetTimelineProperties();
            duration = Math.Max(0, (timeline.EndTime - timeline.StartTime).TotalSeconds);
            position = Math.Max(0, (timeline.Position - timeline.StartTime).TotalSeconds);
            if (playing) position += Math.Max(0, (DateTimeOffset.Now - timeline.LastUpdatedTime).TotalSeconds * (playback.PlaybackRate ?? 1));
            if (duration > 0) position = Math.Min(position, duration);
        }
        catch (Exception) { /* some players report no timeline */ }
        (position, duration) = Smooth(title, session.SourceAppUserModelId, playing, position, duration);
        int repeat = playback.AutoRepeatMode switch { MediaPlaybackAutoRepeatMode.Track => 1, MediaPlaybackAutoRepeatMode.List => 2, _ => 0 };
        return new(title, artist, session.SourceAppUserModelId, controls.IsPreviousEnabled, controls.IsPlayPauseToggleEnabled,
            controls.IsNextEnabled, playing, position, duration, controls.IsShuffleEnabled, playback.IsShuffleActive == true,
            controls.IsRepeatEnabled, repeat);
    }

    // Reading the title and artist is the costly part of a read (the player is asked, thumbnail and all), and the bar reads every couple of
    // seconds: the answer is kept until the session says it changed (or for a few seconds, whichever comes first).
    private GlobalSystemMediaTransportControlsSession? _propsSession;
    private (string Title, string Artist) _props;
    private DateTime _propsAt = DateTime.MinValue;
    private volatile bool _propsStale = true;

    private async Task<(string Title, string Artist)> PropertiesOf(GlobalSystemMediaTransportControlsSession session)
    {
        if (!ReferenceEquals(session, _propsSession))
        {
            if (_propsSession is not null) _propsSession.MediaPropertiesChanged -= OnPropertiesChanged;
            _propsSession = session;
            session.MediaPropertiesChanged += OnPropertiesChanged;
            _propsStale = true;
        }
        if (_propsStale || DateTime.UtcNow - _propsAt > TimeSpan.FromSeconds(4))
        {
            _propsStale = false;
            var media = await session.TryGetMediaPropertiesAsync();
            _props = (media.Title, media.Artist);
            _propsAt = DateTime.UtcNow;
        }
        return _props;
    }

    private void OnPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => _propsStale = true;

    // Browsers publish an empty timeline (position 0, no length) a few seconds after play starts when the page does not report one.
    // Taken at face value the progress would jump back to zero, so the last real timeline of the track is carried on with the clock.
    private string _trackKey = "";
    private double _anchorPosition, _anchorDuration;
    private DateTime _anchorAt;
    private bool _anchorPlaying;

    private (double Position, double Duration) Smooth(string title, string source, bool playing, double position, double duration)
    {
        var key = source + "\n" + title;
        var now = DateTime.UtcNow;
        lock (_anchorLock)          // reads come from IPC handlers on several threads
        {
            if (key != _trackKey) { _trackKey = key; _anchorDuration = 0; }
            if (duration > 0)
            {
                (_anchorPosition, _anchorDuration, _anchorAt, _anchorPlaying) = (position, duration, now, playing);
                return (position, duration);
            }
            if (_anchorDuration <= 0) return (position, duration);        // this track never had a timeline: nothing to carry on
            var estimate = Math.Min(_anchorPosition + (_anchorPlaying ? (now - _anchorAt).TotalSeconds : 0), _anchorDuration);
            (_anchorPosition, _anchorAt, _anchorPlaying) = (estimate, now, playing);
            return (estimate, _anchorDuration);
        }
    }

    private readonly object _anchorLock = new();

    public async Task<bool> ControlAsync(string command)
    {
        var session = await Current();
        if (session is null) return false;
        var playback = session.GetPlaybackInfo();
        return command switch
        {
            "previous" => await session.TrySkipPreviousAsync(),
            "next" => await session.TrySkipNextAsync(),
            "toggle" => await session.TryTogglePlayPauseAsync(),
            "shuffle" => await session.TryChangeShuffleActiveAsync(playback.IsShuffleActive != true),
            "repeat" => await session.TryChangeAutoRepeatModeAsync(playback.AutoRepeatMode switch
            {
                MediaPlaybackAutoRepeatMode.None => MediaPlaybackAutoRepeatMode.List,
                MediaPlaybackAutoRepeatMode.List => MediaPlaybackAutoRepeatMode.Track,
                _ => MediaPlaybackAutoRepeatMode.None,
            }),
            _ => false,
        };
    }
}

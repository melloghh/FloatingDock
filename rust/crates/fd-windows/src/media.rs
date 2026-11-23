//! Windows' current media session (GSMTC); read on demand, no audio interception, hooks or polling.
//! Blocking calls: run them on a worker thread, never on the UI loop.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use windows::Foundation::TypedEventHandler;
use windows::Media::Control::{
    GlobalSystemMediaTransportControlsSession, GlobalSystemMediaTransportControlsSessionManager,
    GlobalSystemMediaTransportControlsSessionPlaybackStatus, MediaPropertiesChangedEventArgs,
};
use windows::Media::MediaPlaybackAutoRepeatMode;

#[derive(Debug, Clone, PartialEq)]
pub struct MediaState {
    pub title: String,
    pub artist: String,
    pub source: String,
    pub previous: bool,
    pub toggle: bool,
    pub next: bool,
    pub playing: bool,
    /// Seconds played, already advanced to "now" while playing.
    pub position: f64,
    /// Track length in seconds, 0 when the player does not report one.
    pub duration: f64,
    pub can_shuffle: bool,
    pub shuffle: bool,
    pub can_repeat: bool,
    /// 0 = off, 1 = repeat track, 2 = repeat list.
    pub repeat: i32,
}

#[derive(Default)]
struct Props {
    source: String,
    title: String,
    artist: String,
    at: Option<Instant>,
}

#[derive(Default)]
struct Anchor {
    track_key: String,
    position: f64,
    duration: f64,
    at: Option<Instant>,
    playing: bool,
}

pub struct MediaController {
    manager: Mutex<Option<GlobalSystemMediaTransportControlsSessionManager>>,
    props: Mutex<Props>,
    props_stale: Arc<AtomicBool>,
    watched_source: Mutex<String>,
    anchor: Mutex<Anchor>,
}

impl Default for MediaController {
    fn default() -> Self {
        Self::new()
    }
}

/// 100-ns ticks since 1601 for "now" (the epoch of the `DateTime` of WinRT).
fn now_ticks() -> i64 {
    let since_unix = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap_or_default();
    (since_unix.as_nanos() / 100) as i64 + 116_444_736_000_000_000
}

impl MediaController {
    pub fn new() -> Self {
        Self {
            manager: Mutex::new(None),
            props: Mutex::new(Props::default()),
            props_stale: Arc::new(AtomicBool::new(true)),
            watched_source: Mutex::new(String::new()),
            anchor: Mutex::new(Anchor::default()),
        }
    }

    fn current(&self) -> Result<Option<GlobalSystemMediaTransportControlsSession>, String> {
        let mut m = self.manager.lock().unwrap();
        if m.is_none() {
            let op = GlobalSystemMediaTransportControlsSessionManager::RequestAsync().map_err(|e| e.to_string())?;
            *m = Some(op.join().map_err(|e| e.to_string())?);
        }
        Ok(m.as_ref().unwrap().GetCurrentSession().ok())
    }

    pub fn read(&self) -> Result<Option<MediaState>, String> {
        let Some(session) = self.current().inspect_err(|_| *self.manager.lock().unwrap() = None)? else { return Ok(None) };
        let source = session.SourceAppUserModelId().map(|s| s.to_string()).unwrap_or_default();
        let (title, artist) = self.properties_of(&session, &source);
        let playback = session.GetPlaybackInfo().map_err(|e| e.to_string())?;
        let controls = playback.Controls().map_err(|e| e.to_string())?;
        let playing = playback.PlaybackStatus().map_err(|e| e.to_string())? == GlobalSystemMediaTransportControlsSessionPlaybackStatus::Playing;
        let (mut position, mut duration) = (0.0f64, 0.0f64);
        if let Ok(tl) = session.GetTimelineProperties() {
            let secs = |t: windows::Foundation::TimeSpan| t.Duration as f64 / 10_000_000.0;
            let (start, end, pos) = (tl.StartTime().map(secs).unwrap_or(0.0), tl.EndTime().map(secs).unwrap_or(0.0), tl.Position().map(secs).unwrap_or(0.0));
            duration = (end - start).max(0.0);
            position = (pos - start).max(0.0);
            if playing {
                let updated = tl.LastUpdatedTime().map(|t| t.UniversalTime).unwrap_or_else(|_| now_ticks());
                let rate = playback.PlaybackRate().and_then(|r| r.Value()).unwrap_or(1.0);
                position += ((now_ticks() - updated) as f64 / 10_000_000.0).max(0.0) * rate;
            }
            if duration > 0.0 {
                position = position.min(duration);
            }
        }
        let (position, duration) = self.smooth(&title, &source, playing, position, duration);
        let repeat = match playback.AutoRepeatMode().and_then(|m| m.Value()) {
            Ok(MediaPlaybackAutoRepeatMode::Track) => 1,
            Ok(MediaPlaybackAutoRepeatMode::List) => 2,
            _ => 0,
        };
        Ok(Some(MediaState {
            title,
            artist,
            source,
            previous: controls.IsPreviousEnabled().unwrap_or(false),
            toggle: controls.IsPlayPauseToggleEnabled().unwrap_or(false),
            next: controls.IsNextEnabled().unwrap_or(false),
            playing,
            position,
            duration,
            can_shuffle: controls.IsShuffleEnabled().unwrap_or(false),
            shuffle: playback.IsShuffleActive().and_then(|b| b.Value()).unwrap_or(false),
            can_repeat: controls.IsRepeatEnabled().unwrap_or(false),
            repeat,
        }))
    }

    // Reading the title and artist is the costly part of a read (the player is asked, thumbnail and all), and the bar reads
    // every couple of seconds: the answer is kept until the session says it changed (or for a few seconds, whichever is first).
    fn properties_of(&self, session: &GlobalSystemMediaTransportControlsSession, source: &str) -> (String, String) {
        {
            let mut w = self.watched_source.lock().unwrap();
            if *w != source {
                *w = source.to_string();
                let stale = self.props_stale.clone();
                let _ = session.MediaPropertiesChanged(&TypedEventHandler::<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs>::new(
                    move |_, _| {
                        stale.store(true, Ordering::SeqCst);
                        Ok(())
                    },
                ));
                self.props_stale.store(true, Ordering::SeqCst);
            }
        }
        let mut props = self.props.lock().unwrap();
        let fresh = props.source == source && props.at.is_some_and(|t| t.elapsed() < Duration::from_secs(4));
        if self.props_stale.swap(false, Ordering::SeqCst) || !fresh {
            if let Ok(media) = session.TryGetMediaPropertiesAsync().and_then(|op| op.join()) {
                props.title = media.Title().map(|s| s.to_string()).unwrap_or_default();
                props.artist = media.Artist().map(|s| s.to_string()).unwrap_or_default();
                props.source = source.to_string();
                props.at = Some(Instant::now());
            }
        }
        (props.title.clone(), props.artist.clone())
    }

    // Browsers publish an empty timeline (position 0, no length) a few seconds after play starts when the page does not report
    // one. Taken at face value the progress would jump back to zero, so the last real timeline of the track is carried on.
    fn smooth(&self, title: &str, source: &str, playing: bool, position: f64, duration: f64) -> (f64, f64) {
        let key = format!("{source}\n{title}");
        let now = Instant::now();
        let mut a = self.anchor.lock().unwrap();
        if a.track_key != key {
            a.track_key = key;
            a.duration = 0.0;
        }
        if duration > 0.0 {
            *a = Anchor { track_key: a.track_key.clone(), position, duration, at: Some(now), playing };
            return (position, duration);
        }
        if a.duration <= 0.0 {
            return (position, duration); // this track never had a timeline: nothing to carry on
        }
        let advanced = if a.playing { a.at.map_or(0.0, |t| now.duration_since(t).as_secs_f64()) } else { 0.0 };
        let estimate = (a.position + advanced).min(a.duration);
        a.position = estimate;
        a.at = Some(now);
        a.playing = playing;
        (estimate, a.duration)
    }

    pub fn control(&self, command: &str) -> Result<bool, String> {
        let Some(session) = self.current()? else { return Ok(false) };
        let playback = session.GetPlaybackInfo().map_err(|e| e.to_string())?;
        macro_rules! done {
            ($r:expr) => {
                $r.and_then(|op| op.join()).unwrap_or(false)
            };
        }
        Ok(match command {
            "previous" => done!(session.TrySkipPreviousAsync()),
            "next" => done!(session.TrySkipNextAsync()),
            "toggle" => done!(session.TryTogglePlayPauseAsync()),
            "shuffle" => {
                let active = playback.IsShuffleActive().and_then(|b| b.Value()).unwrap_or(false);
                done!(session.TryChangeShuffleActiveAsync(!active))
            }
            "repeat" => {
                let next = match playback.AutoRepeatMode().and_then(|m| m.Value()) {
                    Ok(MediaPlaybackAutoRepeatMode::None) | Err(_) => MediaPlaybackAutoRepeatMode::List,
                    Ok(MediaPlaybackAutoRepeatMode::List) => MediaPlaybackAutoRepeatMode::Track,
                    Ok(_) => MediaPlaybackAutoRepeatMode::None,
                };
                done!(session.TryChangeAutoRepeatModeAsync(next))
            }
            _ => false,
        })
    }
}

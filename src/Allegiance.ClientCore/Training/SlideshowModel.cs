namespace Allegiance.ClientCore.Training;

/// <summary>
/// P785 — the training slideshow: the pure-model port of <c>WinTrek/Slideshow.cpp</c>.
/// Native shows a sequence of (image, SoundID) pairs from the mission's MDL
/// <c>slides</c> list: each slide starts its sound and waits for the sound-finish
/// event, which arms a 1.0 s timer before the next slide (<c>Slideshow::NextSlide</c>
/// <c>Slideshow.cpp:80-116</c>, <c>Slideshow::OnEvent</c> :123-146 — finish →
/// <c>AddSink(m_pEventSink, 1.0f)</c>); any key down or a click skips straight to the
/// next slide (<c>Slideshow::OnKey</c> :156-160, <c>Slideshow::Picked</c> :148-151);
/// past the last slide the slideshow is <c>Dismiss()</c>ed (:108-113). The dismiss
/// destination is the subclass's: <c>TrainingSlideshow::Dismiss</c>
/// (<c>WinTrek/TrainingSlideshow.cpp:30-50</c>) shows "Training simulation initiated..."
/// and starts the mission, <c>PostTrainingSlideshow::Dismiss</c>
/// (<c>WinTrek/PostTrainingSlideshow.cpp:25-29</c>) returns to the training screen.
/// Native slide artwork/audio (tm_N_* namespaces, tm_slide_* clips) is not in the
/// public install, so the port renders textual captions standing in for the bitmaps
/// (visual parity: visuals may differ) and carries the native per-slide sound stems.
/// </summary>
public sealed class SlideshowModel
{
    public sealed record Slide(string Caption, string? Detail, string SoundStem);

    /// <summary>The 1.0 s pause after a slide's sound finishes (Slideshow.cpp:133).</summary>
    public const double PostSoundDelaySeconds = 1.0;

    private readonly IReadOnlyList<Slide> _slides;
    private double _postSoundTimer = -1.0;   // -1 = not armed

    public SlideshowModel(IReadOnlyList<Slide> slides, string title)
    {
        _slides = slides;
        Title = title;
    }

    /// <summary>Screen title (native draws none — the slide image fills the screen;
    /// the port labels the stand-in slide).</summary>
    public string Title { get; }

    /// <summary>Index of the slide being shown (the list cursor AFTER
    /// <c>m_pSlideList-&gt;GetNext()</c>, i.e. the slide currently displayed).</summary>
    public int Index { get; private set; } = -1;

    /// <summary>The slide currently displayed (stays on the last slide through the
    /// dismiss frame — the wrap image still shows it while the screen navigates
    /// away, native Dismiss then swaps screens). Callers read <see cref="Dismissed"/>.</summary>
    public Slide? Current => Index >= 0 && Index < _slides.Count ? _slides[Index] : null;

    public int Count => _slides.Count;

    /// <summary>The full slide list (test/inspection access).</summary>
    public IReadOnlyList<Slide> Slides => _slides;

    /// <summary>Whether the slideshow is running (static <c>Slideshow::IsInSlideShow</c>,
    /// Slideshow.cpp:171-174 — set in the constructor, cleared in the destructor).</summary>
    public bool IsInSlideShow { get; private set; }

    /// <summary>Set once the last slide has been shown — the caller performs the
    /// subclass's Dismiss (start the mission / return to the training screen).</summary>
    public bool Dismissed { get; private set; }

    /// <summary>Whether the current slide's sound-finish timer is armed and still
    /// counting down (native <c>m_bInTimer</c>).</summary>
    public bool TimerArmed => _postSoundTimer >= 0.0;

    /// <summary>Native constructor tail (Slideshow.cpp:33-61): mark in-slideshow and
    /// show the first slide (its sound is started by the client).</summary>
    public void Start()
    {
        IsInSlideShow = true;
        Index = 0;
        _postSoundTimer = -1.0;
    }

    /// <summary>The client reports the current slide's sound finished
    /// (<c>Slideshow::OnEvent</c> with a source, Slideshow.cpp:123-146): arm the 1 s
    /// timer. A slide whose sound clip is absent from the install never auto-advances —
    /// the player advances by key, as native does with a missing clip.</summary>
    public void OnSoundFinished()
    {
        if (!IsInSlideShow || Dismissed || _postSoundTimer >= 0.0) return;
        _postSoundTimer = PostSoundDelaySeconds;
    }

    /// <summary>Per-frame pump (the window timer path): run down the armed post-sound
    /// timer and advance when it expires.</summary>
    public void Tick(double deltaSeconds)
    {
        if (_postSoundTimer < 0.0) return;
        _postSoundTimer -= deltaSeconds;
        if (_postSoundTimer < 0.0)
            NextSlide();
    }

    /// <summary>Any key down or click advances immediately (Slideshow.cpp:147-150,
    /// 179-183): the timer is cancelled and the next slide shows.</summary>
    public void OnKey() => NextSlide();

    private void NextSlide()
    {
        // Slideshow::NextSlide (Slideshow.cpp:80-116): stop the sound, cancel the
        // timer; past the end → Dismiss.
        _postSoundTimer = -1.0;
        if (Index + 1 < _slides.Count)
        {
            Index++;
        }
        else
        {
            Dismissed = true;
            IsInSlideShow = false;   // ~Slideshow clears the flag (Slideshow.cpp:67)
        }
    }
}

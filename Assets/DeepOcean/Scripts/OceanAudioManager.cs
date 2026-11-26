using UnityEngine;

/// <summary>
/// Depth-based ocean sound for the submarine experience.
///
///   ABOVE THE SURFACE  -> "waves" loop
///   GOING UNDER        -> "submerge" plays once (splash / bubbles)
///   UNDERWATER         -> "underwater" ambience loop fades in after the splash
///                         (gets more muffled the deeper you go)
///   WHILE DESCENDING   -> "submarine sinking" (hull creaks) fades in while going down, fades out when you stop
///   EVERY SO OFTEN     -> "radar" and "sonar" pings at random intervals while underwater
///   DEEP ONLY          -> "sea alien" from the Bathypelagic zone (1,000 m+),
///                         "sea monster" from the Abyssopelagic zone (4,000 m+),
///                         at random times, more often the deeper you are, from a random left/right direction
///
/// All sounds are 2D (heard "inside the cockpit"), so they work the same in the NOVA ball.
/// Attach to an empty GameObject "Ocean_Audio_Manager", drag the clips into the slots, press Play.
/// The script creates its own AudioSources as child objects when the game starts.
/// </summary>
public class OceanAudioManager : MonoBehaviour
{
    [Header("Depth")]
    [Tooltip("The shared depth scale (Assets/DeepOcean/Settings/OceanScaleSettings).")]
    public OceanScaleSettings scaleSettings;
    [Tooltip("Whose position decides above/under water. Leave empty to use the Main Camera. Later: the submarine.")]
    public Transform depthSource;
    [Tooltip("Go below this many metres under the surface to count as 'submerged'.")]
    public float submergeDepth = 1.5f;
    [Tooltip("Come back above this height (metres ABOVE the surface) to count as 'surfaced'. The gap stops flickering on waves.")]
    public float surfaceHeight = 0.3f;

    [Header("Clips (drag from Assets/Sounds)")]
    public AudioClip waves;
    public AudioClip submerge;
    public AudioClip underwater;
    public AudioClip submarineSinking;
    public AudioClip radar;
    public AudioClip sonar;
    public AudioClip seaAlien;
    public AudioClip seaMonster;

    [Header("Volumes")]
    [Range(0f, 1f)] public float wavesVolume = 0.8f;
    [Range(0f, 1f)] public float submergeVolume = 1f;
    [Range(0f, 1f)] public float underwaterVolume = 0.7f;
    [Range(0f, 1f)] public float sinkingVolume = 0.6f;
    [Range(0f, 1f)] public float radarVolume = 0.35f;
    [Range(0f, 1f)] public float sonarVolume = 0.5f;
    [Range(0f, 1f)] public float creatureVolume = 0.8f;
    [Tooltip("Seconds to fade loops in/out.")]
    public float fadeTime = 2f;

    [Header("Underwater ambience")]
    [Tooltip("Seconds after the splash before the underwater ambience starts fading in.")]
    public float underwaterDelay = 2.5f;
    [Tooltip("Muffling: low-pass cutoff (Hz) at the surface and at the trench floor.")]
    public float cutoffAtSurface = 8000f;
    public float cutoffAtBottom = 1200f;
    [Tooltip("Pitch at the trench floor (1 = normal). A slightly lower pitch feels heavier and deeper.")]
    [Range(0.5f, 1f)] public float pitchAtBottom = 0.85f;

    [Header("Submarine sinking (hull sounds while descending)")]
    [Tooltip("Descent speed (metres per second) at which the sinking sound is at full volume.")]
    public float fullSinkingSpeed = 3f;
    [Tooltip("Below this descent speed (m/s) the sinking sound is silent.")]
    public float minSinkingSpeed = 0.3f;

    [Header("Radar and sonar (random intervals, seconds)")]
    public Vector2 radarInterval = new Vector2(20f, 45f);
    public Vector2 sonarInterval = new Vector2(60f, 120f);

    [Header("Deep-sea creatures")]
    [Tooltip("'Sea alien' can be heard below this depth (metres). 1000 = Bathypelagic.")]
    public float seaAlienMinDepth = 1000f;
    [Tooltip("'Sea monster' can be heard below this depth (metres). 4000 = Abyssopelagic.")]
    public float seaMonsterMinDepth = 4000f;
    [Tooltip("Seconds between creature sounds when just past the minimum depth...")]
    public Vector2 creatureIntervalShallow = new Vector2(60f, 120f);
    [Tooltip("...and at the trench floor (more frequent the deeper you are).")]
    public Vector2 creatureIntervalDeep = new Vector2(20f, 45f);

    [Header("Read-only (for checking)")]
    [SerializeField] bool isSubmerged;
    [SerializeField] float depthMetres;
    [SerializeField] float descentSpeed;

    // Audio sources created at start
    AudioSource wavesSource, underwaterSource, sinkingSource, oneShotSource, creatureSource;
    AudioLowPassFilter underwaterFilter;

    float underwaterTarget, sinkingTarget, wavesTarget;
    float submergedTime;
    float lastDepth;
    float nextRadarTime, nextSonarTime, nextAlienTime, nextMonsterTime;

    // ---------------------------------------------------------------------------------
    void Start()
    {
        wavesSource      = CreateSource("Audio_Waves", waves, loop: true);
        underwaterSource = CreateSource("Audio_Underwater", underwater, loop: true);
        sinkingSource    = CreateSource("Audio_SubmarineSinking", submarineSinking, loop: true);
        oneShotSource    = CreateSource("Audio_OneShots", null, loop: false);
        creatureSource   = CreateSource("Audio_Creatures", null, loop: false);
        underwaterFilter = underwaterSource.gameObject.AddComponent<AudioLowPassFilter>();
        underwaterFilter.cutoffFrequency = cutoffAtSurface;

        // Start in the correct state without playing the splash.
        float y = GetSourcePosition().y;
        isSubmerged = scaleSettings != null && y < scaleSettings.surfaceY - submergeDepth;
        submergedTime = isSubmerged ? -999f : 0f;
        lastDepth = CurrentDepth(y);

        wavesSource.volume = isSubmerged ? 0f : wavesVolume;
        underwaterSource.volume = isSubmerged ? underwaterVolume : 0f;
        sinkingSource.volume = 0f;
        StartLoop(wavesSource);
        StartLoop(underwaterSource);
        StartLoop(sinkingSource);

        ScheduleAll();
    }

    AudioSource CreateSource(string name, AudioClip clip, bool loop)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var source = go.AddComponent<AudioSource>();
        source.clip = clip;
        source.loop = loop;
        source.playOnAwake = false;
        source.spatialBlend = 0f;   // 2D: heard inside the cockpit
        source.volume = 0f;
        return source;
    }

    static void StartLoop(AudioSource source)
    {
        if (source.clip != null && !source.isPlaying) source.Play();
    }

    // ---------------------------------------------------------------------------------
    void Update()
    {
        if (scaleSettings == null) return;

        float y = GetSourcePosition().y;
        depthMetres = CurrentDepth(y);

        // Vertical speed (positive = going down), smoothed so it doesn't jitter.
        float instantSpeed = Time.deltaTime > 0f ? (depthMetres - lastDepth) / Time.deltaTime : 0f;
        descentSpeed = Mathf.Lerp(descentSpeed, instantSpeed, 1f - Mathf.Exp(-4f * Time.deltaTime));
        lastDepth = depthMetres;

        UpdateSubmergedState(y);
        UpdateLoops();
        UpdateRandomSounds();
    }

    float CurrentDepth(float y) => scaleSettings != null ? scaleSettings.WorldYToDepthMetres(y) : 0f;

    Vector3 GetSourcePosition()
    {
        if (depthSource != null) return depthSource.position;
        if (Camera.main != null) return Camera.main.transform.position;
        return transform.position;
    }

    void UpdateSubmergedState(float y)
    {
        float surfaceY = scaleSettings.surfaceY;

        if (!isSubmerged && y < surfaceY - submergeDepth)
        {
            // Just went under the water.
            isSubmerged = true;
            submergedTime = Time.time;
            if (submerge != null) oneShotSource.PlayOneShot(submerge, submergeVolume);
            ScheduleAll();
        }
        else if (isSubmerged && y > surfaceY + surfaceHeight)
        {
            // Back above the water.
            isSubmerged = false;
        }
    }

    void UpdateLoops()
    {
        // Targets
        wavesTarget = isSubmerged ? 0f : wavesVolume;
        bool ambienceReady = isSubmerged && Time.time - submergedTime >= underwaterDelay;
        underwaterTarget = ambienceReady ? underwaterVolume : 0f;

        float sinkAmount = Mathf.InverseLerp(minSinkingSpeed, fullSinkingSpeed, descentSpeed);
        sinkingTarget = isSubmerged ? sinkingVolume * sinkAmount : 0f;

        // Fade towards targets
        float step = Time.deltaTime / Mathf.Max(0.01f, fadeTime);
        wavesSource.volume      = Mathf.MoveTowards(wavesSource.volume, wavesTarget, step);
        underwaterSource.volume = Mathf.MoveTowards(underwaterSource.volume, underwaterTarget, step);
        sinkingSource.volume    = Mathf.MoveTowards(sinkingSource.volume, sinkingTarget, step);

        // Deeper = more muffled and slightly lower.
        float depth01 = scaleSettings.maxDepthMetres > 0f ? Mathf.Clamp01(depthMetres / scaleSettings.maxDepthMetres) : 0f;
        float curve = Mathf.Sqrt(depth01);   // changes faster near the top, slower at the bottom
        underwaterFilter.cutoffFrequency = Mathf.Lerp(cutoffAtSurface, cutoffAtBottom, curve);
        underwaterSource.pitch = Mathf.Lerp(1f, pitchAtBottom, curve);
    }

    void UpdateRandomSounds()
    {
        if (!isSubmerged) return;
        float now = Time.time;

        if (radar != null && now >= nextRadarTime)
        {
            oneShotSource.PlayOneShot(radar, radarVolume);
            nextRadarTime = now + Random.Range(radarInterval.x, radarInterval.y);
        }

        if (sonar != null && now >= nextSonarTime)
        {
            oneShotSource.PlayOneShot(sonar, sonarVolume);
            nextSonarTime = now + Random.Range(sonarInterval.x, sonarInterval.y);
        }

        if (seaAlien != null && depthMetres >= seaAlienMinDepth && now >= nextAlienTime)
        {
            PlayCreature(seaAlien);
            nextAlienTime = now + CreatureDelay(seaAlienMinDepth);
        }

        if (seaMonster != null && depthMetres >= seaMonsterMinDepth && now >= nextMonsterTime)
        {
            PlayCreature(seaMonster);
            nextMonsterTime = now + CreatureDelay(seaMonsterMinDepth);
        }
    }

    /// <summary>Plays a creature sound from a random left/right direction at a random distance (volume).</summary>
    void PlayCreature(AudioClip clip)
    {
        if (creatureSource.isPlaying) return;   // never two creatures on top of each other
        creatureSource.clip = clip;
        creatureSource.panStereo = Random.Range(-0.8f, 0.8f);           // from the left or the right
        creatureSource.pitch = Random.Range(0.85f, 1.05f);              // each call sounds a little different
        creatureSource.volume = creatureVolume * Random.Range(0.5f, 1f); // near or far away
        creatureSource.Play();
    }

    /// <summary>The deeper below the creature's minimum depth, the shorter the wait.</summary>
    float CreatureDelay(float minDepth)
    {
        float deep01 = Mathf.InverseLerp(minDepth, scaleSettings.maxDepthMetres, depthMetres);
        float min = Mathf.Lerp(creatureIntervalShallow.x, creatureIntervalDeep.x, deep01);
        float max = Mathf.Lerp(creatureIntervalShallow.y, creatureIntervalDeep.y, deep01);
        return Random.Range(min, max);
    }

    /// <summary>Sets the first time each random sound may play (not all at once).</summary>
    void ScheduleAll()
    {
        float now = Time.time;
        nextRadarTime   = now + Random.Range(8f, radarInterval.x);
        nextSonarTime   = now + Random.Range(15f, sonarInterval.x);
        nextAlienTime   = now + Random.Range(10f, creatureIntervalShallow.x);
        nextMonsterTime = now + Random.Range(10f, creatureIntervalShallow.x);
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TMPro;

/// <summary>
/// One-script mini-game:
/// - Four prompt planes (RightHandUp, LeftHandUp, Squat, Clap), each with a TMP child for its word.
/// - Each round picks 4 words, assigns 1 per plane, randomly chooses which plane/gesture is the correct target.
/// - Player performs gestures:
///     Right/Left: raise hand above head by threshold.
///     Squat: head goes below baseline by threshold, then return to neutral before next round.
///     Clap: hands close together AND their green axes (Transform.up) pointing away (opposed) enough.
/// - Wrong gesture: flash thumbs-down, hide that plane until next round.
/// - Correct gesture: flash thumbs-up, play right SFX, start await neutral, then new round.
/// - Each round: smoothly shuffle plane positions, flash a hint image (Resources) on a hint plane,
///   play matching word audio (Resources) immediately and every 7s until round ends.
/// </summary>
public class GestureWordGameController : MonoBehaviour
{
    [Header("Body Inputs")]
    public Transform leftHand;
    public Transform rightHand;
    public Transform head;

    [Header("Prompt Planes (roots)")]
    public Transform RightHandUp;
    public Transform LeftHandUp;
    public Transform Squat;
    public Transform Clap;

    [Header("Feedback")]
    public GameObject thumbsUp;
    public GameObject thumbsDown;
    [Tooltip("Seconds to flash feedback objects.")]
    public float feedbackFlashSeconds = 0.25f;

    [Header("Audio")]
    public AudioSource sfx;                 // any AudioSource to play one-shots
    public AudioClip correctSfx;
    public AudioClip wrongSfx;

    [Header("Hints (Resources)")]
    [Tooltip("Folders under Assets/Resources to search for hint images (Texture2D).")]
    public string[] hintImageFolders;
    [Tooltip("Folders under Assets/Resources to search for word audio (AudioClip).")]
    public string[] hintAudioFolders;
    [Tooltip("Renderer on the plane used to flash hint images.")]
    public Renderer hintPlaneRenderer;
    public float hintFlashSeconds = 0.75f;
    public float hintRepeatSeconds = 7f;

    [Header("Board Anchor")]
    [Tooltip("Root transform that should stay in front of the player. Defaults to this object.")]
    [SerializeField] private Transform boardRoot;
    [Tooltip("Recenter when distance from desired anchor exceeds this many meters.")]
    [SerializeField] private float anchorRecenterDistance = 0.2f;
    [Tooltip("Seconds used by SmoothDamp for anchor reposition.")]
    [SerializeField] private float anchorPositionSmoothTime = 0.25f;
    [Tooltip("Speed factor for anchor rotation easing.")]
    [SerializeField] private float anchorRotationLerpSpeed = 8f;
    [Tooltip("Cooldown between anchor recenter operations.")]
    [SerializeField] private float anchorRecenterCooldown = 0.75f;
    [Tooltip("Stop moving anchor when closer than this many meters to the target.")]
    [SerializeField] private float anchorStopDistance = 0.01f;
    [Tooltip("Stop rotating anchor when within this many degrees of the target.")]
    [SerializeField] private float anchorStopAngle = 1f;

    [Header("Anchor LookAway Gating")]
    [Tooltip("Dot(head fwd, head→anchor) to ENTER 'away'. <=0 ~ ≈90° off.")]
    [SerializeField, Range(-1f, 1f)] private float lookAwayDotEnter = 0f;
    [Tooltip("Dot(head fwd, head→anchor) to EXIT 'away'. >0 adds hysteresis (e.g., ~0.25 ≈ 75°).")]
    [SerializeField, Range(-1f, 1f)] private float lookAwayDotExit = 0.25f;
    [Tooltip("Must be looking away for at least this long before recentering.")]
    [SerializeField] private float lookAwayDwellSeconds = 0.25f;
    [Tooltip("Hard fallback: recenter if drift exceeds this many meters even without look-away.")]
    [SerializeField] private float hardRecenterDistance = 1.0f;

    [Header("Hand Visuals")]
    [SerializeField] private Color rightHandColor = Color.red;
    [SerializeField] private Color leftHandColor = Color.blue;

    [Header("Gesture Thresholds (tweak in play mode)")]
    [Tooltip("Meters hand must be above head.y for Right/Left gesture.")]
    public float raiseAboveHead = 0.15f;
    [Tooltip("Meters head must drop below baseline for Squat.")]
    public float squatDelta = 0.15f;
    [Tooltip("Meters head must be within baseline to count as neutral.")]
    public float neutralHeadTolerance = 0.05f;
    [Tooltip("Hands considered 'down' when this far below head.y (+ value).")]
    public float handDownBelowHead = 0.05f;
    [Tooltip("Max distance between hands for Clap.")]
    public float clapMaxDistance = 0.25f;
    [Tooltip("Hands up-vectors must oppose at least this dot (<= value, e.g., -0.3 ~ 107°).")]
    [Range(-1f, 1f)] public float clapOpposeDotMax = -0.3f;
    [Tooltip("After a clap, require hands to separate beyond this to reset.")]
    public float clapResetDistance = 0.35f;

    [Header("Round Flow")]
    [Tooltip("Seconds when shuffling panel positions each round.")]
    public float shuffleSeconds = 0.5f;

    // ---- internals ----
    enum Gesture { RightHandUp, LeftHandUp, Squat, Clap }

    class PlaneSlot
    {
        public Transform root;
        public TMP_Text label;
        public Vector3 homeLocalPos;
        public bool hiddenThisRound;
    }

    readonly Dictionary<Gesture, PlaneSlot> _planes = new();
    readonly System.Random _rng = new System.Random();

    float _baselineHeadY;
    bool _roundActive;
    bool _awaitNeutral;
    bool _squelchIncorrectUntilNeutral;
    bool _pendingBaselineAfterAudio;
    bool _handColorsApplied;

    Gesture _activeGesture;
    string _activeWord;
    float _nextHintAt;

    List<string> _wordPool;

    List<Texture2D> _hintTextures = new();
    List<AudioClip> _wordClips = new();
    readonly HashSet<string> _usedAsTarget = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _lastRoundWordSet = new(StringComparer.OrdinalIgnoreCase);

    Transform Anchor => boardRoot ? boardRoot : transform;
    bool _anchorOffsetsCaptured;
    Vector3 _anchorLocalOffset;
    Quaternion _anchorLocalRotation;
    Vector3 _anchorVelocity;
    Vector3 _anchorTargetPos;
    Quaternion _anchorTargetRot;
    bool _anchorRepositioning;
    float _lastAnchorRecenterAt;
    float _lookAwayAccum;
    bool _anchorLookingAway;

    static Quaternion FlattenToWorldUp(Quaternion rotation)
    {
        Vector3 forward = rotation * Vector3.forward;
        Vector3 flattenedForward = Vector3.ProjectOnPlane(forward, Vector3.up);
        if (flattenedForward.sqrMagnitude <= 1e-6f)
        {
            Vector3 right = rotation * Vector3.right;
            flattenedForward = Vector3.ProjectOnPlane(right, Vector3.up);
            if (flattenedForward.sqrMagnitude <= 1e-6f)
            {
                flattenedForward = Vector3.forward;
            }
        }
        flattenedForward.Normalize();
        return Quaternion.LookRotation(flattenedForward, Vector3.up);
    }

    void Awake()
    {
        // Map planes + pull TMP
        RegisterPlane(Gesture.RightHandUp, RightHandUp);
        RegisterPlane(Gesture.LeftHandUp,  LeftHandUp);
        RegisterPlane(Gesture.Squat,       Squat);
        RegisterPlane(Gesture.Clap,        Clap);

        // Baseline head
        _baselineHeadY = head ? head.position.y : 0f;

        // Hide feedback
        if (thumbsUp)   thumbsUp.SetActive(false);
        if (thumbsDown) thumbsDown.SetActive(false);

        // Words (from your list, preserving case)
        _wordPool = new List<string>(new[]{
            "rat","cat","pan","bag","mat","Hat","sad","bad","tap","dad","pad","yam","fax",
            "if","big","lip","rip","dig","hit","Him","six","Fix","Kid",
            "Up","Cut","Run","Bug","Dug","Cub","Tub","Fun","Mud","Sun",
            "On","Hot","Box","Lot","Dog","Pot","Fox","Mop","Mom","Hop","Job","Jog","Top",
            "Get","Red","Bed","Yes","Men","Wet","Pen","Ten","Jet","Pet","Leg","Met","Fed","Let","Yet"
        });

        // Load hint textures
        if (hintImageFolders != null)
        {
            foreach (var f in hintImageFolders.Where(s => !string.IsNullOrWhiteSpace(s)))
                _hintTextures.AddRange(Resources.LoadAll<Texture2D>(f));
        }

        // Load audio clips
        if (hintAudioFolders != null)
        {
            foreach (var f in hintAudioFolders.Where(s => !string.IsNullOrWhiteSpace(s)))
                _wordClips.AddRange(Resources.LoadAll<AudioClip>(f));
        }
    }

    void Start()
    {
        CaptureAnchorOffsets();
        ApplyHandColors();
        StartCoroutine(StartRoundRoutine());
    }

    void OnValidate()
    {
        // keep sane hysteresis
        lookAwayDotEnter = Mathf.Clamp(lookAwayDotEnter, -1f, 1f);
        lookAwayDotExit  = Mathf.Clamp(lookAwayDotExit,  -1f, 1f);
        if (lookAwayDotExit <= lookAwayDotEnter)
            lookAwayDotExit = Mathf.Min(1f, lookAwayDotEnter + 0.05f);
    }

    void ApplyHandColors()
    {
        bool appliedAny = false;
        appliedAny |= ApplyColorToHand(rightHand, rightHandColor);
        appliedAny |= ApplyColorToHand(leftHand, leftHandColor);
        _handColorsApplied = appliedAny;
    }

    bool ApplyColorToHand(Transform handRoot, Color color)
    {
        if (!handRoot) return false;
        bool applied = false;
        var renderers = handRoot.GetComponentsInChildren<Renderer>(true);
        foreach (var renderer in renderers)
        {
            if (!renderer) continue;
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            block.SetColor("_Color", color);
            block.SetColor("_BaseColor", color);
            renderer.SetPropertyBlock(block);
            applied = true;
        }

        var texts = handRoot.GetComponentsInChildren<TMP_Text>(true);
        foreach (var text in texts)
        {
            if (!text) continue;
            text.color = color;
            applied = true;
        }

        return applied;
    }

    void Update()
    {
        if (!_handColorsApplied)
        {
            ApplyHandColors();
        }

        if (_squelchIncorrectUntilNeutral && IsNeutral())
        {
            _squelchIncorrectUntilNeutral = false;
        }

        UpdateAnchorTracking();

        if (_awaitNeutral)
        {
            if (IsNeutral())
            {
                _awaitNeutral = false;
                StartCoroutine(StartRoundRoutine(true));
            }
            return;
        }

        if (!_roundActive) return;

        // Repeat hint every N seconds during round
        if (Time.time >= _nextHintAt)
        {
            StartCoroutine(PlayHintBurst());
            _nextHintAt = Time.time + Mathf.Max(1f, hintRepeatSeconds);
        }

        // Detect gestures
        var triggered = GetTriggeredGesture();
        if (triggered == null) return;

        if (triggered.Value == _activeGesture)
        {
            // Correct
            if (sfx && correctSfx) sfx.PlayOneShot(correctSfx);
            if (thumbsUp) StartCoroutine(FlashFor(thumbsUp, feedbackFlashSeconds));
            _squelchIncorrectUntilNeutral = false;

            // end round; wait neutral before starting next
            _roundActive = false;
            CancelInvoke(); // any scheduled invokes we might add later
            _awaitNeutral = true;
        }
        else
        {
            // Wrong: flash & hide the picked plane until next round
            if (!_squelchIncorrectUntilNeutral)
            {
                if (sfx && wrongSfx) sfx.PlayOneShot(wrongSfx);
                if (thumbsDown) StartCoroutine(FlashFor(thumbsDown, feedbackFlashSeconds));
                _squelchIncorrectUntilNeutral = true;
            }

            var wrongPlane = _planes[triggered.Value];
            if (!wrongPlane.hiddenThisRound)
            {
                wrongPlane.hiddenThisRound = true;
                wrongPlane.root.gameObject.SetActive(false);
            }
        }
    }

    // ---------- Core Round ----------
    IEnumerator StartRoundRoutine(bool fromNeutral = false)
    {
        _squelchIncorrectUntilNeutral = false;
        _pendingBaselineAfterAudio = true;

        // Restore all planes
        foreach (var p in _planes.Values)
        {
            p.hiddenThisRound = false;
            p.root.gameObject.SetActive(true);
        }

        if (thumbsUp) thumbsUp.SetActive(false);

        // Assign fresh words with random plane mapping
        var gestureOrder = new[] { Gesture.RightHandUp, Gesture.LeftHandUp, Gesture.Squat, Gesture.Clap };
        var shuffledGestures = gestureOrder.OrderBy(_ => _rng.Next()).ToList();

        string targetWord = PickTargetWord();
        var fillerWords = PickFillerWords(targetWord, shuffledGestures.Count - 1);

        int targetIndex = _rng.Next(shuffledGestures.Count);
        _activeGesture = shuffledGestures[targetIndex];
        _activeWord = targetWord;
        if (!string.IsNullOrEmpty(_activeWord)) _usedAsTarget.Add(_activeWord);

        int fillerIdx = 0;
        for (int i = 0; i < shuffledGestures.Count; i++)
        {
            string wordForSlot = (i == targetIndex)
                ? targetWord
                : (fillerIdx < fillerWords.Count ? fillerWords[fillerIdx++] : string.Empty);

            var slot = _planes[shuffledGestures[i]];
            if (slot.label) slot.label.text = wordForSlot;
        }

        _lastRoundWordSet.Clear();
        if (!string.IsNullOrWhiteSpace(targetWord)) _lastRoundWordSet.Add(targetWord);
        foreach (var w in fillerWords)
        {
            if (!string.IsNullOrWhiteSpace(w)) _lastRoundWordSet.Add(w);
        }

        // Smooth shuffle plane positions
        yield return SmoothShufflePlanes();

        // hint now + schedule repeats
        _nextHintAt = 0f; // fire immediately in Update
        _roundActive = true;
    }

    IEnumerator SmoothShufflePlanes()
    {
        // assign each plane a new target = some other plane's local home position (random permutation)
        var planeList = _planes.Values.ToList();
        var targets = planeList.Select(p => p.homeLocalPos).ToList();
        targets = targets.OrderBy(_ => _rng.Next()).ToList();

        var startPositions = planeList.Select(p => p.root.localPosition).ToArray();
        var arcOffsets = new Vector3[planeList.Count];
        for (int i = 0; i < planeList.Count; i++)
        {
            float height = Mathf.Lerp(0.15f, 0.3f, (float)_rng.NextDouble());
            float lateral = (float)(_rng.NextDouble() * 0.2f - 0.1f);
            var moveDir = (targets[i] - startPositions[i]).normalized;
            var sideways = Vector3.Cross(Vector3.up, moveDir);
            if (sideways.sqrMagnitude < 0.001f) sideways = Vector3.right;
            arcOffsets[i] = Vector3.up * height + sideways.normalized * lateral;
        }

        float t = 0f;
        while (t < shuffleSeconds)
        {
            t += Time.deltaTime;
            var normalized = Mathf.Clamp01(t / shuffleSeconds);
            var eased = normalized * normalized * (3f - 2f * normalized); // smoothstep easing
            for (int i = 0; i < planeList.Count; i++)
            {
                var p0 = startPositions[i];
                var p2 = targets[i];
                var p1 = Vector3.Lerp(p0, p2, 0.5f) + arcOffsets[i];
                planeList[i].root.localPosition = BezierPoint(p0, p1, p2, eased);
            }
            yield return null;
        }
        for (int i = 0; i < planeList.Count; i++) planeList[i].root.localPosition = targets[i];
    }

    IEnumerator PlayHintBurst()
    {
        if (hintPlaneRenderer)
        {
            var tex = FindHintTexture(_activeWord);
            if (tex)
            {
                hintPlaneRenderer.material.mainTexture = tex;
                hintPlaneRenderer.gameObject.SetActive(true);
            }
        }

        var clip = FindHintClip(_activeWord);
        if (sfx && clip) sfx.PlayOneShot(clip);

        MaybeCaptureBaselineAfterAudio();

        yield break;
    }

    // ---------- Detection ----------
    Gesture? GetTriggeredGesture()
    {
        // Order matters a bit to avoid double triggers; clap tends to be more specific.
        if (IsRightUp())  return Gesture.RightHandUp;
        if (IsLeftUp())   return Gesture.LeftHandUp;
        if (IsSquat())    return Gesture.Squat;
        if (IsClap())     return Gesture.Clap;
        return null;
    }

    bool IsRightUp() => rightHand && head && (rightHand.position.y > head.position.y + raiseAboveHead);
    bool IsLeftUp()  => leftHand  && head && (leftHand.position.y  > head.position.y + raiseAboveHead);

    bool IsSquat()
    {
        if (!head) return false;
        return head.position.y < _baselineHeadY - Mathf.Abs(squatDelta);
    }

    bool IsClap()
    {
        if (!leftHand || !rightHand) return false;
        float dist = Vector3.Distance(leftHand.position, rightHand.position);
        float dot = Vector3.Dot(leftHand.up.normalized, rightHand.up.normalized);
        bool close = dist <= clapMaxDistance;
        bool opposed = dot <= clapOpposeDotMax; // green (Y) arrows pointing away enough
        return close && opposed;
    }

    bool IsNeutral()
    {
        if (!head || !leftHand || !rightHand) return true;

        // Head near baseline
        bool headOk = Mathf.Abs(head.position.y - _baselineHeadY) <= neutralHeadTolerance;
        // Hands down (below head a bit)
        bool handsDown = (leftHand.position.y  <= head.position.y + handDownBelowHead) &&
                         (rightHand.position.y <= head.position.y + handDownBelowHead);
        // Not clapping
        bool clapReset = Vector3.Distance(leftHand.position, rightHand.position) >= clapResetDistance
                         || Vector3.Dot(leftHand.up, rightHand.up) > clapOpposeDotMax;

        return headOk && handsDown && clapReset;
    }

    void MaybeCaptureBaselineAfterAudio()
    {
        if (!_pendingBaselineAfterAudio || !head) return;
        _baselineHeadY = head.position.y;
        _pendingBaselineAfterAudio = false;
    }

    // ---------- Utils ----------
    void CaptureAnchorOffsets()
    {
        var anchor = Anchor;
        if (!anchor || !head) return;

        var headYaw = FlattenToWorldUp(head.rotation);
        var anchorYaw = FlattenToWorldUp(anchor.rotation);

        _anchorLocalOffset = Quaternion.Inverse(headYaw) * (anchor.position - head.position);
        _anchorLocalOffset.y = 0f;
        _anchorLocalRotation = (Quaternion.Inverse(headYaw) * anchorYaw).normalized;
        _anchorTargetPos = head.position + headYaw * _anchorLocalOffset;
        _anchorTargetRot = headYaw * _anchorLocalRotation;

        anchor.position = _anchorTargetPos;
        anchor.rotation = _anchorTargetRot;
        _anchorOffsetsCaptured = true;
        _anchorRepositioning = false;
        _anchorVelocity = Vector3.zero;
        _lastAnchorRecenterAt = Time.time;
        _lookAwayAccum = 0f;
        _anchorLookingAway = false;
    }

    void UpdateAnchorTracking()
    {
        var anchor = Anchor;
        if (!anchor || !head) return;

        if (!_anchorOffsetsCaptured)
        {
            CaptureAnchorOffsets();
            return;
        }

        Quaternion headYaw = FlattenToWorldUp(head.rotation);

        Vector3 desiredPos = head.position + headYaw * _anchorLocalOffset;
        Quaternion desiredRot = (headYaw * _anchorLocalRotation).normalized;

        float distanceDeltaSqr = (anchor.position - desiredPos).sqrMagnitude;

        // ---- FIX: gate by where the head is looking relative to the anchor's position (XZ only) ----
        Vector3 headFwd = headYaw * Vector3.forward; // already flattened
        headFwd.y = 0f;
        if (headFwd.sqrMagnitude <= 1e-6f) headFwd = Vector3.forward;
        headFwd.Normalize();

        Vector3 toAnchor = anchor.position - head.position;
        toAnchor.y = 0f;
        if (toAnchor.sqrMagnitude <= 1e-6f) toAnchor = headFwd;
        toAnchor.Normalize();

        float facingDot = Vector3.Dot(headFwd, toAnchor); // 1=looking at anchor, 0≈90°, <0=away

        if (_anchorLookingAway)
        {
            if (facingDot > lookAwayDotExit)
            {
                _anchorLookingAway = false;
                _lookAwayAccum = 0f;
            }
        }
        else
        {
            if (facingDot <= lookAwayDotEnter)
            {
                _anchorLookingAway = true;
            }
        }

        if (_anchorLookingAway)
        {
            _lookAwayAccum += Time.deltaTime;
        }
        else
        {
            _lookAwayAccum = 0f;
        }

        bool lookAwayDwelled = _anchorLookingAway && (_lookAwayAccum >= lookAwayDwellSeconds);

        bool softDistanceExceeded = distanceDeltaSqr >= (anchorRecenterDistance * anchorRecenterDistance);
        bool hardDistanceExceeded = distanceDeltaSqr >= (hardRecenterDistance * hardRecenterDistance);

        if (((softDistanceExceeded && lookAwayDwelled) || hardDistanceExceeded) &&
            Time.time - _lastAnchorRecenterAt >= anchorRecenterCooldown)
        {
            SetAnchorTarget(desiredPos, desiredRot);
            if (lookAwayDwelled)
            {
                _lookAwayAccum = 0f;
                _anchorLookingAway = false;
            }
        }

        if (_anchorRepositioning)
        {
            anchor.position = Vector3.SmoothDamp(
                anchor.position,
                _anchorTargetPos,
                ref _anchorVelocity,
                anchorPositionSmoothTime);

            anchor.rotation = Quaternion.Slerp(
                anchor.rotation,
                _anchorTargetRot,
                anchorRotationLerpSpeed * Time.deltaTime);

            bool posSettled = (anchor.position - _anchorTargetPos).sqrMagnitude <= (anchorStopDistance * anchorStopDistance);
            bool rotSettled = Quaternion.Angle(anchor.rotation, _anchorTargetRot) <= anchorStopAngle;

            if (posSettled && rotSettled)
            {
                anchor.position = _anchorTargetPos;
                anchor.rotation = _anchorTargetRot;
                _anchorRepositioning = false;
            }
        }
    }

    void SetAnchorTarget(Vector3 position, Quaternion rotation)
    {
        _anchorTargetPos = position;
        _anchorTargetRot = FlattenToWorldUp(rotation);
        _anchorVelocity = Vector3.zero;
        _anchorRepositioning = true;
        _lastAnchorRecenterAt = Time.time;
        _lookAwayAccum = 0f;

        var anchor = Anchor;
        if (anchor)
        {
            anchor.rotation = FlattenToWorldUp(anchor.rotation);
        }
    }

    void RegisterPlane(Gesture g, Transform t)
    {
        var slot = new PlaneSlot
        {
            root = t,
            label = t ? t.GetComponentInChildren<TMP_Text>(true) : null,
            homeLocalPos = t ? t.localPosition : Vector3.zero,
            hiddenThisRound = false
        };
        _planes[g] = slot;
    }

    IEnumerable<string> SampleDistinct(List<string> source, int count)
    {
        // simple partial Fisher-Yates
        int n = source.Count;
        var indices = Enumerable.Range(0, n).ToArray();
        for (int i = 0; i < count && i < n; i++)
        {
            int j = _rng.Next(i, n);
            (indices[i], indices[j]) = (indices[j], indices[i]);
            yield return source[indices[i]];
        }
    }

    string PickTargetWord()
    {
        var candidates = _wordPool.Where(w => !_usedAsTarget.Contains(w) && !_lastRoundWordSet.Contains(w)).ToList();
        if (candidates.Count == 0)
        {
            candidates = _wordPool.Where(w => !_usedAsTarget.Contains(w)).ToList();
        }
        if (candidates.Count == 0)
        {
            _usedAsTarget.Clear();
            candidates = _wordPool.Where(w => !_lastRoundWordSet.Contains(w)).ToList();
            if (candidates.Count == 0)
            {
                candidates = new List<string>(_wordPool);
            }
        }
        if (candidates.Count == 0) return string.Empty;
        return candidates[_rng.Next(candidates.Count)];
    }

    List<string> PickFillerWords(string targetWord, int count)
    {
        var exclude = new HashSet<string>(_lastRoundWordSet, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(targetWord)) exclude.Add(targetWord);

        var pool = _wordPool.Where(w => !exclude.Contains(w)).ToList();
        if (pool.Count < count)
        {
            pool = _wordPool.Where(w => !string.Equals(w, targetWord, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var selection = SampleDistinct(pool, count).ToList();
        while (selection.Count < count && pool.Count > 0)
        {
            selection.Add(pool[_rng.Next(pool.Count)]);
        }

        return selection;
    }

    static Vector3 BezierPoint(Vector3 p0, Vector3 p1, Vector3 p2, float t)
    {
        float u = 1f - t;
        return (u * u * p0) + (2f * u * t * p1) + (t * t * p2);
    }

    Texture2D FindHintTexture(string word)
    {
        if (_hintTextures.Count == 0) return null;
        string w = word.ToLowerInvariant();
        var matches = _hintTextures.Where(t => t && t.name.ToLowerInvariant().Contains(w)).ToList();
        if (matches.Count == 0) return null;
        return matches[_rng.Next(matches.Count)];
    }

    AudioClip FindHintClip(string word)
    {
        if (_wordClips.Count == 0) return null;
        string w = word.ToLowerInvariant();
        var matches = _wordClips.Where(c => c && c.name.ToLowerInvariant().Contains(w)).ToList();
        if (matches.Count == 0) return null;
        return matches[_rng.Next(matches.Count)];
    }

    IEnumerator FlashFor(GameObject go, float secs)
    {
        if (!go) yield break;
        go.SetActive(true);
        yield return new WaitForSeconds(secs);
        go.SetActive(false);
    }
}

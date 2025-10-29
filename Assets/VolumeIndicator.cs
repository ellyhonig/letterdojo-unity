using UnityEngine;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Lights the child cubes of this GameObject to show mic volume:
/// ● While PhonemeManager is WaitingToRecord → cubes idle GREEN, but the ones
///   past the volume rank flash RED.
/// ● While Recording                        → same lighting logic, but
///   background is GREY instead of GREEN.
/// ● Any other state                        → all GREY.
///
/// Rank-logic:
///   • All children are grouped by unique local-Y (±0.001f tolerance)
///   • Lowest group = rank 0 (bottom bar), next = 1, …
///   • levelsLit = ceil( volume / thresh * ranks )
/// </summary>
public class VolumeIndicator : MonoBehaviour
{
    [Header("Links")]
    [Tooltip("Drag the PhonemeManager in the scene (named \"script\")")]
    [SerializeField] private PhonemeManager pm;

    [Header("Colours")]
    [SerializeField] private Color idleColour    = Color.gray;
    [SerializeField] private Color waitingColour = Color.red;
    [SerializeField] private Color activeColour  = Color.green;

    // --- internal ---
    class Cube { public Renderer rend; public int rank; }
    private List<Cube> cubes = new List<Cube>();
    private int rankCount;
    private const float tol = 0.001f;

    void Awake()
    {
        if (!pm) pm = FindObjectOfType<PhonemeManager>();
        if (!pm)
        {
            Debug.LogError("VolumeIndicator: No PhonemeManager found!");
            enabled = false;
            return;
        }
        CacheCubes();
    }

    void CacheCubes()
    {
        var childTFs = GetComponentsInChildren<Transform>()
                       .Where(t => t != transform)
                       .ToArray();

        // gather unique heights
        var heights = new List<float>();
        foreach (var tf in childTFs)
        {
            float y = tf.localPosition.y;
            if (!heights.Any(h => Mathf.Abs(h - y) < tol))
                heights.Add(y);
        }
        heights.Sort();
        rankCount = heights.Count;

        cubes.Clear();
        foreach (var tf in childTFs)
        {
            float y = tf.localPosition.y;
            int rank = heights.FindIndex(h => Mathf.Abs(h - y) < tol);
            var rend = tf.GetComponent<Renderer>();
            if (rend != null)
                cubes.Add(new Cube { rend = rend, rank = rank });
        }
    }

    void Update()
    {
        if (pm == null) return;

        switch (pm.currentState)
        {
            case PhonemeManager.PhonemeCheckState.WaitingToRecord:
                DrawBars(waitingColour);
                break;

            case PhonemeManager.PhonemeCheckState.Recording:
                DrawBars(idleColour);
                break;

            default:
                TintAll(idleColour);
                break;
        }
        
    }

    void TintAll(Color c)
    {
        foreach (var cInfo in cubes)
            cInfo.rend.material.color = c;
    }

    void DrawBars(Color background)
    {
        if (rankCount <= 0)
        {
            TintAll(background);
            return;
        }

        float peak = GetLivePeak();
        float threshold = Mathf.Max(pm.EffectiveAmplitudeThreshold, 0.0001f);
        float baseline = Mathf.Clamp(pm.AmbientNoiseBaseline, 0f, threshold);

        float baselineFrac = threshold > 0f ? Mathf.Clamp01(baseline / threshold) : 0f;
        int ambientLit = Mathf.Clamp(Mathf.CeilToInt(baselineFrac * rankCount), 0, rankCount);

        int capacity = Mathf.Max(rankCount - ambientLit, 0);
        float numerator = Mathf.Max(peak - baseline, 0f);
        float denominator = Mathf.Max(threshold - baseline, 0.0001f);
        float frac = capacity > 0 ? Mathf.Clamp01(numerator / denominator) : 0f;
        int dynamicLit = Mathf.Clamp(Mathf.CeilToInt(frac * capacity), 0, capacity);

        Color ambientColor = Color.Lerp(background, activeColour, 0.35f);

        foreach (var cube in cubes)
        {
            Color target;
            if (cube.rank < ambientLit)
                target = ambientColor;
            else if (cube.rank < ambientLit + dynamicLit)
                target = activeColour;
            else
                target = background;

            cube.rend.material.color = target;
        }
    }

    float GetLivePeak()
    {
        // On Quest, Microphone.GetPosition(null) does not refer to the active device.
        // Read the live looping mic clip + device name from PhonemeManager instead.
        var clipField = typeof(PhonemeManager)
                        .GetField("micClip", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var devField  = typeof(PhonemeManager)
                        .GetField("micDevice", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var clip = clipField?.GetValue(pm) as AudioClip;
        var dev  = devField?.GetValue(pm) as string;
        if (clip == null || string.IsNullOrEmpty(dev) || !Microphone.IsRecording(dev))
            return 0f;

        const int WIN = 1024;
        int pos = Microphone.GetPosition(dev);
        if (pos < WIN) return 0f; // not enough data yet

        // Read the last WIN frames. If channels > 1, buffer is interleaved.
        int channels = Mathf.Max(1, clip.channels);
        float[] buf = new float[WIN * channels];
        clip.GetData(buf, pos - WIN);

        float peak = 0f;
        for (int i = 0; i < buf.Length; i++)
        {
            float a = Mathf.Abs(buf[i]);
            if (a > peak) peak = a;
        }
        return peak;
    }
}

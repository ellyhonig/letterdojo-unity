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
                DrawBars(waitingColour, pm.amplitudeThreshold);
                break;

            case PhonemeManager.PhonemeCheckState.Recording:
                DrawBars(idleColour, pm.amplitudeThreshold);
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

    void DrawBars(Color background, float thresh)
    {
        float peak = GetLivePeak();
        float frac = Mathf.Clamp01(peak / Mathf.Max(thresh, 0.0001f));
        int lit = Mathf.Clamp(Mathf.CeilToInt(frac * rankCount), 0, rankCount);

        foreach (var cube in cubes)
            cube.rend.material.color = (cube.rank < lit) ? activeColour : background;
    }

    float GetLivePeak()
    {
        // reflectively grab the private 'recordedClip' from PhonemeManager
        var fi = typeof(PhonemeManager)
                 .GetField("recordedClip", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var clip = fi?.GetValue(pm) as AudioClip;
        if (clip == null || !Microphone.IsRecording(null))
            return 0f;

        const int WIN = 1024;
        int pos = Microphone.GetPosition(null);
        if (pos < WIN) return 0f;

        float[] buf = new float[WIN * clip.channels];
        clip.GetData(buf, pos - WIN);

        float peak = 0f;
        foreach (var v in buf) peak = Mathf.Max(peak, Mathf.Abs(v));
        return peak;
    }
}

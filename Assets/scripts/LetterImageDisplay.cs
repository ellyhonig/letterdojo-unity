using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class Letter3DDisplay : MonoBehaviour
{
    [Header("3D Display Renderer")]
    public Renderer displayRenderer;

    [Header("Phoneme Manager Ref")]
    public PhonemeManager phonemeManager;

    private const float displayDuration = 5f;
    private int attemptCount = 0;
    private Coroutine displayCoroutine;

    private static readonly Dictionary<string, (string file1, string file2)> letterToFileNames
        = new Dictionary<string, (string, string)>()
    {
        { "a", ("A1","A2") },{ "b", ("B1","B2") },{ "c", ("C1","C2") },
        { "d", ("D1","D2") },{ "e", ("E1","E2") },{ "f", ("F1","F2") },
        { "g", ("G1","G2") },{ "h", ("H1","H2") },{ "i", ("I1","I2") },
        { "j", ("J1","J2") },{ "k", ("K1","K2") },{ "l", ("L1","L2") },
        { "m", ("M1","M2") },{ "n", ("N1","N2") },{ "o", ("O1","O2") },
        { "p", ("P1","P2") },{ "q", ("Q1","Q2") },{ "r", ("R1","R2") },
        { "s", ("S1","S2") },{ "t", ("T1","T2") },{ "u", ("U1","U2") },
        { "v", ("V1","V2") },{ "w", ("W1","W2") },{ "x", ("X1","X2") },
        { "y", ("Y1","Y2") },{ "z", ("Z1","Z2") }
    };

    void Start()
    {
        if (displayRenderer) 
            displayRenderer.enabled = false;
    }

    void OnEnable()
    {
        if (phonemeManager.levelManager != null)
            phonemeManager.levelManager.OnPhonemeCheckStart += ResetAttempts;
        phonemeManager.OnPhonemeIncorrect += HandleIncorrect; // only on incorrect
    }

    void OnDisable()
    {
        if (phonemeManager.levelManager != null)
            phonemeManager.levelManager.OnPhonemeCheckStart -= ResetAttempts;
        phonemeManager.OnPhonemeIncorrect -= HandleIncorrect;
    }

    private void ResetAttempts() => attemptCount = 0;

    private void HandleIncorrect()
    {
        if (displayCoroutine != null) 
            StopCoroutine(displayCoroutine);
        displayCoroutine = StartCoroutine(DisplayCoroutine());
    }

    private IEnumerator DisplayCoroutine()
    {
        ClearDisplay();

        string letter = phonemeManager.levelManager.currentLetter.ToLower();
        if (letterToFileNames.TryGetValue(letter, out var files))
        {
            string toLoad = attemptCount == 0 ? files.file1 : files.file2;
            string path = $"letterphotos/{toLoad}";
            var tex = Resources.Load<Texture2D>(path);
            if (tex && displayRenderer)
            {
                var mat = displayRenderer.material;
                if (mat.HasProperty("_BaseMap"))
                    mat.SetTexture("_BaseMap", tex);
                else
                    mat.mainTexture = tex;
                displayRenderer.enabled = true;
            }
        }

        attemptCount = Mathf.Clamp(attemptCount + 1, 0, 1);
        yield return new WaitForSeconds(displayDuration);
        ClearDisplay();
    }

    private void ClearDisplay()
    {
        if (!displayRenderer) return;
        var mat = displayRenderer.material;
        if (mat.HasProperty("_BaseMap"))
            mat.SetTexture("_BaseMap", null);
        else
            mat.mainTexture = null;
        displayRenderer.enabled = false;
    }
}

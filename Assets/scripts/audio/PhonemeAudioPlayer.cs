using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Plays the phoneme audio clip that matches the current LevelManager letter when dictation begins.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class PhonemeAudioPlayer : MonoBehaviour
{
    [SerializeField] private LevelManager levelManager;
    [SerializeField] private DictationManager dictationManager;
    [Header("Phoneme Clips (assign manually)")]
    [SerializeField] private AudioClip clipA;
    [SerializeField] private AudioClip clipB;
    [SerializeField] private AudioClip clipC;
    [SerializeField] private AudioClip clipD;
    [SerializeField] private AudioClip clipE;
    [SerializeField] private AudioClip clipF;
    [SerializeField] private AudioClip clipG;
    [SerializeField] private AudioClip clipH;
    [SerializeField] private AudioClip clipI;
    [SerializeField] private AudioClip clipJ;
    [SerializeField] private AudioClip clipK;
    [SerializeField] private AudioClip clipL;
    [SerializeField] private AudioClip clipM;
    [SerializeField] private AudioClip clipN;
    [SerializeField] private AudioClip clipO;
    [SerializeField] private AudioClip clipP;
    [SerializeField] private AudioClip clipQ;
    [SerializeField] private AudioClip clipR;
    [SerializeField] private AudioClip clipS;
    [SerializeField] private AudioClip clipT;
    [SerializeField] private AudioClip clipU;
    [SerializeField] private AudioClip clipV;
    [SerializeField] private AudioClip clipW;
    [SerializeField] private AudioClip clipX;
    [SerializeField] private AudioClip clipY;
    [SerializeField] private AudioClip clipZ;

    private readonly Dictionary<char, AudioClip> clipsByLetter = new Dictionary<char, AudioClip>();
    private readonly HashSet<char> missingLetters = new HashSet<char>();
    private AudioSource audioSource;
    private bool warnedMissingDictationManager;

    private void Awake()
    {
        audioSource = GetComponent<AudioSource>();
        ResolveDependencies();
        BuildClipMap();
    }

    private void OnEnable()
    {
        ResolveDependencies();
        BuildClipMap();
        if (dictationManager != null)
            dictationManager.OnDictationStart += HandleDictationStart;
        else if (!warnedMissingDictationManager)
        {
            Debug.LogWarning("[PhonemeAudioPlayer] DictationManager reference missing; phoneme audio playback disabled.");
            warnedMissingDictationManager = true;
        }
    }

    private void OnDisable()
    {
        if (dictationManager != null)
            dictationManager.OnDictationStart -= HandleDictationStart;
    }

    private void ResolveDependencies()
    {
        if (!levelManager)
            levelManager = GetComponent<LevelManager>();
        if (!levelManager)
            levelManager = FindObjectOfType<LevelManager>();

        if (!dictationManager)
            dictationManager = GetComponent<DictationManager>();
        if (!dictationManager)
            dictationManager = FindObjectOfType<DictationManager>();
    }

    private void HandleDictationStart()
    {
        if (!levelManager)
        {
            Debug.LogWarning("[PhonemeAudioPlayer] LevelManager reference missing; cannot play phoneme audio.");
            return;
        }

        var letter = levelManager.currentLetter;
        if (string.IsNullOrWhiteSpace(letter))
        {
            Debug.LogWarning("[PhonemeAudioPlayer] LevelManager returned an empty letter; skipping audio playback.");
            return;
        }

        var key = ExtractFirstLetter(letter);
        if (key == null)
        {
            Debug.LogWarning($"[PhonemeAudioPlayer] Unable to determine letter from '{letter}'.");
            return;
        }

        if (!clipsByLetter.TryGetValue(key.Value, out var clip) || clip == null)
        {
            if (missingLetters.Add(key.Value))
            {
                Debug.LogWarning($"[PhonemeAudioPlayer] No audio clip assigned for letter '{key.Value}'.");
            }
            return;
        }

        if (!audioSource)
        {
            Debug.LogWarning("[PhonemeAudioPlayer] Missing AudioSource; cannot play clip.");
            return;
        }

        audioSource.Stop();
        audioSource.clip = clip;
        audioSource.Play();
        Debug.Log($"[PhonemeAudioPlayer] Playing clip '{clip.name}' for letter '{key.Value}'.");
    }

    private static char? ExtractFirstLetter(string value)
    {
        foreach (char c in value)
        {
            if (char.IsLetter(c))
                return char.ToUpperInvariant(c);
        }
        return null;
    }

    private void BuildClipMap()
    {
        clipsByLetter.Clear();
        missingLetters.Clear();

        TryRegister('A', clipA);
        TryRegister('B', clipB);
        TryRegister('C', clipC);
        TryRegister('D', clipD);
        TryRegister('E', clipE);
        TryRegister('F', clipF);
        TryRegister('G', clipG);
        TryRegister('H', clipH);
        TryRegister('I', clipI);
        TryRegister('J', clipJ);
        TryRegister('K', clipK);
        TryRegister('L', clipL);
        TryRegister('M', clipM);
        TryRegister('N', clipN);
        TryRegister('O', clipO);
        TryRegister('P', clipP);
        TryRegister('Q', clipQ);
        TryRegister('R', clipR);
        TryRegister('S', clipS);
        TryRegister('T', clipT);
        TryRegister('U', clipU);
        TryRegister('V', clipV);
        TryRegister('W', clipW);
        TryRegister('X', clipX);
        TryRegister('Y', clipY);
        TryRegister('Z', clipZ);
    }

    private void TryRegister(char letter, AudioClip clip)
    {
        if (!clip)
            return;
        clipsByLetter[letter] = clip;
    }
}

using UnityEngine;

public class AudioManager : MonoBehaviour
{
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private AudioClip popSound;
    [SerializeField] private AudioClip winSound;
    [SerializeField] private AudioClip munchSound;
    [SerializeField] private AudioClip incorrectSound;    // NEW

    [SerializeField] private LetterTracingSystem tracingSystem;
    [SerializeField] private ObjectOfInterestManager objManager;
    private DictationManager dictationManager;
    private PhonemeManager phonemeManager;
    private LevelManager levelManager;

    private float winSoundCooldown = 0.4f;
    private float lastWinSoundTime;

    private void Start()
    {
        objManager = GetComponent<ObjectOfInterestManager>();

        if (audioSource == null)
            audioSource = gameObject.AddComponent<AudioSource>();

        if (tracingSystem == null)
            tracingSystem = GetComponent<LetterTracingSystem>();

        if (tracingSystem != null)
        {
            tracingSystem.OnKeyframeReached += PlayPopSound;
            tracingSystem.OnTraceCompleted  += PlayWinSound;
            objManager.OnHMDProximity       += PlayMunchSound;
        }
        else Debug.LogError("LetterTracingSystem missing.");

        phonemeManager = FindObjectOfType<PhonemeManager>();
        dictationManager = GetComponent<DictationManager>();

        if (phonemeManager != null)
        {
            phonemeManager.OnPhonemeCorrect         += PlayWinSound;
            phonemeManager.OnPhonemeIncorrect       += OnPhonemeIncorrectHandler;    // now plays incorrectSound
            phonemeManager.OnPhonemeTriesExhausted  += OnPhonemeTriesExhaustedHandler;
        }
        else Debug.LogError("PhonemeManager missing.");

        levelManager = FindObjectOfType<LevelManager>();
        if (levelManager == null) Debug.LogError("LevelManager missing.");

        if (dictationManager != null)
        {
            dictationManager.OnLetterCorrect   += PlayWinSound;
            dictationManager.OnLetterIncorrect+= PlayPopSound;
        }
        else Debug.LogError("DictationManager missing.");

        lastWinSoundTime = -winSoundCooldown;
    }

    private void PlaySound(AudioClip clip)
    {
        if (audioSource && clip)
            audioSource.PlayOneShot(clip);
        else
            Debug.LogWarning("AudioSource or clip null.");
    }

    private void PlayPopSound()  => PlaySound(popSound);
    private void PlayWinSound()
    {
        if (Time.time >= lastWinSoundTime + winSoundCooldown)
        {
            PlaySound(winSound);
            lastWinSoundTime = Time.time;
        }
    }
    private void PlayMunchSound() => PlaySound(munchSound);

    // NEW handler plays the new incorrectSound
    private void OnPhonemeIncorrectHandler()
    {
        PlaySound(incorrectSound);
    }

    private void OnPhonemeTriesExhaustedHandler()
    {
        // still use popSound on exhaustion
        PlayPopSound();
    }

    private void OnDisable()
    {
        if (tracingSystem != null)
        {
            tracingSystem.OnKeyframeReached -= PlayPopSound;
            tracingSystem.OnTraceCompleted  -= PlayWinSound;
        }
        if (objManager != null)
            objManager.OnHMDProximity -= PlayMunchSound;

        if (phonemeManager != null)
        {
            phonemeManager.OnPhonemeCorrect        -= PlayWinSound;
            phonemeManager.OnPhonemeIncorrect      -= OnPhonemeIncorrectHandler;
            phonemeManager.OnPhonemeTriesExhausted -= OnPhonemeTriesExhaustedHandler;
        }

        if (dictationManager != null)
            dictationManager.OnLetterIncorrect -= PlayPopSound;
    }
}

using UnityEngine;
using TMPro;
using System.Collections;
using System.Reflection;

public class PhonemeDisplayManager : MonoBehaviour
{
    [Header("TextMeshPro References")]
    [SerializeField] private TextMeshPro phonemeText;
    [SerializeField] private TextMeshPro canvasText;

    [Header("Visual Settings")]
    public Color correctColor   = Color.green;
    public Color incorrectColor = Color.red;
    public Color defaultColor   = Color.white;
    public float displayDuration = 1.5f;
    public float checkInterval   = 0.01f;

    private LevelManager levelManager;
    private PhonemeManager phonemeManager;
    private Coroutine clearTextCoroutine;
    private string currentLetter;
    private FieldInfo beamModeField;

    void Awake()
    {
        phonemeText = phonemeText ?? GetComponent<TextMeshPro>();
        phonemeText.text  = "";
        phonemeText.color = defaultColor;
    }

    void Start()
    {
        levelManager   = GetComponent<LevelManager>();
        phonemeManager = GetComponent<PhonemeManager>();
        beamModeField  = typeof(PhonemeManager)
                         .GetField("beamMode", BindingFlags.NonPublic | BindingFlags.Instance);

        phonemeManager.OnPhonemeCorrect   += () => OnPhonemeResult(true);
        phonemeManager.OnPhonemeIncorrect += () => OnPhonemeResult(false);

        currentLetter = levelManager.currentLetter;
        StartCoroutine(CheckForLetterChanges());
    }

    IEnumerator CheckForLetterChanges()
    {
        while (true)
        {
            if (currentLetter != levelManager.currentLetter)
                currentLetter = levelManager.currentLetter;
            if (canvasText != null)
                canvasText.text = currentLetter;
            yield return new WaitForSeconds(checkInterval);
        }
    }

    void OnPhonemeResult(bool wasCorrect)
    {
        var mode = (PhonemeManager.BeamMode)beamModeField.GetValue(phonemeManager);
        // clear if not BeamOn
        if (mode != PhonemeManager.BeamMode.BeamOn )
        {
            StopCoroutineIfNeeded();
            phonemeText.text  = "";
            phonemeText.color = defaultColor;
            return;
        }

        StopCoroutineIfNeeded();
        phonemeText.text  = phonemeManager.lastBeamText;
        Debug.Log("phonemeText.text: " + phonemeText.text);
        phonemeText.color = wasCorrect ? correctColor : incorrectColor;
        clearTextCoroutine  = StartCoroutine(ClearTextAfterDelay());
    }

    IEnumerator ClearTextAfterDelay()
    {
        yield return new WaitForSeconds(displayDuration);
        phonemeText.text  = "";
        phonemeText.color = defaultColor;
        clearTextCoroutine = null;
    }

    void StopCoroutineIfNeeded()
    {
        if (clearTextCoroutine != null)
            StopCoroutine(clearTextCoroutine);
    }

    void OnDestroy() => StopAllCoroutines();
}

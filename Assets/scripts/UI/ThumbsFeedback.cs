using UnityEngine;
using System.Collections;


public class ThumbsFeedback : MonoBehaviour
{
    [Header("Ref")]
    [SerializeField] private AudioManager audioManager;
    [SerializeField] private GameObject thumbsUpPlane;    // assign
    [SerializeField] private GameObject thumbsDownPlane;  // assign

    [Header("Display")]
    [SerializeField, Min(0f)] private float showDuration = 1.0f; // seconds
    [SerializeField] private bool useUnscaledTime = false;        // optional

    private Coroutine displayCo;

    private void Reset()
    {
        if (!audioManager) audioManager = FindObjectOfType<AudioManager>();
    }

    private void Awake()
    {
        if (!audioManager) audioManager = FindObjectOfType<AudioManager>();
        HideAll();
    }

    private void OnEnable()
    {
        if (audioManager != null)
        {
            audioManager.OnWinSoundPlayed       += HandleWin;
            audioManager.OnIncorrectSoundPlayed += HandleIncorrect;
        }
    }

    private void OnDisable()
    {
        if (audioManager != null)
        {
            audioManager.OnWinSoundPlayed       -= HandleWin;
            audioManager.OnIncorrectSoundPlayed -= HandleIncorrect;
        }
        StopCurrent();
        HideAll();
    }

    private void HandleWin()
    {
        Flash(thumbsUpPlane, thumbsDownPlane);
    }

    private void HandleIncorrect()
    {
        Flash(thumbsDownPlane, thumbsUpPlane);
    }

    private void Flash(GameObject toShow, GameObject toHide)
    {
        if (!toShow || !toHide) return;

        // mutex: only one visible at a time
        toHide.SetActive(false);
        toShow.SetActive(true);

        // restart timer
        if (displayCo != null) StopCoroutine(displayCo);
        displayCo = StartCoroutine(HideAfterDelay(toShow, showDuration));
    }

    private IEnumerator HideAfterDelay(GameObject go, float seconds)
    {
        float end = (useUnscaledTime ? Time.unscaledTime : Time.time) + seconds;
        while ((useUnscaledTime ? Time.unscaledTime : Time.time) < end)
            yield return null;

        if (go) go.SetActive(false);
        displayCo = null;
    }

    private void HideAll()
    {
        if (thumbsUpPlane)   thumbsUpPlane.SetActive(false);
        if (thumbsDownPlane) thumbsDownPlane.SetActive(false);
    }

    private void StopCurrent()
    {
        if (displayCo != null)
        {
            StopCoroutine(displayCo);
            displayCo = null;
        }
    }
}

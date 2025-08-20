using UnityEngine;
using System.Collections;
using UnityEngine.Events;

public class SimpleMicVolumeChecker : MonoBehaviour
{
    public float volumeThreshold = 0.1f;
    public float requiredDuration = 0.2f;
    public UnityEvent onVoiceDetected;

    private AudioClip microphoneClip;
    private string selectedMicrophone;
    private bool isListening = false;
    private float timer = 0f;
    public float volumeLevel;

    private void Start()
    {
        if (Microphone.devices.Length > 0)
        {
            selectedMicrophone = Microphone.devices[0];
            StartCoroutine(MicrophoneCheck());
        }
        else
        {
            Debug.LogError("No microphone detected!");
        }

        if (onVoiceDetected == null)
        {
            onVoiceDetected = new UnityEvent();
        }
    }

    public IEnumerator MicrophoneCheck()
    {
        microphoneClip = Microphone.Start(selectedMicrophone, true, 1, AudioSettings.outputSampleRate);
        yield return new WaitForSeconds(0.1f); // Wait for microphone to initialize

        isListening = true;

        while (isListening)
        {
            volumeLevel = GetAverageVolume();

            if (volumeLevel > volumeThreshold)
            {
                timer += Time.deltaTime;
                if (timer >= requiredDuration)
                {
                    onVoiceDetected.Invoke();
                    timer = 0f; // Reset timer after detection
                }
            }
            else
            {
                timer = 0f;
            }

            yield return null;
        }
    }

    private float GetAverageVolume()
    {
        float[] data = new float[128];
        int position = Microphone.GetPosition(selectedMicrophone);
        if (position < data.Length) return 0; // Not enough data yet

        microphoneClip.GetData(data, position - data.Length);

        float sum = 0f;
        for (int i = 0; i < data.Length; i++)
        {
            sum += Mathf.Abs(data[i]);
        }
        return sum / data.Length;
    }

    private void OnDisable()
    {
        isListening = false;
        if (Microphone.IsRecording(selectedMicrophone))
        {
            Microphone.End(selectedMicrophone);
        }
    }
}

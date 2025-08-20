using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Collections;
using System.Text;

/// <summary>
/// Attach this script to a GameObject in your Unity scene.
/// Holding down the Space key starts recording audio via the default microphone.
/// Releasing Space stops recording and sends the audio to the Beam server.
/// </summary>
public class SpacebarRecordingTest : MonoBehaviour
{
    // --- Beam API Info ---
    [Header("Beam API")]
    [SerializeField] private string API_URL = "https://recognize-3a64e01-v3.app.beam.cloud";
    [SerializeField] private string TOKEN = "YOUR_BEAM_TOKEN";

    // --- Recording Settings ---
    [Header("Recording Settings")]
    [SerializeField] private int maxRecordingSeconds = 10;  // maximum clip length
    [SerializeField] private int recordingSampleRate = 44100;

    // Private fields
    private AudioClip recordedClip;
    private string micDevice;  // which microphone to use
    private bool isRecording = false;

    void Start()
    {
        // Pick the first available microphone, or specify by name if needed
        if (Microphone.devices.Length > 0)
        {
            micDevice = Microphone.devices[0];
            Debug.Log($"Using microphone: {micDevice}");
        }
        else
        {
            Debug.LogError("No microphone devices found!");
        }
    }

    void Update()
    {
        // Start recording on KeyDown
        if (Input.GetKeyDown(KeyCode.Space) && !isRecording && micDevice != null)
        {
            StartRecording();
        }
        // Stop recording on KeyUp
        else if (Input.GetKeyUp(KeyCode.Space) && isRecording)
        {
            StopRecording();
            // Send the recorded audio to Beam
            SendToBeam();
        }
    }

    /// <summary>
    /// Begin recording using the Unity Microphone API.
    /// </summary>
    private void StartRecording()
    {
        Debug.Log("Start Recording...");
        isRecording = true;
        recordedClip = Microphone.Start(
            deviceName: micDevice,
            loop: false,
            lengthSec: maxRecordingSeconds,
            frequency: recordingSampleRate
        );
    }

    /// <summary>
    /// End recording.
    /// </summary>
    private void StopRecording()
    {
        Debug.Log("Stop Recording.");
        if (Microphone.IsRecording(micDevice))
        {
            Microphone.End(micDevice);
        }
        isRecording = false;
    }

    /// <summary>
    /// Convert the recorded AudioClip to WAV bytes, Base64-encode it, and POST to Beam.
    /// </summary>
    private void SendToBeam()
    {
        if (recordedClip == null)
        {
            Debug.LogError("No audio clip to send.");
            return;
        }

        // Convert to WAV
        byte[] wavData = WavUtility.FromAudioClip(recordedClip, out string wavInfo);
        Debug.Log($"WAV Info: {wavInfo}");

        // Encode to Base64
        string base64Audio = Convert.ToBase64String(wavData);

        // Fire off the request
        StartCoroutine(PostRequest(base64Audio));
    }

    /// <summary>
    /// Coroutine to send the Base64 WAV data as JSON to Beam using UnityWebRequest.
    /// </summary>
    private IEnumerator PostRequest(string base64Audio)
    {
        // Build JSON payload
        var requestData = new AudioRequest { audio_file = base64Audio };
        string jsonData = JsonUtility.ToJson(requestData);

        // Create a POST request
        UnityWebRequest request = new UnityWebRequest(API_URL, "POST")
        {
            uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonData)),
            downloadHandler = new DownloadHandlerBuffer()
        };

        // Headers
        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("Authorization", $"Bearer {TOKEN}");

        // Send the request
        yield return request.SendWebRequest();

        // Check response
        if (request.result == UnityWebRequest.Result.Success)
        {
            // Assuming the response is JSON like {"text": "phoneme results"}
            Debug.Log($"Beam response: {request.downloadHandler.text}");
        }
        else
        {
            Debug.LogError($"HTTP Error {request.responseCode}: {request.error}");
        }
    }

    /// <summary>
    /// Simple class for JSON payload ("audio_file" key).
    /// </summary>
    [Serializable]
    private class AudioRequest
    {
        public string audio_file;
    }
}

/// <summary>
/// Minimal WAV Utility for converting an AudioClip to a 16-bit WAV byte array.
/// </summary>
public static class WavUtility
{
    /// <summary>
    /// Converts an AudioClip to WAV data (16-bit). Returns the raw WAV bytes.
    /// Also sets a debug string with basic info about the clip.
    /// </summary>
    public static byte[] FromAudioClip(AudioClip clip, out string debugInfo)
    {
        // Get the raw float samples from the clip
        float[] samples = new float[clip.samples * clip.channels];
        clip.GetData(samples, 0);

        // Convert floats to 16-bit PCM
        short[] intData = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            intData[i] = (short)Mathf.Clamp(samples[i] * short.MaxValue, short.MinValue, short.MaxValue);
        }

        // Create a byte buffer
        byte[] bytesData = new byte[intData.Length * 2];
        Buffer.BlockCopy(intData, 0, bytesData, 0, bytesData.Length);

        // Calculate overall byte size
        int sampleCount = samples.Length;
        int channelCount = clip.channels;
        int sampleRate = clip.frequency;

        // Build the WAV header + data
        byte[] wav = AddWavHeader(bytesData, channelCount, sampleRate);

        debugInfo = $"Channels: {channelCount}, SampleRate: {sampleRate}, Samples: {sampleCount}, Bytes: {wav.Length}";
        return wav;
    }

    /// <summary>
    /// Prepend a WAV header to the PCM bytes.
    /// </summary>
    private static byte[] AddWavHeader(byte[] pcmData, int channels, int sampleRate)
    {
        // Reference: https://stackoverflow.com/questions/26589416/write-new-wave-file-using-raw-pcm-data-in-c-sharp

        int totalDataLen = pcmData.Length + 36;
        int byteRate = sampleRate * channels * 2; // 16 bit = 2 bytes
        short blockAlign = (short)(channels * 2);
        short bitsPerSample = 16;

        using (var memStream = new System.IO.MemoryStream(44 + pcmData.Length))
        using (var writer = new System.IO.BinaryWriter(memStream))
        {
            // RIFF header
            writer.Write(System.Text.Encoding.UTF8.GetBytes("RIFF"));
            writer.Write(totalDataLen);
            writer.Write(System.Text.Encoding.UTF8.GetBytes("WAVE"));

            // fmt chunk
            writer.Write(System.Text.Encoding.UTF8.GetBytes("fmt "));
            writer.Write(16);  // Sub chunk size
            writer.Write((short)1); // AudioFormat = PCM
            writer.Write((short)channels);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write(blockAlign);
            writer.Write(bitsPerSample);

            // data chunk
            writer.Write(System.Text.Encoding.UTF8.GetBytes("data"));
            writer.Write(pcmData.Length);
            writer.Write(pcmData);

            return memStream.ToArray();
        }
    }
}

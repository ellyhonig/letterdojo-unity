// BeamPronunciationTester.cs
using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Collections;
using System.Text;

/// <summary>
/// Quick-n-dirty mic capture -> Beam test.
/// • SPACE once  = start recording
/// • SPACE again = stop + send to Beam
/// Logs Beam's "text" field.
/// Needs WavUtility.cs in project.
/// </summary>
public class BeamPronunciationTester : MonoBehaviour
{
    [Header("Beam")]
    [SerializeField] private string API_URL = "https://recognize-xxxxx.app.beam.cloud";
    [SerializeField] private string TOKEN   = "YOUR_BEAM_TOKEN";

    [Header("Mic")]
    [SerializeField] private int   sampleRate          = 44100;
    [SerializeField] private int   maxRecordingSeconds = 10;

    private string     mic;
    private AudioClip  clip;
    private bool       recording;

    void Awake()
    {
        if (Microphone.devices.Length == 0)
        {
            Debug.LogError("BeamTester | no mic found"); enabled = false; return;
        }
        mic = Microphone.devices[0];
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            if (!recording) StartRec();
            else            StopRecAndSend();
        }
    }

    /* ────────── MIC ────────── */
    void StartRec()
    {
        clip      = Microphone.Start(mic, false, maxRecordingSeconds, sampleRate);
        recording = true;
        Debug.Log("BeamTester | recording…");
    }

    void StopRecAndSend()
    {
        if (!recording) return;
        Microphone.End(mic);
        recording = false;
        Debug.Log("BeamTester | sending…");

        byte[] wav = WavUtility.FromAudioClip(clip, out _);
        StartCoroutine(PostToBeam(Convert.ToBase64String(wav)));
    }

    /* ────────── BEAM ────────── */
    IEnumerator PostToBeam(string b64)
    {
        string body = JsonUtility.ToJson(new BeamReq { audio_file = b64 });
        UnityWebRequest r = new(API_URL, "POST")
        {
            uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)),
            downloadHandler = new DownloadHandlerBuffer()
        };
        r.SetRequestHeader("Content-Type", "application/json");
        r.SetRequestHeader("Authorization", $"Bearer {TOKEN}");
        yield return r.SendWebRequest();

        if (r.result == UnityWebRequest.Result.Success)
            Debug.Log($"BeamTester | text → {ParseText(r.downloadHandler.text)}");
        else
            Debug.LogError($"BeamTester | HTTP {r.responseCode}: {r.error}");
    }

    /* grab `"text":"..."` fast */
    static string ParseText(string json)
    {
        int i = json.IndexOf("\"text\":\"", StringComparison.Ordinal);
        if (i < 0) return "";
        int start = i + 8;
        int end   = json.IndexOf("\"", start, StringComparison.Ordinal);
        return end > start ? json.Substring(start, end - start) : "";
    }

    [Serializable] private struct BeamReq { public string audio_file; }
}

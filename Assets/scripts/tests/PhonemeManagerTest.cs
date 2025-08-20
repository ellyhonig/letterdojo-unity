/*
 * PhonemeManagerTest.cs  (July‑2025 automated loop build)
 * ---------------------------------------------
 * • Automatically records A–Z in a loop (no manual start).
 * • Stops on loudness trigger, sends to Beam, grades, then restarts.
 * • Lenient IPA match + full logging.
 */

using UnityEngine;
using UnityEngine.Networking;
using TMPro;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

public class PhonemeManagerTest : MonoBehaviour
{
    public enum BeamMode { BeamOn, MarkAnswersWrong, MarkAnswersCorrect }

    [Header("Beam Mode")]
    [SerializeField] private BeamMode beamMode = BeamMode.BeamOn;
    [Header("UI")]
    [SerializeField] private TextMeshPro feedbackText;
    [Header("Beam API")]
    [SerializeField] private string API_URL = "https://recognize-cpu-ce752ad-v2.app.beam.cloud";
    [SerializeField] private string TOKEN   = "YOUR_BEAM_TOKEN";
    [Header("Mic Settings")]
    [SerializeField] private int maxRecordingSeconds = 4;
    [SerializeField] private int sampleRate = 16000;
    [Header("Auto‑Stop Settings")]
    [SerializeField] private float amplitudeThreshold = 0.03f;
    [SerializeField] private float loudEnoughTime     = 0.18f;

    private readonly string[] letters = Enumerable.Range('A',26)
                                                  .Select(c=>((char)c).ToString())
                                                  .ToArray();
    private int currentIndex = 0;
    private const float CONF_THRESHOLD = 0.35f;

    private AudioClip micClip, recordedClip;
    private string micDevice;
    private int startSample;
    private bool autoStopped;
    private float loudTimer;

    // Lenient IPA patterns per letter
    private static readonly Dictionary<string,string[]> ipaAccept = new()
    {
        {"A", new[]{"a","ɑ","æ","ʌ","ɒ","aː","ɑː"}},
        {"B", new[]{"b","bə","bʌ","b̩"}},
        {"C", new[]{"k","kʰ","kə","kʌ","s","sə","ks","ɡz"}},
        {"D", new[]{"d","də","dʌ","ɾə"}},
        {"E", new[]{"ɛ","e","e̞","eɪ","i","ɪ","ə"}},
        {"F", new[]{"f","fə","fʌ","f̩"}},
        {"G", new[]{"ɡ","g","ɡə","gʌ","dʒ","dʒə"}},
        {"H", new[]{"h","hə","hʌ"}},
        {"I", new[]{"ɪ","i","iː","ɪə","ə","aɪ"}},
        {"J", new[]{"dʒ","dʒə","ʒ","ʒə"}},
        {"K", new[]{"k","kʰ","kə","kʌ"}},
        {"L", new[]{"l","lə","lʌ","l̩","ɫ"}},
        {"M", new[]{"m","mə","mʌ","m̩"}},
        {"N", new[]{"n","nə","nʌ","n̩","ŋ"}},
        {"O", new[]{"oʊ","ɔ","ɒ","ɑ","əʊ","o","oː"}},
        {"P", new[]{"p","pʰ","pə","pʌ"}},
        {"Q", new[]{"k","kw","kju","kjuː","kwə"}},
        {"R", new[]{"ɹ","r","ɾ","rə","ɹ̩","ɚ","ɝ"}},
        {"S", new[]{"s","sə","sʌ","ʃ","ʃə"}},
        {"T", new[]{"t","tʰ","tə","tʌ","ʧ","tʃ"}},
        {"U", new[]{"u","ʊ","ju","juː","ʌ","ə","uː"}},
        {"V", new[]{"v","və","vʌ","v̩"}},
        {"W", new[]{"w","wə","wʌ","ʍ"}},
        {"X", new[]{"ks","kəs","ɡz","ɛks"}},
        {"Y", new[]{"j","jə","jʌ","jaɪ","ɪ"}},
        {"Z", new[]{"z","zə","zʌ","zi","zɛd","ʒ"}}
    };
    private static readonly Regex ipaClean = new(@"[ˈˌ\.\s]", RegexOptions.Compiled);

    private static string NormalizeIPA(string ipa)
    {
        if (string.IsNullOrEmpty(ipa)) return string.Empty;
        return ipaClean.Replace(ipa.ToLowerInvariant(), string.Empty);
    }

    private static bool LenientIPAMatch(string ipaRaw, string letter, out string matchedPattern)
    {
        matchedPattern = string.Empty;
        if (!ipaAccept.TryGetValue(letter, out var patterns))
            return false;
        var ipa = NormalizeIPA(ipaRaw);
        foreach (var p in patterns)
        {
            if (ipa.Contains(NormalizeIPA(p)))
            {
                matchedPattern = p;
                return true;
            }
        }
        return false;
    }

    private void Start()
    {
        // Auto‑initialize microphone
        var devices = Microphone.devices;
        if (devices.Length > 0)
        {
            micDevice = devices[0];
            micClip = Microphone.Start(micDevice, true, maxRecordingSeconds, sampleRate);
        }
        // kick off first recording
        StartCoroutine(RecordingLoop());
    }

    private IEnumerator RecordingLoop()
    {
        while (true)
        {
            // Start recording
            feedbackText?.SetText($"Say {letters[currentIndex]}");
            int start = Microphone.GetPosition(micDevice);
            autoStopped = false;
            loudTimer = 0f;
            // wait for loudness trigger
            while (!autoStopped)
            {
                yield return null;
                int pos = Microphone.GetPosition(micDevice);
                if (pos < 0 || micClip == null) continue;
                int delta = (pos - start + micClip.samples) % micClip.samples;
                if (delta < 1024) continue;
                var buf = new float[1024];
                micClip.GetData(buf, (pos - 1024 + micClip.samples) % micClip.samples);
                float peak = buf.Max(Mathf.Abs);
                loudTimer = peak >= amplitudeThreshold ? loudTimer + Time.deltaTime : 0f;
                if (loudTimer >= loudEnoughTime) autoStopped = true;
            }
            // stop and trim
            int end = Microphone.GetPosition(micDevice);
            int len = (end - start + micClip.samples) % micClip.samples;
            var data = new float[len];
            micClip.GetData(data, start);
            recordedClip = AudioClip.Create("take", len, 1, sampleRate, false);
            recordedClip.SetData(data, 0);

            // Process
            feedbackText?.SetText("Processing...");
            byte[] wav = WavUtility.FromAudioClip(recordedClip, out _);
            // send
            yield return SendAndGrade(wav);
            // move to next letter
            currentIndex = (currentIndex + 1) % letters.Length;
        }
    }

    private IEnumerator SendAndGrade(byte[] wavBytes)
    {
        string b64 = Convert.ToBase64String(wavBytes);
        var payload = JsonUtility.ToJson(new { audio_file = b64 });
        using var r = new UnityWebRequest(API_URL, "POST")
        {
            uploadHandler   = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(payload)),
            downloadHandler = new DownloadHandlerBuffer()
        };
        r.SetRequestHeader("Content-Type","application/json");
        r.SetRequestHeader("Authorization",$"Bearer {TOKEN}");
        yield return r.SendWebRequest();

        string txt = r.downloadHandler.text;
        Debug.Log($"[Beam RAW] {txt}");

        if (r.result == UnityWebRequest.Result.Success)
        {
            var resp = JsonUtility.FromJson<BeamResp>(txt);
            string ipa = !string.IsNullOrEmpty(resp.clean) ? resp.clean : (resp.text ?? resp.canon);
            float conf = resp.conf > 0 ? resp.conf : 1f;

            bool matched = LenientIPAMatch(ipa, letters[currentIndex], out string pat);
            bool ok      = conf >= CONF_THRESHOLD && matched;
            Debug.Log($"[GRADE] target='{letters[currentIndex]}' ipa='{ipa}' conf={conf:0.00} accepted={ok} match='{pat}'");

            feedbackText?.SetText(ok ? "✔ Good" : "✖ Try again");
        }
        else
        {
            feedbackText?.SetText("✖ Error");
        }
    }

    [Serializable] private class BeamResp { public string text, clean, canon; public float conf; }
}

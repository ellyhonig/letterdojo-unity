using UnityEngine;
using System.IO;

public class SaveManager : MonoBehaviour
{
    string persistFmt(string letter) =>
        Path.Combine(Application.persistentDataPath, $"simple_recording_{letter}.json");

    public void SaveRecording(SimpleRecord rec, string letter)
    {
        var json = JsonUtility.ToJson(rec, true);
        File.WriteAllText(persistFmt(letter), json);
        Debug.Log($"saved → {persistFmt(letter)}");
    }

    public SimpleRecord LoadRecording(string letter)
    {
        // 1) try persistent
        //var p = persistFmt(letter);
        //if (File.Exists(p))
          //  return JsonUtility.FromJson<SimpleRecord>(File.ReadAllText(p));

        // 2) fallback to Resources
        var ta = Resources.Load<TextAsset>($"letter3Dpathdata/simple_recording_{letter}");
        if (ta != null)
            return JsonUtility.FromJson<SimpleRecord>(ta.text);

        Debug.LogWarning("no recording found for " + letter);
        return null;
    }
}

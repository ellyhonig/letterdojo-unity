#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(DictationManager))]
public class DictationManagerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        base.OnInspectorGUI();

        DictationManager manager = (DictationManager)target;
        if (GUILayout.Button("Play Current Letter Audio"))
        {
            manager.EditorPlayCurrentLetterAudio();
        }
    }
}
#endif

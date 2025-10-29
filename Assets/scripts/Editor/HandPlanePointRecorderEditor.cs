#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(HandPlanePointRecorder))]
public class HandPlanePointRecorderEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var recorder = (HandPlanePointRecorder)target;

        GUILayout.Space(8f);
        EditorGUILayout.LabelField("Recording Controls", EditorStyles.boldLabel);

        EditorGUI.BeginDisabledGroup(!Application.isPlaying);
        string toggleLabel = recorder.IsRecording ? "Stop Recording" : "Start Recording";
        if (GUILayout.Button(toggleLabel))
        {
            recorder.ToggleRecording();
            EditorUtility.SetDirty(recorder);
        }
        EditorGUI.EndDisabledGroup();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Save"))
        {
            recorder.SaveRecording();
        }
        if (GUILayout.Button("Load"))
        {
            recorder.LoadRecording();
        }
        GUILayout.EndHorizontal();
    }
}
#endif

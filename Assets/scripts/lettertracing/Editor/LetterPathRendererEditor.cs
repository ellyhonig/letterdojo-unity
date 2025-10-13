#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(LetterPathRenderer))]
internal class LetterPathRendererEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var renderer = (LetterPathRenderer)target;

        EditorGUILayout.Space();
        if (GUILayout.Button("Regenerate Anchors"))
        {
            renderer.RegenerateAnchors();
        }
    }
}
#endif

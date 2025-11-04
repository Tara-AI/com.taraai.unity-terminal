using UnityEngine;
using UnityEditor;

public class TerminalSettingsWindow : EditorWindow
{
    private static TerminalSettings settings;

    public static void ShowWindow(TerminalSettings current)
    {
        settings = current;
        GetWindow<TerminalSettingsWindow>("Terminal Settings").Show();
    }

    private void OnGUI()
    {
        GUILayout.Label("Terminal Preferences", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        settings.preferredShell = EditorGUILayout.TextField("Preferred Shell", settings.preferredShell);
        settings.fontSize = EditorGUILayout.IntSlider("Font Size", settings.fontSize, 10, 24);
        settings.wordWrap = EditorGUILayout.Toggle("Word Wrap", settings.wordWrap);
        settings.enableTransparency = EditorGUILayout.Toggle("Transparency", settings.enableTransparency);

        settings.backgroundColor = EditorGUILayout.ColorField("Background Color", settings.backgroundColor);
        settings.accentColor = EditorGUILayout.ColorField("Accent Color", settings.accentColor);

        EditorGUILayout.Space(10);

        if (GUILayout.Button("Save Settings"))
        {
            settings.Save();
            Close();
        }
    }
}

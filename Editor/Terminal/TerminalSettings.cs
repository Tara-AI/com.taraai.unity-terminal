using UnityEngine;
using System.IO;

[System.Serializable]
public class TerminalSettings
{
    public string preferredShell = "auto"; // auto, pwsh, cmd, bash, zsh
    public Color backgroundColor = new Color(0.07f, 0.07f, 0.07f, 0.95f);
    public Color accentColor = new Color(0.0f, 0.55f, 1.0f);
    public int fontSize = 14;
    public bool wordWrap = true;
    public bool enableTransparency = true;

    private static string settingsPath => Path.Combine(Application.persistentDataPath, "terminal_settings.json");

    public static TerminalSettings Load()
    {
        if (File.Exists(settingsPath))
        {
            return JsonUtility.FromJson<TerminalSettings>(File.ReadAllText(settingsPath));
        }
        return new TerminalSettings();
    }

    public void Save()
    {
        File.WriteAllText(settingsPath, JsonUtility.ToJson(this, true));
    }
}

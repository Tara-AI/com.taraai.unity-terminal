using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using System.Diagnostics;
using System.Text;
using System.Collections.Generic;
using System.Linq;

public class CrossPlatformTerminalWindow : EditorWindow
{
    private Process shellProcess;
    private StringBuilder outputBuilder = new StringBuilder();
    private Vector2 scrollPos;
    private string inputCommand = "";
    private List<string> commandHistory = new List<string>();
    private int historyIndex = -1;
    private bool scrollToBottom = true;
    private TerminalSettings settings;

    private static string sessionFilePath => Path.Combine(Application.persistentDataPath, "terminal_session.txt");
    private static string historyFilePath => Path.Combine(Application.persistentDataPath, "terminal_history.json");

    [MenuItem("Window/Terminal/Terminal")]
    public static void ShowWindow()
    {
        GetWindow<CrossPlatformTerminalWindow>("Terminal");
    }

    private void OnEnable()
    {
        settings = TerminalSettings.Load();
        EditorApplication.quitting += StopShell;
        StartShell();
        LoadHistory();
    }

    private void OnDisable()
    {
        SaveSession();
        SaveHistory();
        StopShell();
        EditorApplication.quitting -= StopShell;
    }

    private void StartShell()
    {
        string shellName = ResolveShell();
        string shellArgs = "";

        if (shellName.Contains("pwsh"))
            shellArgs = "-NoLogo";

        shellProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = shellName,
                Arguments = shellArgs,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            },
            EnableRaisingEvents = true
        };

        shellProcess.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) AppendOutput(e.Data, false); };
        shellProcess.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) AppendOutput(e.Data, true); };

        try
        {
            shellProcess.Start();
            shellProcess.BeginOutputReadLine();
            shellProcess.BeginErrorReadLine();
            AppendOutput($"[Started {shellName} session]\n", false);

            if (File.Exists(sessionFilePath))
                AppendOutput(File.ReadAllText(sessionFilePath), false);
        }
        catch (Exception ex)
        {
            AppendOutput("[Failed to start shell] " + ex.Message, true);
        }
    }

    private string ResolveShell()
    {
        string pref = settings.preferredShell.ToLower();
        if (pref != "auto")
            return pref;

        if (IsCommandAvailable("pwsh")) return "pwsh";
        if (Application.platform == RuntimePlatform.WindowsEditor) return "cmd.exe";
        if (IsCommandAvailable("bash")) return "bash";
        if (IsCommandAvailable("zsh")) return "zsh";
        return "/bin/sh";
    }

    private void StopShell()
    {
        try
        {
            if (shellProcess != null && !shellProcess.HasExited)
            {
                shellProcess.Kill();
                shellProcess.Dispose();
            }
        }
        catch { }
    }

    private void SendCommand(string command)
    {
        if (string.IsNullOrEmpty(command)) return;
        if (shellProcess == null || shellProcess.HasExited)
        {
            AppendOutput("Shell process not running.", true);
            return;
        }

        shellProcess.StandardInput.WriteLine(command);
        shellProcess.StandardInput.Flush();
        AppendOutput($"<color=#{ColorUtility.ToHtmlStringRGB(settings.accentColor)}>> {command}</color>", false);

        commandHistory.Add(command);
        historyIndex = commandHistory.Count;
    }

    private void AppendOutput(string text, bool isError)
    {
        string colored = ApplySyntaxColor(text, isError);
        outputBuilder.AppendLine(colored);
        scrollToBottom = true;
        Repaint();
    }

    private void OnGUI()
    {
        DrawModernToolbar();

        // Background style
        var bg = new GUIStyle(EditorStyles.textArea)
        {
            richText = true,
            wordWrap = settings.wordWrap,
            normal = { textColor = Color.white }
        };

        if (settings.enableTransparency)
            GUI.backgroundColor = new Color(settings.backgroundColor.r, settings.backgroundColor.g, settings.backgroundColor.b, settings.backgroundColor.a);
        else
            GUI.backgroundColor = settings.backgroundColor;

        scrollPos = EditorGUILayout.BeginScrollView(scrollPos, GUILayout.ExpandHeight(true));
        GUIStyle labelStyle = new GUIStyle(EditorStyles.label)
        {
            richText = true,
            fontSize = settings.fontSize,
            font = Font.CreateDynamicFontFromOSFont("Consolas", settings.fontSize)
        };
        GUILayout.Label(outputBuilder.ToString(), labelStyle);
        if (scrollToBottom && Event.current.type == EventType.Repaint)
        {
            scrollPos.y = Mathf.Infinity;
            scrollToBottom = false;
        }
        EditorGUILayout.EndScrollView();

        GUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();
        GUI.SetNextControlName("InputField");
        inputCommand = EditorGUILayout.TextField(inputCommand, GUILayout.ExpandWidth(true));

        GUI.backgroundColor = settings.accentColor;
        if (GUILayout.Button("Run", GUILayout.Width(70)))
        {
            SendCommand(inputCommand);
            inputCommand = "";
        }
        GUI.backgroundColor = Color.white;
        EditorGUILayout.EndHorizontal();

        HandleHistoryNavigation();
    }

    private void DrawModernToolbar()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

        GUI.backgroundColor = settings.accentColor;
        if (GUILayout.Button("Clear", EditorStyles.toolbarButton))
            outputBuilder.Clear();
        if (GUILayout.Button("Restart", EditorStyles.toolbarButton))
        {
            StopShell();
            StartShell();
        }
        GUI.backgroundColor = Color.white;

        GUILayout.FlexibleSpace();
        if (GUILayout.Button("⚙ Settings", EditorStyles.toolbarButton))
        {
            TerminalSettingsWindow.ShowWindow(settings);
        }
        EditorGUILayout.EndHorizontal();
    }

    private void HandleHistoryNavigation()
    {
        var e = Event.current;
        if (GUI.GetNameOfFocusedControl() != "InputField") return;

        if (e.type == EventType.KeyDown)
        {
            if (e.keyCode == KeyCode.UpArrow && historyIndex > 0)
            {
                historyIndex--;
                inputCommand = commandHistory[historyIndex];
                e.Use();
            }
            else if (e.keyCode == KeyCode.DownArrow)
            {
                if (historyIndex < commandHistory.Count - 1)
                {
                    historyIndex++;
                    inputCommand = commandHistory[historyIndex];
                    e.Use();
                }
                else
                {
                    inputCommand = "";
                    historyIndex = commandHistory.Count;
                }
            }
            else if (e.keyCode == KeyCode.Return)
            {
                SendCommand(inputCommand);
                inputCommand = "";
                GUI.FocusControl("InputField");
                e.Use();
            }
        }
    }

    private string ApplySyntaxColor(string text, bool isError)
    {
        if (isError)
            return $"<color=#ff5555>{text}</color>";
        if (text.Contains("warning", StringComparison.OrdinalIgnoreCase))
            return $"<color=#ffaa00>{text}</color>";
        if (text.Contains("error", StringComparison.OrdinalIgnoreCase))
            return $"<color=#ff3333>{text}</color>";
        if (text.Contains("success", StringComparison.OrdinalIgnoreCase))
            return $"<color=#55ff55>{text}</color>";
        return text;
    }

    private bool IsCommandAvailable(string cmd)
    {
        try
        {
            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Application.platform == RuntimePlatform.WindowsEditor ? "where" : "which",
                    Arguments = cmd,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                }
            };
            proc.Start();
            string result = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return !string.IsNullOrEmpty(result.Trim());
        }
        catch { return false; }
    }

    private void SaveSession() => File.WriteAllText(sessionFilePath, outputBuilder.ToString());

    private void LoadHistory()
    {
        if (File.Exists(historyFilePath))
            commandHistory = JsonUtility.FromJson<CommandHistory>(File.ReadAllText(historyFilePath)).history;
        else
            commandHistory = new List<string>();
        historyIndex = commandHistory.Count;
    }

    private void SaveHistory() =>
        File.WriteAllText(historyFilePath, JsonUtility.ToJson(new CommandHistory { history = commandHistory }));

    [Serializable]
    private class CommandHistory { public List<string> history; }
}

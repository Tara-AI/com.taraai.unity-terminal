// CrossPlatformTerminalWindow.cs
using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Threading.Tasks;

public class CrossPlatformTerminalWindow : EditorWindow
{
    private PtyProcess _pty;
    private StringBuilder outputBuilder = new StringBuilder();
    private Vector2 scrollPos;
    private string inputCommand = "";
    private List<string> commandHistory = new List<string>();
    private int historyIndex = -1;
    private bool scrollToBottom = true;
    private TerminalSettings settings;

    private static string sessionFilePath => Path.Combine(Application.persistentDataPath, "terminal_session.txt");
    private static string historyFilePath => Path.Combine(Application.persistentDataPath, "terminal_history.json");

    [MenuItem("Window/Terminal/Cross-Platform Shell (PTY)")]
    public static void ShowWindow() => GetWindow<CrossPlatformTerminalWindow>("Terminal");

    private void OnEnable()
    {
        settings = TerminalSettings.Load();
        EditorApplication.quitting += StopShell;
        StartShellAsync();
        LoadHistory();
    }

    private void OnDisable()
    {
        SaveSession();
        SaveHistory();
        StopShell();
        EditorApplication.quitting -= StopShell;
    }

    private async void StartShellAsync()
    {
        await Task.Delay(10); // let editor settle
        StartShell();
    }

    private void StartShell()
    {
        try
        {
            _pty?.Dispose();
            _pty = PtyProcess.Create();

            // Resolve shell executable
            string shell = ResolveShellExecutable();
            string args = "";
            if (shell == "pwsh") args = "-NoLogo -NoProfile";
            if (shell == "cmd.exe") args = "/K";
            if (shell == "/bin/bash" || shell == "bash") args = "-l";

            _pty.OnOutput += Pty_OnOutput;
            _pty.OnError += Pty_OnError;

            _pty.Start(shell, args);
            AppendOutput($"[Started shell: {shell}]\n", false);
            // Restore session
            if (File.Exists(sessionFilePath)) AppendOutput(File.ReadAllText(sessionFilePath), false);
        }
        catch (NotImplementedException nie)
        {
            AppendOutput("[PTY not fully implemented on this platform: " + nie.Message + "]", true);
            _pty?.Dispose();
            _pty = null;
        }
        catch (Exception ex)
        {
            AppendOutput("[Start shell failed] " + ex.Message, true);
            _pty?.Dispose();
            _pty = null;
        }
    }

    private string ResolveShellExecutable()
    {
        var pref = settings.preferredShell?.ToLower() ?? "auto";
        if (pref != "auto")
        {
            if (pref == "pwsh") return "pwsh";
            if (pref == "cmd" || pref == "cmd.exe") return "cmd.exe";
            if (pref == "bash" || pref == "zsh") return pref;
        }
        // Auto-detect
        if (IsCommandAvailable("pwsh")) return "pwsh";
#if UNITY_EDITOR_WIN
        return "cmd.exe";
#else
        if (IsCommandAvailable("bash")) return "bash";
        if (IsCommandAvailable("zsh")) return "zsh";
        return "/bin/sh";
#endif
    }

    private void Pty_OnError(string obj) => AppendOutput(obj, true);
    private void Pty_OnOutput(string obj) => AppendOutput(obj, false);

    private void StopShell()
    {
        try { _pty?.Kill(); } catch { }
        try { _pty?.Dispose(); } catch { }
        _pty = null;
    }

    private void SendCommand(string command)
    {
        if (string.IsNullOrEmpty(command)) return;
        if (_pty == null)
        {
            AppendOutput("[Shell not running]", true);
            return;
        }
        _pty.WriteAsync(command + "\n");
        AppendOutput($"<color=#{ColorUtility.ToHtmlStringRGB(settings.accentColor)}>> {command}</color>", false);
        commandHistory.Add(command);
        historyIndex = commandHistory.Count;
    }

    private void AppendOutput(string text, bool isError)
    {
        string colored = ApplySyntaxColor(text, isError);
        outputBuilder.Append(colored);
        scrollToBottom = true;
        Repaint();
    }

    private void OnGUI()
    {
        DrawModernToolbar();

        // Background and style
        var bg = new GUIStyle(EditorStyles.textArea) { richText = true, wordWrap = settings.wordWrap, fontSize = settings.fontSize };

        // font
        GUIStyle labelStyle = new GUIStyle(EditorStyles.label) { richText = true, fontSize = settings.fontSize };
        try
        {
            var dynFont = Font.CreateDynamicFontFromOSFont("Consolas", settings.fontSize);
            if (dynFont != null) labelStyle.font = dynFont;
        }
        catch { }

        // draw background box with rounded-ish look (approx)
        Rect full = GUILayoutUtility.GetRect(0, position.width, 0, position.height - 60);
        EditorGUI.DrawRect(full, settings.backgroundColor);
        GUILayout.Space(-full.height); // keep same rect region for scroll area

        scrollPos = EditorGUILayout.BeginScrollView(scrollPos, GUILayout.Height(full.height));
        GUILayout.Label(outputBuilder.ToString(), labelStyle, GUILayout.ExpandHeight(true));
        if (scrollToBottom && Event.current.type == EventType.Repaint)
        {
            scrollPos.y = Mathf.Infinity;
            scrollToBottom = false;
        }
        EditorGUILayout.EndScrollView();

        GUILayout.Space(6);
        EditorGUILayout.BeginHorizontal();
        GUI.SetNextControlName("InputField");
        inputCommand = EditorGUILayout.TextField(inputCommand, GUILayout.ExpandWidth(true));
        GUI.backgroundColor = settings.accentColor;
        if (GUILayout.Button("Run", GUILayout.Width(80)))
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
        if (GUILayout.Button("Clear", EditorStyles.toolbarButton)) { outputBuilder.Clear(); }
        if (GUILayout.Button("Restart", EditorStyles.toolbarButton)) { StopShell(); StartShell(); }
        GUI.backgroundColor = Color.white;
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("⚙ Settings", EditorStyles.toolbarButton)) TerminalSettingsWindow.ShowWindow(settings);
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
        if (isError) return $"<color=#ff5555>{text}</color>";
        if (text.IndexOf("warning", StringComparison.OrdinalIgnoreCase) >= 0) return $"<color=#ffaa00>{text}</color>";
        if (text.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0) return $"<color=#ff3333>{text}</color>";
        return text;
    }

    private bool IsCommandAvailable(string cmd)
    {
        try
        {
#if UNITY_EDITOR_WIN
            var p = new System.Diagnostics.ProcessStartInfo("where", cmd) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
#else
            var p = new System.Diagnostics.ProcessStartInfo("which", cmd) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
#endif
            var proc = System.Diagnostics.Process.Start(p);
            string outp = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return !string.IsNullOrEmpty(outp.Trim());
        }
        catch { return false; }
    }

    private void SaveSession() { try { File.WriteAllText(sessionFilePath, outputBuilder.ToString()); } catch { } }

    private void LoadHistory()
    {
        try
        {
            if (File.Exists(historyFilePath))
            {
                var ch = JsonUtility.FromJson<CommandHistory>(File.ReadAllText(historyFilePath));
                commandHistory = ch.history ?? new List<string>();
            }
        }
        catch { commandHistory = new List<string>(); }
        historyIndex = commandHistory.Count;
    }

    private void SaveHistory()
    {
        try { File.WriteAllText(historyFilePath, JsonUtility.ToJson(new CommandHistory { history = commandHistory })); } catch { }
    }

    [Serializable] class CommandHistory { public List<string> history; }
}

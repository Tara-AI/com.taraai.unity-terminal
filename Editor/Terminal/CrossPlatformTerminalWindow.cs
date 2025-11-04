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
    private string currentDirectory = "";

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

        shellProcess.OutputDataReceived += (s, e) => 
        { 
            if (!string.IsNullOrEmpty(e.Data))
            {
                if (e.Data.StartsWith("PATH:"))
                {
                    currentDirectory = e.Data.Substring(5).Trim();
                    return;
                }
                AppendOutput(e.Data, false);
            }
        };
        shellProcess.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) AppendOutput(e.Data, true); };

        try
        {
            shellProcess.Start();
            shellProcess.BeginOutputReadLine();
            shellProcess.BeginErrorReadLine();
            AppendOutput($"[Started {shellName} session]\n", false);
            
            // Set up command prompt initialization based on shell
            if (shellName.Contains("pwsh") || shellName.Contains("powershell"))
            {
                shellProcess.StandardInput.WriteLine("function prompt { Write-Host \"PATH:$(Get-Location)\"; \"PS > \" }");
            }
            else if (shellName.Contains("cmd"))
            {
                shellProcess.StandardInput.WriteLine("prompt PATH:$P$_$G");
            }
            else
            {
                shellProcess.StandardInput.WriteLine("PROMPT_COMMAND='echo \"PATH:$PWD\"'");
                shellProcess.StandardInput.WriteLine("PS1='\\$ '");
            }

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
            if (shellProcess != null)
            {
                if (!shellProcess.HasExited)
                {
                    shellProcess.CancelOutputRead();
                    shellProcess.CancelErrorRead();
                    shellProcess.Kill();
                }
                shellProcess.Dispose();
                shellProcess = null;
            }
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError($"Error stopping shell process: {ex.Message}");
        }
    }

    private void SendCommand(string command)
    {
        if (string.IsNullOrEmpty(command)) return;
        if (shellProcess == null || shellProcess.HasExited)
        {
            AppendOutput("Shell process not running.", true);
            return;
        }

        try
        {
            shellProcess.StandardInput.WriteLine(command);
            shellProcess.StandardInput.Flush();
        }
        catch (Exception ex)
        {
            AppendOutput($"Error sending command: {ex.Message}", true);
            // Attempt to restart shell if there was an error
            StopShell();
            StartShell();
            return;
        }
        
        // Don't echo the command since it's already visible in the prompt
        if (command.Trim().ToLower() == "clear" || command.Trim().ToLower() == "cls")
        {
            outputBuilder.Clear();
        }

        // Only add non-empty commands to history
        if (!string.IsNullOrWhiteSpace(command))
        {
            commandHistory.Add(command);
            historyIndex = commandHistory.Count;
        }
    }

    private void AppendOutput(string text, bool isError)
    {
        // Ensure thread safety when appending output
        lock (outputBuilder)
        {
            string colored = ApplySyntaxColor(text, isError);
            outputBuilder.AppendLine(colored);
            scrollToBottom = true;
            EditorApplication.delayCall += () => Repaint();
        }
    }

    private void OnGUI()
    {
        DrawModernToolbar();

        // Terminal window style
        var terminalStyle = new GUIStyle(EditorStyles.textArea)
        {
            richText = true,
            wordWrap = settings.wordWrap,
            normal = { textColor = Color.white },
            padding = new RectOffset(10, 10, 10, 10),
            margin = new RectOffset(0, 0, 0, 0)
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
        
        // Create a horizontal layout for the prompt and input
        EditorGUILayout.BeginHorizontal(GUILayout.ExpandWidth(true));
        
        // Display current directory prompt
        GUIStyle promptStyle = new GUIStyle(EditorStyles.label)
        {
            richText = true,
            fontSize = settings.fontSize,
            font = Font.CreateDynamicFontFromOSFont("Consolas", settings.fontSize),
            normal = { textColor = settings.accentColor }
        };
        
        string prompt = $"<color=#{ColorUtility.ToHtmlStringRGB(settings.accentColor)}>{currentDirectory}></color> ";
        float promptWidth = promptStyle.CalcSize(new GUIContent(prompt)).x;
        GUILayout.Label(prompt, promptStyle, GUILayout.Width(promptWidth));

        // Command input field with custom style
        GUI.SetNextControlName("InputField");
        var inputStyle = new GUIStyle(EditorStyles.textField)
        {
            fontSize = settings.fontSize,
            font = Font.CreateDynamicFontFromOSFont("Consolas", settings.fontSize),
            normal = { textColor = Color.white },
            margin = new RectOffset(0, 0, 2, 2)
        };
        
        string newInput = EditorGUILayout.TextField(inputCommand, inputStyle, GUILayout.ExpandWidth(true));
        if (newInput != inputCommand)
        {
            inputCommand = newInput;
            GUI.FocusControl("InputField");
        }
        
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
        // Handle keyboard navigation for command history and common shortcuts.
        // Use both Event.current and Event.current.rawType so we catch keys that
        // may be consumed by the text field internally.
        var e = Event.current;

        // Only act when the input field is focused — this prevents global
        // interception when other controls are used.
        if (GUI.GetNameOfFocusedControl() != "InputField")
            return;

        // Prefer rawType KeyDown to catch events even if IMGUI text field
        // consumes them first. Fall back to normal KeyDown as well.
        var isKeyDown = (e.rawType == EventType.KeyDown) || (e.type == EventType.KeyDown);
        if (!isKeyDown)
            return;

        bool handled = false;

        if (e.keyCode == KeyCode.UpArrow)
        {
            if (commandHistory.Count > 0 && historyIndex > 0)
            {
                historyIndex--;
                inputCommand = commandHistory[historyIndex];
            }
            else if (commandHistory.Count > 0 && historyIndex == -1)
            {
                historyIndex = commandHistory.Count - 1;
                inputCommand = commandHistory[historyIndex];
            }
            handled = true;
        }
        else if (e.keyCode == KeyCode.DownArrow)
        {
            if (commandHistory.Count == 0)
            {
                inputCommand = "";
                historyIndex = -1;
            }
            else if (historyIndex < 0)
            {
                inputCommand = "";
                historyIndex = commandHistory.Count;
            }
            else if (historyIndex < commandHistory.Count - 1)
            {
                historyIndex++;
                inputCommand = commandHistory[historyIndex];
            }
            else
            {
                inputCommand = "";
                historyIndex = commandHistory.Count;
            }
            handled = true;
        }
        else if (e.keyCode == KeyCode.Return && !e.shift)
        {
            // Execute command on Enter
            if (!string.IsNullOrWhiteSpace(inputCommand))
            {
                SendCommand(inputCommand);
                inputCommand = "";
            }
            handled = true;
        }
        else if (e.keyCode == KeyCode.L && e.control)
        {
            // Ctrl+L: clear
            outputBuilder.Clear();
            handled = true;
        }

        if (handled)
        {
            e.Use();
            GUI.FocusControl("InputField");
            Repaint();
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
    public class CommandHistory
    {
        public List<string> history; 
    }
}

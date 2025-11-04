# Example Usage

This sample adds a custom menu to open the terminal from code.

```csharp
using UnityEditor;

public static class TerminalMenuExample
{
    [MenuItem("Tools/Open PTY Terminal")]
    public static void OpenTerminal()
    {
        EditorApplication.ExecuteMenuItem("Window/Terminal/Cross-Platform Shell (PTY)");
    }
}
```

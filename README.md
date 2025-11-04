# Unity Terminal (PTY)

A **cross-platform terminal window for Unity Editor** supporting:
- Full interactive shell via PTY/ConPTY
- Persistent command history and session logs
- Modern theming inspired by Windows Terminal
- Shell auto-detection (`bash`, `zsh`, `cmd`, `pwsh`)
- Editor-only (safe for all build targets)

---

## 🧩 Installation

Add to your project via Unity’s **Package Manager** → **Add package from Git URL**:

```
https://github.com/TaraAI/com.taraai.unity-terminal.git
```

Unity will automatically fetch the package.

---

## 🚀 Usage

Open from the menu:
> **Window → Terminal**

Then type commands (bash, PowerShell, etc.) right in the Editor window.

---

## ⚙ Configuration

Click the ⚙ **Settings** button in the toolbar to adjust:
- Theme colors
- Font size
- Preferred shell
- Word wrap and persistence

Settings are stored per-user and persist across sessions.

---

## 💡 Requirements

- **Unity 2021.3+**
- **Windows 10 build 1809+** for ConPTY  
- **macOS / Linux** with `libc` (openpty)

---

## 📄 License

MIT © 2025 Tara AI

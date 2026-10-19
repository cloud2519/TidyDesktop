# TidyDesktop

<p align="center">
  <strong>Keep your Windows desktop clean and organized with shaded, tidy boxes.</strong><br>
  <em>바탕화면 아이콘을 깔끔한 상자로 정리해 주는 가볍고 안전한 데스크톱 유틸리티</em>
</p>

<p align="center">
  <a href="#features">Features</a> •
  <a href="#languages">Languages</a> •
  <a href="#privacy">Privacy</a> •
  <a href="#installation--usage">Usage</a> •
  <a href="#building-from-source">Build</a> •
  <a href="#license">License</a>
</p>

---

## 🌟 Overview

**TidyDesktop** groups real desktop icons into customizable "boxes" directly on your desktop. Because it manages your actual Windows desktop icons, all standard interactions—double-clicking, right-click context menus, and drag-and-drop—work exactly as you expect.

* **Non-destructive**: Your files and icons stay in their real desktop locations. You can revert to your original layout at any time with one click.
* **Completely Private**: 100% offline. Zero telemetry, zero cloud sync, and zero internet permissions required.
* **Single Portable Executable**: No installer needed. Runs cleanly on Windows 10 and Windows 11.

---

## ✨ Features

- **Organize in Boxes**: Create boxes, drag icons into or out of them, and roll them up when you want a minimal desktop.
- **Smart Auto-Sort**: Automatically categorize icons by kind (Web & Chat, Office, Pictures, Documents, Development, etc.).
- **Automatic File Rules**: Set rules to automatically move incoming files into specific folders or boxes based on extension, name, or size.
- **Recent Desktop Changes**: Live history tracking of new, deleted, or moved files on the desktop, with one-click **Undo** for rule actions.
- **Layout Snapshots**: Automated backups every 30 minutes and before any major change so you can restore any previous layout instantly.
- **Double-Click Hide**: Double-click an empty area of the desktop to quickly hide all boxes and icons, and double-click again to bring them back.
- **Deep Customization**: Adjust box background color, opacity, title, sorting order, and grid snapping (Hold `Shift` while dragging).

---

## 🌍 Languages

TidyDesktop automatically matches your Windows display language, or you can pick one from the tray menu:

- English (`en`)
- 한국어 (`ko`)
- 日本語 (`ja`)
- 简体中文 (`zh`)
- Español (`es`)
- Deutsch (`de`)
- Français (`fr`)
- Português (`pt`)
- Русский (`ru`)

> **Adding Custom Languages**: You can easily add more languages without rebuilding! Just copy `Languages/en.json`, translate the strings, and place your `.json` file inside a `Languages` folder next to `TidyDesktop.exe`.

---

## 🔒 Privacy & Safety

- All configurations and recent change logs are stored locally in `%APPDATA%\TidyDesktop`.
- No network requests are made.
- When tracking files that left the desktop, TidyDesktop only inspects file names to check where they moved—it never reads file contents.

---

## 🚀 Installation & Usage

1. Download the latest release from the [Releases](https://github.com/cloud2519/TidyDesktop/releases) page.
2. Unzip and run `TidyDesktop.exe`.
3. An icon will appear in the system tray (bottom-right next to the clock). Right-click it to access all settings, rules, and language options.

---

## 🛠️ Building from Source

### Prerequisites
- Windows 10 / 11 (x64)
- [.NET 10.0 SDK](https://dotnet.microsoft.com/)

### Build & Run
```bash
# Clone the repository
git clone https://github.com/cloud2519/TidyDesktop.git
cd TidyDesktop

# Build Debug
dotnet build

# Build standalone release package
pwsh ./publish.ps1
```
The standalone, single-file compressed ZIP release will be created under `dist/`.

---

## 🔏 Code Signing

Free code signing provided by [SignPath.io](https://signpath.io), using a certificate by the [SignPath Foundation](https://signpath.org).

---

## 📄 License

Distributed under the **MIT License**. See [`LICENSE`](LICENSE) for more details.


**Crafted with care by [404 Architect](https://www.404architect.com)**


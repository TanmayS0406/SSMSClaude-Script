# SQL Chat for SSMS

A dockable chat window for SQL Server Management Studio 21 / 22. It ships with an **echo stub** instead of
a real AI, so everything around the model (window, streaming, buttons, editor integration, installer) is
done and you only have to plug a provider in later.

Open it from **View > SQL Chat** in SSMS.

## What it does today

- Dockable / floating tool window that follows the SSMS light and dark themes.
- Streaming chat bubbles, Enter to send, Shift+Enter for a new line, Stop and Clear buttons.
- **Include selected / current query as context**: passes the selected text (or the whole script) of the
  active query window to the chat service.
- **Insert into query window**: puts an assistant reply (the first ```` ``` ```` code block, or the whole
  reply) into the active query window.

## Build

Needs Visual Studio 2022 or newer with the **Visual Studio extension development** workload (or the Build Tools).

```powershell
.\Build-Installer.ps1
```

This restores NuGet packages, builds the VSIX and creates `dist\SsmsChatExtension-Installer\`
(and a `.zip` of it) containing the `.vsix` and the install scripts.

You can also open `SsmsChatExtension.sln` in Visual Studio and build it there. `dotnet build` cannot build
a VSIX; use Visual Studio or MSBuild.

## Install into SSMS

1. Close SSMS.
2. Run `Install.cmd` from the `dist\SsmsChatExtension-Installer` folder (or `Install.cmd -Quiet` to skip the dialog).
3. Start SSMS and open **View > SQL Chat**.

The script runs the `VSIXInstaller.exe` that ships with SSMS (double-clicking a `.vsix` does not work for SSMS 21+).
Use `Uninstall.cmd` to remove it. If SSMS lives in a non-standard folder:
`Install-SsmsExtension.ps1 -SsmsPath "D:\...\Common7\IDE"`.

## Plugging in an AI later

Everything goes through one interface, `Services/IChatService.cs`:

```csharp
public sealed class MyAiChatService : IChatService
{
    public string Name => "My model";

    public async Task GetReplyAsync(ChatRequest request, Action<string> onToken, CancellationToken ct)
    {
        // request.Turns          -> conversation so far (last item = new user message)
        // request.EditorContext  -> selected / current SQL, or null
        // Call your API (HttpClient + streaming), and for each chunk of text:
        onToken(chunk);
    }
}
```

Then set it once, for example in `SsmsChatPackage.InitializeAsync`:

```csharp
ChatServiceProvider.Current = new MyAiChatService();
```

Notes for the real implementation:

- Do not hard-code API keys. Read them from Windows Credential Manager, DPAPI-protected settings, or an
  environment variable. An options page (`DialogPage`) is the usual place to ask for them.
- Query text may contain sensitive data. Decide what you send, and consider making "include query as context" opt-in.
- Do not auto-run generated SQL. The window only inserts it into the editor so you can review it.

## Project layout

| Path | Purpose |
| --- | --- |
| `src/SsmsChatExtension/SsmsChatPackage.cs` | Package entry point, registers the View menu command and the tool window |
| `src/SsmsChatExtension/VSCommandTable.vsct`, `PackageIds.cs` | Menu definition and the GUIDs / IDs it uses |
| `src/SsmsChatExtension/source.extension.vsixmanifest` | VSIX identity and the SSMS install target |
| `src/SsmsChatExtension/UI/` | WPF chat control, view model, tool window |
| `src/SsmsChatExtension/Services/` | `IChatService`, the echo stub and the message models |
| `src/SsmsChatExtension/Editor/SsmsEditor.cs` | Reads / writes the active query window through DTE |
| `installer/` | Install / uninstall scripts |
| `Build-Installer.ps1` | Builds and bundles the installer |

## Troubleshooting

- **No "SQL Chat" in the View menu:** run `Ssms.exe /updateconfiguration` once from the SSMS `Common7\IDE`
  folder to rebuild the extension cache, then restart SSMS.
- **Install fails:** open the newest `VSIXInstaller_*.log` in `%TEMP%`.
- **Extension misbehaves after startup:** run `Ssms.exe /log` and check `%APPDATA%\Microsoft\SSMS\...\ActivityLog.xml`.
- Microsoft has not published a stable SSMS extensibility SDK, so a future SSMS update may need small changes
  (the `Microsoft.VisualStudio.Ssms` install target in the manifest and the 17.9.x SDK package versions are the first places to look).

The publisher name ("Seth Business Solutions") lives in `source.extension.vsixmanifest` and `AssemblyInfo.cs`; change it as you like.

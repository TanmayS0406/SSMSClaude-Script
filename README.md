# SQL Chat for SSMS

A dockable chat window for SQL Server Management Studio 21 / 22, powered by **Claude** through your own
Claude account. Claude can understand the database you are connected to, run queries and (with your approval)
changes against it, edit the SQL files in your repository, and rewrite, open or execute query windows in SSMS.

Open it from **View > SQL Chat** in SSMS.

Claude is instructed to stay on topic: SQL Server, T-SQL, stored procedures, schema design, performance, SSMS and
the scripts in your repository. Other requests get a one-line polite refusal. (This is a system-prompt rule,
so treat it as a strong default rather than a security boundary.)

## What it does

- **Sign in with Claude**: the button at the top of the window runs the official Claude Code CLI sign-in
  (`claude auth login`) in a console window and your browser. The extension never sees or stores your Claude
  credentials; the CLI keeps them. **Sign out** runs `claude auth logout`.
- **Understands your database**: Claude gets tools to look at the server and database of the
  *active query window*: `get_connection_info`, `list_databases`, `list_objects`, `describe_object` (columns,
  keys, indexes, foreign keys, parameters, row count, full definition), `get_dependencies` (what an object uses
  and what uses it) and `run_query` (always read-only).
- **Stored procedures**: Claude reads a procedure's definition and dependencies before explaining it, writes
  new or changed procedures as `CREATE OR ALTER` in the editor or your repository, deploys them with `execute_sql`
  when you ask, and runs them with `EXEC` (subject to the approval setting below).
- **Diagrams**: ask for a flowchart of a procedure, an ER diagram of some tables or a call chain, and Claude
  calls `show_diagram`, which opens a Mermaid diagram in your browser with *Download SVG* and *Copy source*
  buttons. Pages are kept in `%LOCALAPPDATA%\SsmsChatExtension\diagrams`. Rendering loads Mermaid from
  cdn.jsdelivr.net, so it needs internet access (the source is always on the page).
- **Runs SQL for you**: `execute_sql` runs any script (INSERT / UPDATE / DELETE, CREATE / ALTER, EXEC, `GO`
  batches), commits it and gives Claude the results; `execute_query_window` presses F5 on the active query
  window so you see the results grid in SSMS. What happens depends on **Settings > Let Claude run SQL that
  changes the database**:
  - *Ask me before each change* (default): SSMS shows the SQL, the server / database and Claude's reason;
    nothing runs unless you click **Yes** (No is the default button).
  - *Off*: both tools refuse; Claude can still explore with `run_query` and put scripts in the editor.
  - *Allow without asking*: runs immediately. Only use this against development databases.
- **Edits your repository**: Claude works in the *repository folder* set in **Settings** and can create and edit
  files there (turn "Let Claude create and edit files..." off to make it ask instead, which in this window means
  the edit is refused). Read-only `git status/diff/log/show` are allowed; other shell commands are blocked.
- **Edits SSMS query windows**: `get_query_window`, `replace_query_window` (one Ctrl+Z undoes it) and
  `open_new_query_window` (connected like the active window). These only change the text; running it is
  `execute_query_window` (above).
- The conversation continues across messages (`--resume`); **Clear** starts a new Claude session.
- Tool activity shows in the reply as `› SSMS describe_object: dbo.Orders`, `› Edit: C:\repo\proc.sql`, and so on.
- Still there: streaming replies, Stop, *Include selected / current query as context*, *Insert into query window*, Copy.

## Requirements

- **Claude Code CLI** (`claude`) on the same machine: install it from https://claude.com/claude-code (native
  installer `~/.local/bin/claude.exe` or `npm i -g @anthropic-ai/claude-code`). The window finds it on `PATH`
  or in those folders; otherwise set the path in **Settings**.
- A Claude subscription (Pro / Max / Team / Enterprise) or Console account to sign in with.

## How it works

```
SQL Chat window ──stdin──▶ claude -p --output-format stream-json --resume <session>   (cwd = repository folder)
       ▲                         │  built-in tools: Read / Edit / Write / Grep ... inside the repository
       └──── streamed reply ─────┤
                                 └─ MCP over http://127.0.0.1:<random port>/mcp  (random bearer token)
                                        └─ "ssms" tools inside SSMS: database + query windows
```

- Every message runs `claude -p` in the repository folder with `--permission-mode acceptEdits` (or `default`),
  `--allowedTools mcp__ssms` plus read-only git, and an `--mcp-config` file that points to a small MCP server
  hosted inside SSMS. That file lives in `%LOCALAPPDATA%\SsmsChatExtension\mcp-<pid>.json`; the token changes
  every SSMS session.
- Database tools use the connection of the active query window, created by SSMS itself (Windows, SQL and
  Microsoft Entra logins all work). If that is not possible, set a **connection string override** in Settings
  (stored DPAPI-encrypted for your Windows user).
- `run_query` is read-only by design: statements that write, `EXEC`, transactions, `USE`, `OPENROWSET` and similar
  are rejected before they run, and every query also runs inside a transaction that is always rolled back,
  with a 30 second timeout and a row limit.
- `execute_sql` runs batch by batch (split on `GO`, like SSMS) with autocommit and a 5 minute timeout per batch,
  and stops at the first error, reporting which batches were already committed. The approval dialog is the
  safety net, so read it; if you need a hard guarantee, connect with a login that has only the permissions
  you want Claude to have.

## Settings

**Settings** (top of the window), saved to `%LOCALAPPDATA%\SsmsChatExtension\settings.json`:

| Setting | Meaning |
| --- | --- |
| Repository folder | Where Claude works and may edit files. Empty = a private scratch folder (`...\SsmsChatExtension\workspace`). |
| Let Claude create and edit files | `acceptEdits` permission mode. Off = edits are refused. |
| Let Claude run SQL that changes the database | Off / Ask me before each change (default) / Allow without asking. |
| Model | Optional `--model` value, for example `sonnet` or `opus`. Empty = CLI default. |
| Connection string override | Optional. Used for database tools instead of the active query window. |
| Claude Code CLI path | Optional full path to `claude.exe` / `claude.cmd`. |

You can also add a `CLAUDE.md` to the repository folder to give Claude project conventions (naming, style,
which databases are which); the CLI picks it up automatically.

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

## Using a different AI provider

Everything goes through one interface, `Services/IChatService.cs`; `ChatServiceProvider.Current` defaults to
`ClaudeCodeChatService`. `EchoChatService` is a no-AI stub that is handy for UI work:

```csharp
ChatServiceProvider.Current = new EchoChatService();
```

## Project layout

| Path | Purpose |
| --- | --- |
| `src/SsmsChatExtension/SsmsChatPackage.cs` | Package entry point, registers the View menu command and the tool window |
| `src/SsmsChatExtension/VSCommandTable.vsct`, `PackageIds.cs` | Menu definition and the GUIDs / IDs it uses |
| `src/SsmsChatExtension/source.extension.vsixmanifest` | VSIX identity and the SSMS install target |
| `src/SsmsChatExtension/UI/` | WPF chat control, view model, tool window |
| `src/SsmsChatExtension/Services/` | `IChatService`, the echo stub and the message models |
| `src/SsmsChatExtension/Services/Claude/` | Claude Code CLI wrapper (sign-in, status), the chat service, settings |
| `src/SsmsChatExtension/Mcp/` | Loopback MCP server and the `ssms` tools Claude calls |
| `src/SsmsChatExtension/Database/` | Active query window connection (via SSMS internals), the read-only query runner and the `GO`-aware executor |
| `src/SsmsChatExtension/Diagrams/` | Mermaid diagram pages opened by `show_diagram` |
| `src/SsmsChatExtension/Editor/SsmsEditor.cs` | Reads / writes / opens query windows through DTE |
| `installer/` | Install / uninstall scripts |
| `Build-Installer.ps1` | Builds and bundles the installer |

## Troubleshooting

- **"Claude Code CLI not found"**: install it, or set *Claude Code CLI path* in Settings, then click **Refresh**.
- **Sign-in console says the command is unknown**: update the CLI (`claude update`), or run `claude` once in a
  terminal and type `/login`, then click **Refresh**.
- **"SSMS database tools unavailable"** or *No database connection*: click into a connected query window
  before sending, or set a connection string override.

- **No "SQL Chat" in the View menu:** run `Ssms.exe /updateconfiguration` once from the SSMS `Common7\IDE`
  folder to rebuild the extension cache, then restart SSMS.
- **Install fails:** open the newest `VSIXInstaller_*.log` in `%TEMP%`.
- **Extension misbehaves after startup:** run `Ssms.exe /log` and check `%APPDATA%\Microsoft\SSMS\...\ActivityLog.xml`.
- The active-window connection comes from SSMS internals (`ServiceCache.ScriptFactory`, `UIConnectionInfoUtil`)
  read via reflection; if a future SSMS moves them, the connection string override still works.
- Microsoft has not published a stable SSMS extensibility SDK, so a future SSMS update may need small changes
  (the `Microsoft.VisualStudio.Ssms` install target in the manifest and the 17.9.x SDK package versions are the first places to look).

The publisher name ("Seth Business Solutions") lives in `source.extension.vsixmanifest` and `AssemblyInfo.cs`; change it as you like.

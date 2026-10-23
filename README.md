# SSMS Claude Chat

A dockable chat window for SQL Server Management Studio 21 / 22, powered by **Claude** through your own
Claude account. Claude can understand the database you are connected to, run queries and (with your approval)
changes against it, edit the SQL files in your repository, and rewrite, open or execute query windows in SSMS.

Open it from **View > SSMS Claude Chat** in SSMS.

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
- **One-click database diagram**: the **DB diagram** button lists the databases on the active connection's server;
  tick one or more (and optionally a schema) and it draws an ER diagram straight from the catalog - no AI call,
  no tokens used:
  - **Solid lines** are declared foreign keys.
  - **Dashed lines** are *inferred* relationships: a column such as `Invoices.CustomerId`, `Customer_ID` or
    `CustomerKey` is linked to the single-column primary key of a `Customer` / `Customers` / `tblCustomer` table
    when the types are compatible (all integer types match each other), and a distinctive key name such as
    `ProductCode` matches itself. Matches named after the table win, then the same schema, the same database,
    and finally the other selected databases - so links **across databases**, which SQL Server cannot declare,
    show up too. A column that is its table's whole primary key becomes a 1:1 link.
  - Each database gets its own colour; under the diagram the page lists every inferred relationship with the
    reason (review them - they are guesses) and the views / procedures / functions that read other databases.
  - Large selections are trimmed to 200 tables (tables with relationships first), and above 40 tables only key
    columns are shown. Claude can do the same with `show_database_diagram` (one or more databases).
- **Runs SQL for you**: `execute_sql` runs any script (INSERT / UPDATE / DELETE, CREATE / ALTER, EXEC, `GO`
  batches), commits it and gives Claude the results; `execute_query_window` presses F5 on the active query
  window so you see the results grid in SSMS. What happens depends on **Settings > Let Claude run SQL that
  changes the database**:
  - *Ask me before each change* (default): SSMS shows the SQL, the server / database and Claude's reason;
    nothing runs unless you click **Yes** (No is the default button).
  - *Off*: both tools refuse; Claude can still explore with `run_query` and put scripts in the editor.
  - *Allow without asking*: runs immediately. Only use this against development databases.
- **Object Explorer stays current**: after `execute_sql` creates, alters, drops or renames objects, the database
  node in Object Explorer and the IntelliSense cache are refreshed automatically (so new tables and procedures
  appear without pressing F5 there). After `execute_query_window`, the refresh happens once the query finishes.
  Claude can also call `refresh_object_explorer`. A database that is not expanded in Object Explorer simply
  loads fresh when you open it.
- **Edits your repository**: Claude works in the *repository folder* set in **Settings** and can create and edit
  files there. Read-only `git status/diff/log/show` are always allowed; what else Claude may do depends on
  **Settings > Claude permissions** (see below).
- **Edits SSMS query windows**: `get_query_window`, `replace_query_window` (one Ctrl+Z undoes it) and
  `open_new_query_window` (connected like the active window). These only change the text; running it is
  `execute_query_window` (above).
- **Attach files and screenshots**: **Attach...** picks files (SQL scripts, `.sqlplan` execution plans, logs, CSV,
  images, PDF; up to 10 per message, 20 MB each), or drop them on the input box. **Screenshot** opens the Windows
  snipping overlay and attaches what you select; Ctrl+V in the message box also attaches a copied image or copied
  files. Attachments are copied to `%LOCALAPPDATA%\SsmsChatExtension\attachments` (kept 30 days), that folder is
  passed to the CLI with `--add-dir`, and Claude reads each file with its Read tool, in every permission mode.
- **Chat history**: every conversation is saved (`%LOCALAPPDATA%\SsmsChatExtension\history`, one JSON file per
  chat). **History** lists them; opening one shows its messages and continues the same Claude session
  (`--resume`), so Claude still remembers it. **New chat** starts a fresh conversation; **Delete** removes one.
- **Model and effort**: pick the model (`opus`, `sonnet`, `haiku`, `fable`, or type any model id) and the effort
  level (`low` ... `max`) in Settings; the header shows what is active.
- Tool activity shows in the reply as `› SSMS describe_object: dbo.Orders`, `› Edit: C:\repo\proc.sql`, and so on.
- Still there: streaming replies, Stop, *Include selected / current query as context*, *Insert into query window*, Copy.

## Requirements

- **Claude Code CLI** (`claude`) on the same machine: install it from https://claude.com/claude-code (native
  installer `~/.local/bin/claude.exe` or `npm i -g @anthropic-ai/claude-code`). The window finds it on `PATH`
  or in those folders; otherwise set the path in **Settings**.
- A Claude subscription (Pro / Max / Team / Enterprise) or Console account to sign in with.

## How it works

```
SSMS Claude Chat window ──stdin──▶ claude -p --output-format stream-json --resume <session>   (cwd = repository folder)
       ▲                         │  built-in tools: Read / Edit / Write / Grep ... inside the repository
       └──── streamed reply ─────┤
                                 └─ MCP over http://127.0.0.1:<random port>/mcp  (random bearer token)
                                        └─ "ssms" tools inside SSMS: database + query windows
```

- Every message runs `claude -p` in the repository folder with the chosen `--permission-mode`, `--model` and
  `--effort`, `--allowedTools mcp__ssms` plus read-only git, the extension's instructions via
  `--append-system-prompt-file`, and an `--mcp-config` file that points to a small MCP server hosted inside SSMS. That file lives in `%LOCALAPPDATA%\SsmsChatExtension\mcp-<pid>.json`; the token changes
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
| Claude permissions | *Edit files in the repository folder* (`acceptEdits`, default) / *Auto mode* (`auto`: Claude Code's safety classifier approves actions, including shell commands, and blocks risky ones) / *Read-only* (`default`: anything that needs permission is refused). |
| Let Claude run SQL that changes the database | Off / Ask me before each change (default) / Allow without asking. Applies in every Claude permission mode. |
| Model | `--model`: `opus`, `sonnet`, `haiku`, `fable` or any model id. Empty = Claude Code's default. |
| Effort | `--effort`: `low`, `medium`, `high`, `xhigh`, `max`. Empty = the model's default. Available levels depend on the model. |
| Connection string override | Optional. Used for database tools instead of the active query window. |
| Claude Code CLI path | Optional full path to `claude.exe` / `claude.cmd`. |

You can also add a `CLAUDE.md` to the repository folder to give Claude project conventions (naming, style,
which databases are which); the CLI picks it up automatically.

## Install into SSMS

1. Download the latest setup from the SSMS Claude Chat website's **Download** button, or
   **[SsmsClaudeChat-Setup.exe](https://github.com/TanmayS0406/SSMSClaude-Script/releases/latest/download/SsmsClaudeChat-Setup.exe)**
   from GitHub (every version is on the [Releases page](https://github.com/TanmayS0406/SSMSClaude-Script/releases)).
2. Close SSMS and run the setup. It needs no admin rights.
3. Start SSMS and open **View > SSMS Claude Chat**.

Setup finds SSMS 21 / 22 and installs the extension with the `VSIXInstaller.exe` that ships with SSMS
(double-clicking a `.vsix` does not work for SSMS 21+). Running a newer setup upgrades in place. Remove it from
**Settings > Apps > Installed apps > SSMS Claude Chat**.

Setup options: `/SSMSPATH="D:\...\Release\Common7\IDE"` if SSMS lives in a non-standard folder,
`/VERYSILENT` for an unattended install.

Windows may show a SmartScreen warning because the setup is not code-signed: click **More info > Run anyway**.

## Build

Needs Visual Studio 2022 or newer with the **Visual Studio extension development** workload (or the Build Tools),
and [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`).

```powershell
.\Build-Installer.ps1                  # version from source.extension.vsixmanifest
.\Build-Installer.ps1 -Version 1.5.0   # stamps 1.5.0 into the manifest and assembly first
```

This restores NuGet packages, builds the VSIX and creates `dist\SsmsClaudeChat-Setup-<version>.exe`
(plus the `.vsix`).

You can also open `SsmsChatExtension.sln` in Visual Studio and build it there. `dotnet build` cannot build
a VSIX; use Visual Studio or MSBuild.

## Releases

`.github/workflows/build.yml` publishes a GitHub release on **every push to `main`**, so the download links above
always get the newest build. The version bumps automatically: the patch number of the newest `v*` tag goes up by
one (1.4.0, 1.4.1, 1.4.2, ...) and is stamped into the VSIX manifest and assembly before building.

- **New minor/major version:** raise the version in `source.extension.vsixmanifest` (e.g. to `1.5.0`) and push;
  that push releases exactly `1.5.0`, and later pushes continue from there.
- **Specific version:** push a tag (`git tag v2.0.0`, `git push origin v2.0.0`).
- Pull requests only build; the setup is under the run's *Artifacts*.

Pushes to `main` also upload to the `downloads` container of the `sbsbuilds1998` storage account, one folder per
version, plus `version.json` at the root pointing at the newest one:

```
downloads/
  version.json                       {"version": "1.4.7", "path": "1.4.7/SsmsClaudeChat-Setup-1.4.7.exe", ...}
  1.4.7/SsmsClaudeChat-Setup-1.4.7.exe
  1.4.7/SsmsClaudeChat-1.4.7.vsix
```

The website reads `version.json` and streams the setup from its own `/download` URL, so visitors never see the
storage address.
The upload is skipped until one of these is set (Settings > Secrets and variables > Actions):

| Name | Kind | Value |
| --- | --- | --- |
| `AZURE_STORAGE_SAS_TOKEN` | secret | SAS token for the `downloads` container with create + write rights (no leading `?`) |
| `AZURE_CLIENT_ID` | variable | Alternative to the SAS: App ID of an Entra app with a federated credential for `repo:TanmayS0406/SSMSClaude-Script:ref:refs/heads/main` and *Storage Blob Data Contributor* on the container; also needs `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID` |
| `AZURE_STORAGE_ACCOUNT` | variable | `sbsbuilds1998` (`AZURE_STORAGE_CONTAINER` optionally overrides `downloads`) |

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
| `src/SsmsChatExtension/Services/` | `IChatService`, the echo stub, the message models and the chat history store |
| `src/SsmsChatExtension/Services/Claude/` | Claude Code CLI wrapper (sign-in, status), the chat service, settings |
| `src/SsmsChatExtension/Mcp/` | Loopback MCP server and the `ssms` tools Claude calls |
| `src/SsmsChatExtension/Database/` | Active query window connection (via SSMS internals), the read-only query runner and the `GO`-aware executor |
| `src/SsmsChatExtension/Diagrams/` | Mermaid diagram pages opened by `show_diagram` |
| `src/SsmsChatExtension/Editor/SsmsEditor.cs` | Reads / writes / opens / executes query windows through DTE |
| `src/SsmsChatExtension/Editor/SsmsObjectExplorer.cs` | Refreshes Object Explorer and the IntelliSense cache after schema changes |
| `installer/SsmsClaudeChat.iss` | Inno Setup script for the setup `.exe` |
| `Build-Installer.ps1` | Builds the VSIX and the setup `.exe` |
| `.github/workflows/build.yml` | CI build and tag-triggered GitHub releases |

## Troubleshooting

- **"Claude Code CLI not found"**: install it, or set *Claude Code CLI path* in Settings, then click **Refresh**.
- **Sign-in console says the command is unknown**: update the CLI (`claude update`), or run `claude` once in a
  terminal and type `/login`, then click **Refresh**.
- **"SSMS database tools unavailable"** or *No database connection*: click into a connected query window
  before sending, or set a connection string override.

- **No "SSMS Claude Chat" in the View menu:** run `Ssms.exe /updateconfiguration` once from the SSMS `Common7\IDE`
  folder to rebuild the extension cache, then restart SSMS.
- **Install fails:** open the newest `VSIXInstaller_*.log` in `%TEMP%`, or run setup with `/LOG="%TEMP%\setup.log"`.
- **Extension misbehaves after startup:** run `Ssms.exe /log` and check `%APPDATA%\Microsoft\SSMS\...\ActivityLog.xml`.
- The active-window connection comes from SSMS internals (`ServiceCache.ScriptFactory`, `UIConnectionInfoUtil`)
  read via reflection; if a future SSMS moves them, the connection string override still works.
- Microsoft has not published a stable SSMS extensibility SDK, so a future SSMS update may need small changes
  (the `Microsoft.VisualStudio.Ssms` install target in the manifest and the 17.9.x SDK package versions are the first places to look).

The publisher name ("Tanmay Seth") lives in `source.extension.vsixmanifest`, `AssemblyInfo.cs` and `installer/SsmsClaudeChat.iss`.

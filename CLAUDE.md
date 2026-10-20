# Jarvis

A free, lightweight PDM for SolidWorks: a private GitHub repo with Git LFS file locking is the vault,
cloned to the same folder on every PC (the capstone team's is `C:\CapstoneVault`). Users never type Git
commands; the desktop app and the SolidWorks add-in do. No paid services, no AI at runtime. See `README.md`
(users, admins, developers) and `Capstone PDM Spec.md` (original spec).

Jarvis was called "Capstone PDM" up to 0.4.0 (exe `CapstonePDM.exe`, folders `CapstonePDM`). Migration code
moves old installs over: `Config/AppFolders` (settings, backups, logs), `Client/Services/LegacyInstall`
(exe, shortcuts), `ExplorerIntegration` (old menu keys), `Install-SolidWorksAddin.ps1` (old add-in; same
add-in GUID). Releases also carry `CapstonePDM.exe` and `CapstonePDM-SolidWorksAddin.zip` so 0.4.0 can update.
Keep these until nobody runs 0.4.0. The vault guard (`vault-template/.github`) still says Capstone PDM on
purpose: every vault compares it byte for byte and would ask for Update Vault Setup.

## Layout

- `src/Jarvis.Core` (netstandard2.0): **all vault logic**, no UI. Shared by both front ends; never duplicate logic in them.
  - `Vault/VaultService.cs`: every PDM command (check out/in, undo, Get Latest, freeze, reviews, milestones, index, snapshot).
  - `Vault/VaultCatalog.cs` (find/download vaults), `VaultCreator.cs` (New team vault: private repo + template), `VaultInvites.cs`; `Git/GitHubApi.cs` (REST calls with the saved GCM token), `Git/GitHubSignIn.cs` (first-run checks).
  - `Vault/FileTree.cs`: files-in-use tree rows from SolidWorks components (pure, unit-tested).
  - `Git/GitRunner.cs` runs git.exe; `GitErrorClassifier` maps stderr to typed `PdmException`s with user-facing messages.
  - `Updates/Updater.cs`: GitHub Releases + SHA256SUMS. `Notifications/`: Discord webhook.
- `src/Jarvis.Client` (.NET 8 WPF, MVVM-lite): vault view, history, 3D viewer, Settings, File Explorer menu, auto-update.
- `src/Jarvis.SwAddin` (.NET Framework 4.8, COM add-in): task pane (`VaultPane`), files-in-use tree (`FileTreeView`), SolidWorks API wrapper (`SolidWorksSession`), `AssemblySaveGuard`.
- `vault-template/`: files for a new vault, including the **vault guard** (`.github/workflows/vault-guard.yml` + `.github/vault-guard/guard.py`), a GitHub Action that undoes pushes breaking the rules. It's embedded into Core and installed by "Update Vault Setup".
- `tests/Jarvis.Core.Tests`: xUnit. Integration tests use real Git against a local bare repo (no LFS lock server, so locking is covered by parser tests).
- `scripts/`: Setup (teammates), Initialize-Vault, Publish-Client (zips + SHA256SUMS), Release-Client (GitHub release), Install-SolidWorksAddin.

## Build and test

```powershell
dotnet build Jarvis.sln     # the add-in needs SolidWorks installed (interop DLLs)
dotnet test Jarvis.sln
dotnet run --project src/Jarvis.Client
```

- The add-in builds against the **SolidWorks 2024** interop DLLs (`C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS`) with Embed Interop Types, and supports 2020+. Only use API calls that exist in 2020, or guard them with `RevisionNumber()`.
- To try add-in changes: **SolidWorks must be closed**, then `scripts\Install-SolidWorksAddin.ps1 -Source .\src\Jarvis.SwAddin\bin\Debug\net48` (Windows admin prompt). It locks the install folder so only administrators can change it.
- On this PC the user works in **SolidWorks 2026**: `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS (2)\SLDWORKS.exe`. The folder without "(2)" is 2024.
- Add-in log: `%LocalAppData%\Jarvis\addin.log`.

## Conventions

- PDM vocabulary in the UI: check out, check in, Get Latest, vault, version, revision, frozen. Plain, specific messages that say what to do next.
- SolidWorks API calls on the UI thread; Git work is async so SolidWorks never freezes.
- Admin = GitHub **Admin or Maintain** role on the vault repo (verified with the user's saved Git credential), never a list in a file.
- Shared state lives in the vault under `.pdm/` (frozen.json, checkin-requests.json, reviews.json, index/, preview/) and is checked by the vault guard.
- Commit messages: written to a temp file and committed with `git commit -F` (here-strings with quotes break). Push only when the user says so.
- Source files keep non-ASCII characters as `\uXXXX` escapes in C# strings.

## SolidWorks gotchas (learned the hard way)

- SolidWorks keeps a **shared** handle on every loaded file: other programs can read it, but an exclusive open fails. Check-in only needs to read, so it saves targets in place and never closes documents (`FileUtil.IsUnreadable`). Get Latest and Undo replace files, so they close and reopen.
- `GetRootComponent3().GetChildren()` returns nothing for a read-only assembly whose parts aren't loaded; `IAssemblyDoc.GetComponents(false)` still works. `FileTree` falls back automatically.
- Saving while editing a part inside a read-only assembly makes SolidWorks try to save the assembly (as a new file). `AssemblySaveGuard` hooks `FileSaveNotify` to save only the checked-out parts.
- STL export can write differently named files (per-instance, or `.STL` upper case); `ExportPreview` fixes the case and deletes strays.
- Changing user preferences (e.g. STL options) must restore the user's values afterwards.

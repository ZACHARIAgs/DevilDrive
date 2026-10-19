# Capstone PDM

A small PDM (product data management) tool for our SolidWorks files. It stops two people from editing the same file at once, keeps every version you check in, and posts check-outs and check-ins to Discord.

Under the hood the vault is a private GitHub repo using Git LFS file locking. You never need to type a Git command.

---

## For teammates

### One-time setup (about 10 minutes)

1. Get **CapstonePDM-Setup.zip** from an admin (GitHub Releases or the Discord pinned message) and unzip it anywhere.
2. Double-click **Setup.cmd**. It will:
   - install Git (a Windows permission prompt may appear),
   - ask for your **name**, **email** (the one on your GitHub account) and **GitHub username**,
   - download the vault to **`C:\CapstoneVault`** (a browser window opens so you can sign in to GitHub),
   - put a **Capstone PDM** shortcut on your Desktop.
3. Open **Capstone PDM** from the Desktop.

You must accept the GitHub collaborator invite for the vault repo before setup can download it. Ask an admin if you didn't get one.

### Daily workflow

| Step | What to do |
|---|---|
| 1. **Get Latest** | Start every session with this. It downloads everyone's newest check-ins. Files you're editing are never overwritten. |
| 2. **Check Out** | Select the files you'll edit, then click Check Out. This locks them so nobody else can edit them, and makes them writable. |
| 3. **Edit** | Open the files from `C:\CapstoneVault` in SolidWorks and save as usual. |
| 4. **Close** | Close the files in SolidWorks so the saved version is complete. |
| 5. **Check In** | Select the files, click Check In and write a real comment ("added mounting holes", not "update"). This saves a new version to the vault and releases your check-out. |

* **Check In with nothing selected** offers everything you can check in: your checked-out files plus new files.
* **Keep checked out** (in the Check In window) saves a version but lets you keep editing.
* **Undo Check Out** throws away your changes and releases the file. A backup of what you threw away is kept in `%LocalAppData%\CapstonePDM\Backups`.
* **New files** (state "New, not in vault yet") just need a Check In. There's no need to check them out first.
* **History** shows every version of a file. **Restore This Version** checks the file out, puts the old version back and checks it in as a new version. Nothing is lost, and the newer versions stay in the history.

### Inside SolidWorks (add-in)

Setup offers to install the **Capstone PDM add-in** (SolidWorks 2024 or newer; Windows asks for admin permission once). To add it later, close SolidWorks and double-click **Install SolidWorks Add-in.cmd** from the setup zip.

In SolidWorks, click the **padlock tab** in the Task Pane on the right. It shows the state of the document you're looking at, plus buttons:

* **Check Out**: for an assembly or drawing, you can tick the referenced parts to check out at the same time. The document becomes editable right away; there's no need to reopen it.
* **Check In…**: saves, closes, checks in and reopens the files for you, so you don't have to close them yourself. New parts used by an assembly are pre-ticked, so teammates never get missing references.
* **Undo Check Out**, **Get Latest**: as in the app. If files that need updating are open, it offers to close and reopen them.
* **Open Capstone PDM**: opens the full app (vault view, history, restore).

When you open a file someone else has checked out, a warning tells you who has it. If the add-in misbehaves, send the file `%LocalAppData%\CapstonePDM\addin.log` to an admin.

### What the states mean

| Dot | State | Meaning |
|---|---|---|
| 🟢 | Available | In the vault, nobody has it. Check it out to edit. |
| 🔵 | Checked out to you | Yours to edit. Check it in when done. |
| 🔴 | Checked out by *name* | Someone else is editing it. You can open it read-only. |
| 🟠 | Edited WITHOUT check-out! | You changed a file you hadn't checked out. Check it out now (your edits are kept), or Undo Check Out. |
| 🟠 | Edited (check in to save) | A non-CAD file (e.g. a spreadsheet) with changes. Just check it in. |
| 🟣 | New, not in vault yet | Only on your PC. Check it in to add it. |
| ⚪ | …newer version in vault | Click Get Latest. |

A **read-only file means you haven't checked it out.** Don't clear the read-only flag by hand.

### SolidWorks rules

1. Always work inside `C:\CapstoneVault`. Never open copies from Downloads, Discord or email.
2. Check out before editing.
3. **Never rename or move files in Windows Explorer.** Use Save As in SolidWorks with references updated, or ask whoever owns the assembly.
4. Check in at least daily, with a real comment.
5. Close the file in SolidWorks before checking in from the app. The add-in does this for you.
6. Follow the team's file naming scheme.

### When something goes wrong

Every error window has a **Copy** button. Paste the text into Discord when asking for help.

| Message | What it means / what to do |
|---|---|
| **Vault offline** | No internet, or GitHub is down. Nothing was changed. Try again when you're back online. The vault view still shows the last known status. |
| **Checked out by someone else** | *Name* has the file. Ask them to check it in. If they're unreachable and it's stale, ask an admin to **Break Lock**. |
| **Check-in not sent to the vault** | Your new version is saved on your PC but didn't reach GitHub (offline, or the vault refused it). Your files stay checked out to you. Fix the cause and click **Check In** again: it sends what's waiting. A yellow banner reminds you until it's sent. |
| **File in use** | The file is open in SolidWorks. Close it and retry. |
| **Not checked out** | You edited a file without checking it out. Check it out (your edits are kept), then check in. |
| **Conflict** | The vault has a newer version of a file you also changed. Usually someone broke your check-out. Nothing was overwritten. Copy your version somewhere safe, Undo Check Out, Get Latest, then sort it out with whoever made the other change. |
| **Sign-in or permission problem** | Git Credential Manager will ask you to sign in again. If it still fails, ask an admin to check you're a collaborator on the repo. |

---

## For admins

### First-time vault setup

1. Create a **private, empty** GitHub repo (no README). Add each teammate as a collaborator with **Write** access.
2. Edit `vault-template/pdm.json`:
   - `admins`: GitHub usernames allowed to break locks,
   - `team`: GitHub username → real name (shown in the app and Discord),
   - `discordWebhookUrl`: Discord → Server Settings → Integrations → Webhooks → New Webhook → Copy URL.
3. Run once, after installing Git (e.g. by running Setup.cmd yourself):
   ```powershell
   .\scripts\Initialize-Vault.ps1 -RepoUrl https://github.com/YOUR-TEAM/capstone-vault.git
   ```
4. In `scripts/Setup-CapstonePdm.ps1`, set `$DefaultRepoUrl` to the repo URL.
5. Build the teammate zip and share it (see *Publishing the app* below).

`pdm.json` lives in the vault, so you can change admins, names or the webhook later. Edit it in `C:\CapstoneVault` and check it in. Everyone picks up the change on their next Get Latest.

### Breaking a stale lock

Select the file, then click **Break Lock**. The button only appears for users listed in `admins`. The team is told in Discord. The person who had it loses their check-out. Any changes they hadn't checked in stay on their PC only, and they'll see a conflict or "edited without check-out" state.

GitHub decides whether a forced unlock is allowed. If it's refused, your GitHub role on the repo may need to be raised (Maintain or Admin).

### What's tracked

`.gitattributes` makes these **LFS + lockable** (read-only until checked out): `.sldprt .sldasm .slddrw .step .stp .pdf .dxf` (lower- and upper-case). Everything else (spreadsheets, notes, `pdm.json`) is a normal Git file: it's versioned but not lockable, so you edit it and check it in. `.gitignore` excludes SolidWorks temp files (`~$*`) and backups.

### GitHub LFS storage

GitHub limits free LFS **storage and bandwidth**, and every Get Latest that downloads CAD files counts toward bandwidth. **Check the current limits** in GitHub's docs before relying on the free tier. Watch usage under the repo's *Settings → Billing / Git LFS*. If you outgrow it, a paid data pack, GitLab, or a self-hosted Gitea are drop-in replacements: change the repo URL and nothing else.

---

## For developers

### Layout

```
src/CapstonePdm.Core/     netstandard2.0. All vault logic, no UI. Reused by the Phase 2 add-in.
  Git/                    GitRunner (runs git.exe), GitErrorClassifier (stderr → typed errors)
  Vault/                  VaultService (all PDM commands), models, Git output parsers
  Notifications/          Discord webhook
  Config/                 pdm.json (team) and settings.json (per PC)
  Errors/                 PdmException and subclasses, with user-facing messages
src/CapstonePdm.Client/   WPF app (.NET 8), MVVM-lite
src/CapstonePdm.SwAddin/  SolidWorks add-in (.NET Framework 4.8): task pane, reference-aware check-out/in
tests/CapstonePdm.Core.Tests/  xUnit: parsers, error classification, and integration tests against real Git
vault-template/           .gitattributes, .gitignore, pdm.json for a new vault
scripts/                  Setup (teammates), Initialize-Vault (admin), Publish-Client (build the zip)
```

### Build, test, run

Requires the .NET 8 SDK and Git.

```powershell
dotnet build CapstonePdm.sln
dotnet test CapstonePdm.sln
dotnet run --project src/CapstonePdm.Client
```

The integration tests use a local bare repo in place of GitHub. A local remote has no LFS lock server, so locking itself is only covered by parser and classifier tests. **Test check-out and check-in against a real GitHub test repo before the team relies on it.**

### Publishing the app

```powershell
.\scripts\Publish-Client.ps1
```

This produces `dist/CapstonePDM-Setup.zip`, containing a self-contained single-file `CapstonePDM.exe` (teammates don't need .NET), `Setup.cmd` and `Setup-CapstonePdm.ps1`. Attach it to a GitHub Release of this repo or pin it in Discord.

### How the commands map to Git

| Command | Git |
|---|---|
| Get Latest | `fetch`, refuse if an incoming file has local edits or is open in SolidWorks, then `merge --ff` (never rebase or stash) |
| Check Out | Get Latest, `lfs lock`, clear read-only, re-fetch to close the lock race |
| Check In | verify locks are ours, `add` + `commit` only the chosen files, Get Latest, `push` (merge and retry up to 3× if the vault moved), `lfs unlock` |
| Undo Check Out | back up, `checkout HEAD -- file`, `lfs unlock` |
| History / Restore | `log -- file` / `checkout <commit> -- file`, then Check In |
| Break Lock | `lfs unlock --force --id` |
| Vault view | `fetch`, `lfs locks --verify --json`, `status`, `ls-files`, `log --name-only` |

### SolidWorks add-in

* **Building** needs SolidWorks installed (it references the interop DLLs in `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS`; override with `/p:SolidWorksDir=...`). Without SolidWorks, build the other projects individually.
* **Version support:** built against the SolidWorks **2024** interop files with *Embed Interop Types* on, and it uses only API calls that exist in 2024, so one build loads in 2024 and newer. Don't add newer API calls without a version check (`ISldWorks.RevisionNumber()`).
* **Install for development:** close SolidWorks, build, then run `scripts\Install-SolidWorksAddin.ps1`. It copies the Release (or Debug) build to `C:\ProgramData\CapstonePDM\SolidWorksAddin` and registers it with RegAsm (admin prompt). Run with `-Uninstall` to remove it. Re-run after every rebuild.
* **Design:** `CapstonePdmAddin` (COM entry point and registration), `VaultPane` (WinForms task pane: watches the active document with a 1-second timer instead of SolidWorks events, then calls Core), `SolidWorksSession` (references, save, close/reopen so Git can replace files SolidWorks has open).
* **Dependencies:** SolidWorks is the host process, so binding redirects don't apply. `AssemblyResolver` loads System.Text.Json and friends from the add-in folder.
* **Log:** `%LocalAppData%\CapstonePDM\addin.log`.
* **Not yet done (Phase 2, part 2):** rename and move through the add-in with reference updates.

# Capstone PDM

A small PDM (product data management) tool for our SolidWorks files. It stops two people from editing the same file at once, keeps every version you check in, and posts check-outs and check-ins to Discord.

Under the hood the vault is a private GitHub repo using Git LFS file locking. You never need to type a Git command.

---

## For teammates

### One-time setup (about 10 minutes)

1. Get **CapstonePDM-Setup.zip** from an admin (GitHub Releases or the Discord pinned message) and unzip it anywhere.
2. Double-click **Setup.cmd**. It will:
   - install Git (a Windows permission prompt may appear),
   - ask for your **name** and **email** (the one on your GitHub account),
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
* **Details panel** (right side of the app): a **3D preview you can rotate** (drag to rotate, right-drag to pan, scroll to zoom; **Open large 3D view** for a bigger window) or a picture, its properties (part number, description, material, ...), what it **contains**, and **where it's used**. Check where-used before changing a part, so you know which assemblies it affects.
* **Search** finds files by name, part number, description or any other SolidWorks property.
* **Milestones…** lets you view the whole vault as it was at a milestone (e.g. "Design Review 1"). The vault folder switches to those versions, read-only, so assemblies open with the matching parts. Click **Return to Latest** when you're done.
* **History** shows every version of a file. **Restore This Version** checks the file out, puts the old version back and checks it in as a new version. Nothing is lost, and the newer versions stay in the history.

### From File Explorer

Right-click a file inside the vault and choose **Capstone PDM** (on Windows 11, click **Show more options** first). From there you can **Check Out**, **Check In…**, **Undo Check Out**, or **Show in Capstone PDM**. Selecting several files runs one command for all of them, with one check-in comment. Right-click empty space in a vault folder for **Get Latest**. You can turn the menu off in Settings.

### Inside SolidWorks (add-in)

Setup offers to install the **Capstone PDM add-in** (SolidWorks 2020 or newer, tested on 2024 and 2026; Windows asks for admin permission once). To add it later, close SolidWorks and double-click **Install SolidWorks Add-in.cmd** from the setup zip.

In SolidWorks, click the **safe icon** tab in the Task Pane on the right. It shows the state of the document you're looking at, plus buttons:

* **Check Out**: for an assembly or drawing, you can tick the referenced parts to check out at the same time. The document becomes editable right away; there's no need to reopen it.
* **Check In…**: saves, closes, checks in and reopens the files for you, so you don't have to close them yourself. New parts used by an assembly are pre-ticked, so teammates never get missing references.
* **Undo Check Out**, **Get Latest**: as in the app. If files that need updating are open, it offers to close and reopen them.
* **Rename / Move**: the safe way to rename or move a file. It finds every assembly and drawing that uses the file, updates their references, and checks everything in together.
* The card also shows the file's part number, which SolidWorks version saved it, and where it's used.
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
| ⚫ | Frozen by *name* | An admin froze it (e.g. released for production). Nobody can check it out or change it. Ask an admin if it needs to change. |
| ⚪ | …newer version in vault | Click Get Latest. |

A **read-only file means you haven't checked it out.** Don't clear the read-only flag by hand.

### SolidWorks rules

1. Always work inside `C:\CapstoneVault`. Never open copies from Downloads, Discord or email.
2. Check out before editing.
3. **Never rename or move files in Windows Explorer.** Use **Rename / Move** in the SolidWorks pane, which updates every assembly and drawing that uses the file.
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
   - `admins`: GitHub usernames with admin rights (break locks, freeze, request check-ins, milestones). People GitHub lists as **admins or maintainers of the repo** are always admins too,
   - `team`: GitHub username → real name (shown in the app and Discord),
   - `discordWebhookUrl`: Discord → Server Settings → Integrations → Webhooks → New Webhook → Copy URL.
3. Run once, after installing Git (e.g. by running Setup.cmd yourself):
   ```powershell
   .\scripts\Initialize-Vault.ps1 -RepoUrl https://github.com/YOUR-TEAM/capstone-vault.git
   ```
4. In `scripts/Setup-CapstonePdm.ps1`, set `$DefaultRepoUrl` to the repo URL.
5. Build the teammate zip and share it (see *Publishing the app* below).

`pdm.json` lives in the vault, so you can change admins, names or the webhook later. Edit it in `C:\CapstoneVault` and check it in. Everyone picks up the change on their next Get Latest.

### Who's an admin (and why typing a name doesn't work)

Capstone PDM asks GitHub which account each PC is signed in as, using the sign-in Git already saved, so nobody can claim to be someone else by typing a name. Settings shows the verified account. If a PC is offline, admin buttons stay hidden until it can check with GitHub.

### Stopping people from getting around the app with Git commands

The app's rules run on each PC, so someone typing raw Git commands could skip them. These steps move the protection onto GitHub, where nobody but you can change it:

1. **Install the vault guard.** In the app, click **Update Vault Setup** (it appears for admins whenever the guard is missing or was changed). This adds a GitHub Actions check (`.github/workflows/vault-guard.yml`) that runs after every push. It uses GitHub's record of who pushed and undoes any push where a non-admin changed `pdm.json`, the frozen list, `.github`, a frozen file, or someone else's check-in request. It posts to Discord when it does. It also warns about force pushes and files saved in a newer SolidWorks than the team's. Pushes it checks show under the repo's **Actions** tab.
2. **Repo roles.** On GitHub, go to **Settings**, then **Collaborators**. Give teammates **Write** and admins **Admin** (or Maintain).
3. **Protect `main`.** On GitHub, go to **Settings**, then **Branches**, then **Add rule** for `main`. Leave **Allow force pushes** and **Allow deletions** off, so history can't be rewritten or erased. On a private repo this needs GitHub Pro, which is free with the GitHub Student Developer Pack.
4. **Test file locks once with two accounts.** Have a teammate check out a file, then try to push a change to it from your account with Git. If GitHub accepts it, the lock was only enforced by the app; the vault guard still catches frozen files but not ordinary check-outs.

The guard is also why the app warns admins if `.github` is changed: it's the one thing a determined person could tamper with, and **Update Vault Setup** puts it back.

### Breaking a stale lock

Select the file, then click **Break Lock**. The button only appears for users listed in `admins`. The team is told in Discord. The person who had it loses their check-out. Any changes they hadn't checked in stay on their PC only, and they'll see a conflict or "edited without check-out" state.

GitHub decides whether a forced unlock is allowed. If it's refused, your GitHub role on the repo may need to be raised (Maintain or Admin).

### Requesting a check-in

Someone has a file checked out and isn't around, but a teammate needs it? Select the file and click **Request Check-In** (in the app or the SolidWorks pane), and add a short note.

* That person's own Capstone PDM, either the app or the SolidWorks add-in, checks in **their last saved version** and releases the file automatically. Both contact the vault every minute, so if their PC is on it usually happens within a minute. They get a message explaining what happened, and the team sees it in Discord.
* Changes they hadn't **saved** in SolidWorks aren't included, because those aren't on disk.
* If their PC is off, the request waits until they next open Capstone PDM or SolidWorks. If you can't wait, **Break Lock** releases the file right away, but their saved work then stays on their PC only.
* The file shows "check-in requested by *name*" until it's done.

### Freezing files (released for production)

Select files in the app (or open one in SolidWorks) and click **Freeze…**, then give a reason such as "Released for production, rev A". Only admins listed in `pdm.json` see Freeze and Unfreeze. In SolidWorks you can freeze an assembly's parts at the same time.

* A frozen file shows **Frozen by *name*** with the reason, for everyone, in the app and in SolidWorks. Opening one in SolidWorks shows a warning.
* **Nobody can check it out**, admins included. Admins click **Unfreeze** first if it really has to change, which also tells the team in Discord.
* How it's enforced: the freezing admin keeps the file's lock, so the app and GitHub's lock check refuse changes from anyone else. The list of frozen files (who, when, why) is stored in the vault as `.pdm/frozen.json`; don't edit it by hand.
* Files with changes, or checked out by someone else, can't be frozen until they're checked in.
* Non-admins also can't check in `pdm.json`, so nobody can make themselves admin through the app.

A teammate who uses raw Git commands instead of the app could get around these rules. Every version stays in the history, so anything can be restored.

### SolidWorks versions (version guard)

Set the team's SolidWorks version in `pdm.json`, for example `"solidWorksVersion": 2024`. Then the add-in:
* shows which SolidWorks last saved each file,
* **refuses to check in files saved in a newer version** than the team's,
* turns off Check Out and Check In when someone runs a newer SolidWorks than the team's, so they can still look at files but can't save them in a format the others can't open.

The desktop app reminds people at check-in, but only SolidWorks can read a file's version, so the add-in is what enforces this.

### Vault index (part numbers, properties, where-used)

The SolidWorks add-in records each file's custom properties, references and saved-in version whenever it's checked in from SolidWorks. Files added before the add-in, or checked in from the desktop app, aren't recorded until you click **Rebuild Index** in the SolidWorks pane, which reads every SolidWorks file in the vault (it takes a few minutes). The app recognizes common property names: `PartNo`/`Part Number`/`Number`, `Description`, `Material`.

### Milestones

Click **Milestones…**, type a name such as "Design Review 1" and an optional description, then click **Create**. This labels the vault exactly as it is now; only admins can create milestones. Everyone can view one later. The team gets a Discord notice.

### Revisions and drawing PDFs

Every freeze is a release with the next revision letter: A, B, C, and so on, skipping I, O, Q, S, X and Z as drawing standards do. The letter shows in the **Rev** column and the SolidWorks pane, and the sequence continues after unfreezing and refreezing. **Freezing a drawing from the SolidWorks pane also saves `<name> Rev X.pdf`** next to it, covering all sheets, and checks in and freezes that PDF with the same revision. Freezing from the desktop app gives the revision but no PDF, because making a PDF needs SolidWorks.

### SolidWorks versions

The PDM itself works with any version, and the add-in supports SolidWorks 2020 and newer. But **SolidWorks files can't be opened in an older SolidWorks than the one that last saved them.** Agree on one SolidWorks version for all vault files (the oldest one anyone uses), or a single save in a newer version locks everyone on the older one out of that file and every assembly that uses it.

### What's tracked

`.gitattributes` makes these **LFS + lockable** (read-only until checked out): `.sldprt .sldasm .slddrw .step .stp .pdf .dxf` (lower- and upper-case). Everything else (spreadsheets, notes, `pdm.json`) is a normal Git file: it's versioned but not lockable, so you edit it and check it in. `.gitignore` excludes SolidWorks temp files (`~$*`) and backups.

### Using it for another project (e.g. a club)

The tool isn't tied to the capstone. For a new team:

1. Create a new **private, empty** GitHub repo, e.g. `club-vault`, and add that team as collaborators.
2. Edit `vault-template/pdm.json` (admins, names, Discord webhook, `solidWorksVersion`), then run:
   ```powershell
   .\scripts\Initialize-Vault.ps1 -RepoUrl https://github.com/YOU/club-vault.git -VaultPath C:\ClubVault
   ```
   Pick a folder name that fits. It just has to be the same on every member's PC.
3. In `scripts/Setup-CapstonePdm.ps1`, set `$DefaultRepoUrl` and `$DefaultVaultPath` to the new repo and folder. Then run `scripts\Publish-Client.ps1` and share that zip with the club.
4. Click **Update Vault Setup** once in the app to turn on the vault guard.

If you're in both teams, keep both vaults on your PC and switch with **Settings → Vault folder**. The SolidWorks add-in and the File Explorer menu follow the switch. Each vault has its own admins, frozen files, milestones and Discord channel.

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
| Version guard | add-in reads `ISldWorks.VersionHistory` (file version 17000 = 2024, 19000 = 2026) and compares with `pdm.json` `solidWorksVersion` |
| Vault index | add-in writes `.pdm/index/<path>.json` (saved-in, direct references, custom properties) in the same commit as the check-in; Rebuild Index commits them all |
| Milestones | annotated tag `milestone/<name>` on the vault's latest commit; viewing = `checkout --detach <tag>`, Return to Latest = `checkout main` + Get Latest |
| Rename / Move | add-in: check out file + referencing files, move the file, `ReplaceReferencedDocument` on each, check in old path + new path + referencing files |
| Identity | `git credential fill` (no prompt) → GitHub API `/user` + repo permissions; admin = verified login in `pdm.json` admins, or repo admin/maintainer |
| Vault guard | GitHub Actions on push; guard script read from the commit before the push; undoes a rule-breaking push by restoring the touched files in one new commit |
| 3D previews | add-in exports coarse binary STL to `.pdm/preview/<path>.stl` (LFS) at check-in / Rebuild Index; app renders with HelixToolkit |
| File Explorer | per-user `HKCU\Software\Classes\*\shell\CapstonePDM` cascade with `AppliesTo` = vault path; `CapstonePDM.exe --explorer <command> <path>`; multi-select merged over a named pipe |
| Freeze / Unfreeze | admin keeps (or releases) the `lfs lock`; `.pdm/frozen.json` committed and pushed |
| Request Check-In | admin adds an entry to `.pdm/checkin-requests.json`; the owner's client sees it on its next vault check, commits the saved file plus the updated list, pushes and unlocks |
| Vault view | `fetch`, `lfs locks --verify --json`, `status`, `ls-files`, `log --name-only` |

### SolidWorks add-in

* **Building** needs SolidWorks installed (it references the interop DLLs in `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS`; override with `/p:SolidWorksDir=...`). Without SolidWorks, build the other projects individually.
* **Version support:** built against the SolidWorks **2024** interop files with *Embed Interop Types* on. It only uses API calls that already existed in SolidWorks 2017 (the multi-size task pane icon, with a fallback for older versions), so one build supports **2020 and newer**. It has been tested on 2024 and 2026. Don't add newer API calls without a version check (`ISldWorks.RevisionNumber()`; 2020 is 28, 2024 is 32, 2026 is 34).
* **Install for development:** close SolidWorks, build, then run `scripts\Install-SolidWorksAddin.ps1`. It copies the Release (or Debug) build to `C:\ProgramData\CapstonePDM\SolidWorksAddin` and registers it with RegAsm (admin prompt). Run with `-Uninstall` to remove it. Re-run after every rebuild.
* **Design:** `CapstonePdmAddin` (COM entry point and registration), `VaultPane` (WinForms task pane: watches the active document with a 1-second timer instead of SolidWorks events, then calls Core), `SolidWorksSession` (references, save, close/reopen so Git can replace files SolidWorks has open).
* **Dependencies:** SolidWorks is the host process, so binding redirects don't apply. `AssemblyResolver` loads System.Text.Json and friends from the add-in folder.
* **Log:** `%LocalAppData%\CapstonePDM\addin.log`.
* **Rename / Move** (`VaultPane.RenameAsync`) finds referencing files by reading them with `GetDocumentDependencies2` rather than trusting the index, and rolls back if any reference update fails.

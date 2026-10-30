# DevilDrive

A lightweight SolidWorks PDM for the Solar Devils team at ASU. It stops two people from editing the same file at once, keeps every version you check in, and posts check-outs and check-ins to Discord.

Under the hood the vault is a private GitHub repo using Git LFS file locking. You never need to type a Git command.

---

## For teammates

### Installing

Go to the **[latest release](https://github.com/ZACHARIAgs/DevilDrive/releases/latest)** and follow the instructions at the top of it.

### Daily workflow

| Step | What to do |
|---|---|
| 1. **Get Latest** | Start every session with this. It downloads everyone's newest check-ins. Files you're editing are never overwritten. |
| 2. **Check Out** | Select the files you'll edit, then click Check Out. This locks them so nobody else can edit them, and makes them writable. |
| 3. **Edit** | Open the files from `C:\SolarDevilsVault` in SolidWorks and save as usual. |
| 4. **Save** | Save in SolidWorks. Only saved changes get checked in; the files can stay open. |
| 5. **Check In** | Select the files, click Check In and write a real comment ("added mounting holes", not "update"). This saves a new version to the vault and releases your check-out. |

* **Check In with nothing selected** offers everything you can check in: your checked-out files plus new files.
* **Keep checked out** (in the Check In window) saves a version but lets you keep editing.
* **Undo Check Out** throws away your changes and releases the file. A backup of what you threw away is kept in `%LocalAppData%\DevilDrive\Backups`.
* **Theme / Dark Mode:** DevilDrive defaults to Dark mode; switch between Light and Dark mode anytime in **Settings**.
* **McMaster-Carr parts:** click **McMaster** in the toolbar, type the part number (e.g. 91251A540) and click **Get from McMaster**. DevilDrive opens the part's page; click McMaster's **Download**, choose **SOLIDWORKS** and save. DevilDrive picks the file up from your Downloads folder, puts it in `COTS\MCMASTER` and offers to check it in.
* **Part numbering:** if an admin has set it up (**Numbering** in the toolbar), checking in a new part from SolidWorks asks which group it belongs to (e.g. 200 Chassis), gives it the next free number, and offers to rename the file to match. The rename uses Rename / Move, so assemblies that use it are updated.
* **Material** comes from the part's SolidWorks material (Edit Material), recorded at every check-in; there's nothing to type.
* **Request Delete** (right-click): ask the admins to delete a file from the vault for everyone. Write why; they see the request in DevilDrive (and Discord) and either delete the file or reject the request. Changed your mind? **Withdraw Delete Request**.
* **Delete Local File** (right-click) removes a file you made in the vault folder but never checked in (it shows as *Not in vault*). Files that are in the vault can't be deleted this way. A copy is kept in the same backups folder.
* **Folders**: the tree on the left shows the vault's folders; click one to see the files in it, like File Explorer (a search or filter looks in its subfolders too). Right-click a folder for **New Folder…** (it reaches teammates once files are in it) or **Delete Folder** (empty ones). To move files, drag them onto a folder, or right-click → **Move to Folder…**. The move is checked in right away. Moving SolidWorks files also updates every assembly and drawing that uses them: DevilDrive starts SolidWorks (your team's version) in the background for that, so the first move takes a little longer. Files that are open in SolidWorks have to be closed first. Files you had checked out are still checked out to you afterwards. If an assembly or drawing that isn't downloaded on your PC might use the file (DevilDrive can't tell because it was never indexed), the move is refused until you download it.
* **Rename**: right-click a file and choose **Rename…**, then type the new name (the .SLDPRT/.SLDASM/.SLDDRW ending stays). It works like Move to Folder: assemblies and drawings that use the file are updated by SolidWorks in the background and checked in with the rename, so nothing breaks for anyone.
* **Export** (like Pack and Go): select files, right-click, **Export…**. DevilDrive copies them and every part and sub-assembly they use (all levels), and their drawings if you want, to a folder or a .zip file, and points the copies' assemblies and drawings at each other, so they open on any PC without the vault (to send to a sponsor, a machine shop or a teammate). Files that are only placeholders on your PC are downloaded first. SolidWorks is used in the background, as for Move to Folder. Nothing in the vault changes, and the copies aren't linked to it.
* The version you're on is shown at the bottom right of DevilDrive and at the bottom of the SolidWorks pane. The add-in updates along with the app: if its update was skipped (SolidWorks was open), DevilDrive offers it again at the next start.
* **New files** (state "New, not in vault yet") just need a Check In. There's no need to check them out first.
* **Details panel** (right side of the app): a **3D preview you can rotate** (drag to rotate, right-drag to pan, scroll to zoom; **Open large 3D view** for a bigger window) or a picture, its properties (part number, description, material), what it **contains**, and **where it's used**. Check where-used before changing a part so you know which assemblies it affects.
* **Search** finds files by name, part number, description or any other SolidWorks property.
* **Milestones** lets you view the whole vault as it was at a milestone (e.g. "Design Review 1"). Click **Return to Latest** when you're done.
* **Notify me when it's free:** right-click a file someone else has checked out and choose **Notify Me When It's Free** (or use the button in the SolidWorks pane). You get a message as soon as it's checked in.
* **Drawings follow their parts:** a drawing shows "may be out of date" when its part or assembly was checked in after the drawing was. Checking out a part offers its drawings too, so you can update them together.
* **Submit for Review…:** when a part, assembly or drawing is ready to release, select it and submit it with a note. Nobody can change it while it's in review. An admin approves it (it's frozen with the next revision letter) or rejects it with a reason.
* **History** shows every version of a file. **Restore This Version** checks the file out, puts the old version back and checks it in as a new version. Nothing is lost, and newer versions stay in history.

### Saving the vault's storage

* **Export warning:** checking in a new or changed STEP, PDF, DXF (or similar) asks first, because exports can be made again from the model and each check-in keeps a full copy. Check them in when you actually send them out, like a DXF for cutting.
* **Only download my folders:** in **Settings → What to download**, pick the folders you work on. Get Latest then only downloads files in those folders. Everything else shows as **not downloaded**: you can still see its 3D preview, and you can right-click it and choose **Download** to get it. In SolidWorks, if an assembly uses parts you haven't downloaded, the pane offers **Download missing parts**. Check Out always downloads the real file first.
* Other ways to save space: turn off *Save tessellation data* in SolidWorks (`Tools → Options → Performance`), delete unused configurations, and check in at the end of a work session rather than after every tweak.

### Inside SolidWorks (add-in)

In SolidWorks, click the **safe icon** tab in the Task Pane on the right. It shows the active document and, below it, **Files in use**: everything that document uses, without opening any of the parts.

* **Files in use tree:** component tree with dots (🟢 available, 🔵 checked out to you, 🔴 checked out by someone else, ⚫ frozen, 🟩 in review, 🟠 edited without check-out), version ratios (`4/4` or `7/10` orange), part numbers, and descriptions.
* **Tick boxes:** select multiple rows to check out or in together.
* **Check Out / Check In / Undo Check Out:** work directly from the pane.
* **Saving a part inside an assembly:** press Save as usual. The add-in saves only the checked-out parts you changed, leaving read-only assemblies intact.
* **Get Latest:** updates open files safely.
* **Rename / Move:** updates all referencing assemblies and drawings across the vault.
* **Export BOM (Excel):** saves a complete hierarchical parts list as a CSV file (quantities, part numbers, descriptions, materials, revisions).

### What the states mean

| Dot | State | Meaning |
|---|---|---|
| 🟢 | Available | In the vault, nobody has it. Check it out to edit. |
| 🔵 | Checked out to you | Yours to edit. Check it in when done. |
| 🔴 | Checked out by *name* | Someone else is editing it. You can open it read-only. |
| 🟠 | Edited WITHOUT check-out! | You changed a file you hadn't checked out. Check it out now (your edits are kept), or Undo Check Out. |
| 🟠 | Edited (check in to save) | A non-CAD file (e.g. a spreadsheet) with changes. Just check it in. |
| 🟣 | New, not in vault yet | Only on your PC. Check it in to add it. |
| 🟩 | In review (submitted by *name*) | Waiting for an admin to approve or reject it. |
| ⚫ | Frozen by *name* | An admin froze it (released for production). Nobody can change it. |
| ⚪ | …newer version in vault | Click Get Latest. |

### SolidWorks rules

1. **Always work inside `C:\SolarDevilsVault`.** Never open copies from Downloads, Discord or email.
2. **Check out before editing.**
3. **Never rename or move files in Windows Explorer.** Use **Rename** or **Move to Folder** in DevilDrive or the SolidWorks pane.
4. **Check in daily with a real comment.**
5. **Save the file in SolidWorks before checking in.**

### When something goes wrong

Every error window has a **Copy** button. Paste the text into Discord when asking for help.

| Message | What it means / what to do |
|---|---|
| **Vault offline** | No internet, or GitHub is down. Try again when back online. |
| **Checked out by someone else** | Someone has the file. Ask them to check it in or request a check-in. |
| **Check-in not sent to the vault** | Saved locally but push didn't reach GitHub. Click **Check In** again when connected. |
| **File in use** | Close the file in SolidWorks and retry Get Latest or Undo. |
| **Not checked out** | Check it out (edits are kept), then check in. |
| **Conflict** | Someone broke your lock. Copy your version safely, Undo, Get Latest, and reconcile. |
| **Sign-in or permission problem** | Sign in again via Git Credential Manager or ask an admin to check your Write access. |

---

<details>
<summary><h2>For admins</h2></summary>

### First-time vault setup

All in the app, no scripts or GitHub settings pages:

1. Install DevilDrive (`DevilDrive.exe` from the latest release). The welcome screen signs you in to GitHub.
2. Click **New team vault…** (on the welcome screen, or **Vaults** → **New team vault…**). Enter:
   - **Vault name**: e.g. "Solar Devils Vault",
   - **GitHub owner**: your account or the Solar Devils GitHub organization,
   - **SolidWorks version**: team's baseline version (default 2024),
   - **Vault folder**: `C:\SolarDevilsVault` (identical on every PC; keep outside OneDrive).
   
   The app creates a private GitHub repo with starting folders (`COTS`, `Sandbox`), vault rules (`.gitattributes`, `.gitignore`), team settings (`pdm.json`) and the vault guard.
3. **Add teammates:** click **Team** in the command bar. Type their GitHub usernames and click **Add to team** (grants Write access). Click **Copy welcome message** to paste into Discord or email with onboarding instructions.
4. Configure `pdm.json` in `C:\SolarDevilsVault`:
   - `team`: GitHub username → real name,
   - `discordWebhookUrl`: Discord channel webhook URL. Check it in via DevilDrive.

### Part numbering

Click **Numbering** in the toolbar. Set a prefix, digit count, and groups (e.g. 200 Chassis, 300 Suspension, 400 Powertrain, 500 Aero). Saved in `.pdm/numbering.json` so all teammates get it at next Get Latest.

### Deleting files from the vault

Only files that nothing else uses can be deleted. Teammates use **Request Delete** (right-click a file). Admins filter by **Delete requested** and choose:
- **Delete from Vault… (admin)**: deletes file, index entry, and 3D preview across the team in one check-in.
- **Reject Delete Request… (admin)**: keeps the file with a reason sent to Discord.

### Who's an admin

Admins are anyone GitHub lists as **Admin** or **Maintain** on the repository. Admin status cannot be spoofed by modifying local files.

### Server-side vault guard

Click **Update Vault Setup** if the guard needs installation or repair. It installs a GitHub Actions check (`.github/workflows/vault-guard.yml` + `guard.py`) that undoes any push breaking vault rules, protecting against manual Git commands. Protect the `main` branch on GitHub to disallow force pushes and deletions.

### Breaking a stale lock

Select the file and click **Break Lock** (admin only). The team is notified in Discord and the lock is released on GitHub.

### Freezing files (released for production)

Select files and click **Freeze** with a note (e.g. "Released for manufacture Rev A"). Freezing holds the Git LFS lock, increments the revision letter, and marks the file as read-only for everyone. Freezing a drawing from the SolidWorks pane automatically creates and freezes its PDF.

### Milestones

Click **Milestones** → **Create** (admin only) to tag the entire vault state (e.g. "PDR Release", "Competition Freeze").

### SolidWorks version guard

Set `"solidWorksVersion": 2024` in `pdm.json`. The add-in blocks check-ins of files saved in newer SolidWorks versions to prevent breaking compatibility for teammates.

### GitHub LFS storage

Monitor usage in GitHub under repository Settings → Billing & Plans → Git LFS data. GitHub provides 10 GB storage and 10 GB/month bandwidth on free accounts; 50 GB data packs can be added if needed for large teams.

</details>

---

<details>
<summary><h2>For developers</h2></summary>

### Repository Layout

```
src/Jarvis.Core/     netstandard2.0: all vault logic, Git/LFS operations, GitHub API, models
src/Jarvis.Client/   .NET 8 WPF desktop application (DevilDrive.exe)
src/Jarvis.SwAddin/  .NET Framework 4.8 SolidWorks COM add-in
tests/Jarvis.Core.Tests/  xUnit tests: unit and integration tests against local Git
vault-template/      Template files for new vaults (.gitattributes, .gitignore, pdm.json, vault-guard)
scripts/             Build and release scripts (Publish-Client.ps1, Release-Client.ps1, Setup-DevilDrive.ps1)
```

### Build, test, run

Requires .NET 8 SDK, .NET Framework 4.8 targeting pack, and Git.

```powershell
# Build entire solution
dotnet build Jarvis.sln

# Run test suite
dotnet test tests/Jarvis.Core.Tests

# Run DevilDrive desktop client
dotnet run --project src/Jarvis.Client
```

### Publishing the app

Run the automated publishing script from PowerShell:
```powershell
.\scripts\Publish-Client.ps1
```

Outputs generated in `dist/`:
- `dist/DevilDrive.exe` (Self-contained single-file executable)
- `dist/DevilDrive-SolidWorksAddin.zip` (Add-in package with register script)
- `dist/DevilDrive-Setup.zip` (Complete setup bundle with Setup.cmd)
- `dist/SHA256SUMS` (Checksums for verified auto-updates)

### Publishing a release

1. Update `<Version>` in `Directory.Build.props`, commit and push to your branch.
2. Run:
   ```powershell
   .\scripts\Release-Client.ps1 -Notes "Release notes here"
   ```
   This builds release artifacts, tags the commit, and creates a GitHub Release on `ZACHARIAgs/DevilDrive`.

### SolidWorks Add-in Development

* Built against SolidWorks 2024 interop assemblies with *Embed Interop Types* enabled; backwards compatible with SolidWorks 2020+.
* To install local debug build into SolidWorks:
  Close SolidWorks and run:
  ```powershell
  .\scripts\Install-SolidWorksAddin.ps1 -Source .\src\Jarvis.SwAddin\bin\Debug\net48
  ```
* Add-in logs are written to `%LocalAppData%\DevilDrive\addin.log`.

### Git & LFS Command Mapping

| Command | Action |
|---|---|
| Get Latest | `fetch`, refuse if an incoming file has local edits or is open in SolidWorks, then `merge --ff` (never rebase or stash) |
| Check Out | Get Latest, `lfs lock`, clear read-only, re-fetch to close the lock race |
| Check In | verify locks are ours, `add` + `commit` only the chosen files, Get Latest, `push` (merge and retry up to 3× if the vault moved), `lfs unlock` |
| Undo Check Out | back up, `checkout HEAD -- file`, `lfs unlock` |
| History / Restore | `log -- file` / `checkout <commit> -- file`, then Check In |
| Break Lock | `lfs unlock --force --id` |
| Version `7/10` | check-ins touching the file in `HEAD` / plus those in `HEAD..origin/main` (`git log --name-only`) |
| Version guard | add-in reads `ISldWorks.VersionHistory` (file version 17000 = 2024, 19000 = 2026) and compares with `pdm.json` `solidWorksVersion` |
| Vault index | add-in writes `.pdm/index/<path>.json` (saved-in, direct references, custom properties) in the same commit as the check-in (from the app: the app asks the add-in through SolidWorks' `GetAddInObject` and `JarvisAddin.RecordFiles`); after each SolidWorks check-in `IndexCatchUp` picks up to 10 files with a missing or outdated record or no preview |
| Milestones | annotated tag `milestone/<name>` on the vault's latest commit; viewing = `checkout --detach <tag>`, Return to Latest = `checkout main` + Get Latest |
| Export | `VaultExport.Collect` (Core): the chosen files plus their references, all levels, read by SolidWorks (`GetDocumentDependencies2`; the index is used first to download placeholders) and drawings from the index's where-used; then `VaultImport.CopyAndRepoint` into the export folder (zipped from a temp folder for a .zip) |
| Rename / Move, Rename, Move to Folder | `VaultService.PlanMoveAsync` / `MoveAsync` (Core): find referencing files with an `IReferenceEditor` (the add-in's SolidWorks, or the app's `SolidWorksAutomation`, which drives the team's SolidWorks over COM, started hidden if needed), check out, move, `ReplaceReferencedDocument` on each, move index entries and previews, check in old paths + new paths + referencing files; everything is put back if a reference can't be updated |
| Import files | `VaultImport.CopyAndRepoint` (Core): copy into the vault under new names, `ReplaceReferencedDocument` on each copy so it uses the other copies (copies are deleted again if one fails); then the add-in writes PartNumber/Description, makes records and previews, and checks the copies in as one commit |
| Identity | `git credential fill` (no prompt) → GitHub API `/user` + repo permissions; admin = verified login in `pdm.json` admins, or repo admin/maintainer |
| Vault guard | GitHub Actions on push; guard script read from the commit before the push; undoes a rule-breaking push by restoring the touched files in one new commit |
| 3D previews | add-in exports coarse binary STL to `.pdm/preview/<path>.stl` (LFS) at check-in (and the catch-up); over 12,000 triangles (usually assemblies with detailed hardware) `PreviewSimplifier` merges nearby points until it fits (about 600 KB at most); app renders with HelixToolkit |
| Freeze / Unfreeze | admin keeps (or releases) the `lfs lock`; `.pdm/frozen.json` committed and pushed |
| Request Check-In | admin adds an entry to `.pdm/checkin-requests.json`; the owner's client sees it on its next vault check, commits the saved file plus the updated list, pushes and unlocks |
| Vault view | `fetch`, `lfs locks --verify --json`, `status`, `ls-files`, `log --name-only` |

</details>

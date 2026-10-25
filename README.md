# Jarvis

A small PDM (product data management) tool for our SolidWorks files. It stops two people from editing the same file at once, keeps every version you check in, and posts check-outs and check-ins to Discord.

Under the hood the vault is a private GitHub repo using Git LFS file locking. You never need to type a Git command.

---

## For teammates

### Installing

Go to the **[latest release](https://github.com/aturne59-stack/jarvis/releases/latest)** and follow the instructions at the top of it.

### Daily workflow

| Step | What to do |
|---|---|
| 1. **Get Latest** | Start every session with this. It downloads everyone's newest check-ins. Files you're editing are never overwritten. |
| 2. **Check Out** | Select the files you'll edit, then click Check Out. This locks them so nobody else can edit them, and makes them writable. |
| 3. **Edit** | Open the files from `C:\CapstoneVault` in SolidWorks and save as usual. |
| 4. **Save** | Save in SolidWorks. Only saved changes get checked in; the files can stay open. |
| 5. **Check In** | Select the files, click Check In and write a real comment ("added mounting holes", not "update"). This saves a new version to the vault and releases your check-out. |

* **Check In with nothing selected** offers everything you can check in: your checked-out files plus new files.
* **Keep checked out** (in the Check In window) saves a version but lets you keep editing.
* **Undo Check Out** throws away your changes and releases the file. A backup of what you threw away is kept in `%LocalAppData%\Jarvis\Backups`.
* **McMaster-Carr parts:** click **McMaster** in the toolbar, type the part number (e.g. 91251A540) and click **Get from McMaster**. Jarvis opens the part's page; click McMaster's **Download**, choose **SOLIDWORKS** and save. Jarvis picks the file up from your Downloads folder, puts it in `COTS\MCMASTER` and offers to check it in. (McMaster's own API needs an approved account, so Jarvis uses their normal download.)
* **Part numbering:** if an admin has set it up (**Numbering** in the toolbar), checking in a new part from SolidWorks asks which group it belongs to (e.g. 200 Turret), gives it the next free number (TLT-203, saved in its PartNumber property) and offers to rename the file to match (TLT-203_Bearing_Housing.SLDPRT). The rename uses Rename / Move, so assemblies that use it are updated.
* **Material** comes from the part's SolidWorks material (Edit Material), recorded at every check-in; there's nothing to type.
* **Request Delete** (right-click): ask the admins to delete a file from the vault for everyone. Write why; they see the request in Jarvis (and Discord) and either delete the file or reject the request. Changed your mind? **Withdraw Delete Request**.
* **Delete Local File** (right-click) removes a file you made in the vault folder but never checked in (it shows as *Not in vault*). Files that are in the vault can't be deleted this way. A copy is kept in the same backups folder.
* **Folders**: the tree on the left shows the vault's folders; click one to see the files in it, like File Explorer (a search or filter looks in its subfolders too). Right-click a folder for **New Folder…** (it reaches teammates once files are in it) or **Delete Folder** (empty ones). To move files, drag them onto a folder, or right-click → **Move to Folder…**. The move is checked in right away. Moving SolidWorks files also updates every assembly and drawing that uses them: Jarvis starts SolidWorks (your team's version) in the background for that, so the first move takes a little longer. Files that are open in SolidWorks have to be closed first. Files you had checked out are still checked out to you afterwards. If an assembly or drawing that isn't downloaded on your PC might use the file (Jarvis can't tell because it was never indexed), the move is refused until you download it.
* The version you're on is shown at the bottom right of Jarvis and at the bottom of the SolidWorks pane. The add-in updates along with the app: if its update was skipped (SolidWorks was open), Jarvis offers it again at the next start.
* **New files** (state "New, not in vault yet") just need a Check In. There's no need to check them out first.
* **Details panel** (right side of the app): a **3D preview you can rotate** (drag to rotate, right-drag to pan, scroll to zoom; **Open large 3D view** for a bigger window) or a picture, its properties (part number, description, material, ...), what it **contains**, and **where it's used**. Check where-used before changing a part, so you know which assemblies it affects.
* **Search** finds files by name, part number, description or any other SolidWorks property.
* **Milestones** lets you view the whole vault as it was at a milestone (e.g. "Design Review 1"). The vault folder switches to those versions, read-only, so assemblies open with the matching parts. Click **Return to Latest** when you're done.
* **Notify me when it's free:** right-click a file someone else has checked out and choose **Notify Me When It's Free** (or use the button in the SolidWorks pane). You get a message as soon as it's checked in.
* **Drawings follow their parts:** a drawing shows "may be out of date" when its part or assembly was checked in after the drawing was. Checking out a part offers its drawings too, so you can update them together.
* **Submit for Review…:** when a part, assembly or drawing is ready to release, select it and submit it with a note. Nobody can change it while it's in review. An admin approves it (it's frozen with the next revision letter) or rejects it with a reason. You can **Withdraw from Review** to keep working on it.
* **History** shows every version of a file. **Restore This Version** checks the file out, puts the old version back and checks it in as a new version. Nothing is lost, and the newer versions stay in the history.

### Saving the vault's storage

The vault lives on GitHub's free plan: **10 GB of storage** (every version of every file ever checked in counts) and **10 GB of downloads a month**, shared by every repo of the GitHub account or organization that owns the vault. Check usage on GitHub under **Settings → Billing and licensing**. Two features help:

* **Export warning:** checking in a new or changed STEP, PDF, DXF (or similar) asks first, because exports can be made again from the model and each check-in keeps a full copy. Check them in when you actually send them out, like a DXF for cutting. Admins can turn it off with `"warnOnExports": false` in `pdm.json`.
* **Only download my folders:** in **Settings → What to download**, pick the folders you work on. Get Latest then only downloads files in those folders. Everything else shows as **not downloaded**: you can still see its 3D preview, and you can right-click it and choose **Download** to get it. In SolidWorks, if an assembly uses parts you haven't downloaded, the pane offers **Download missing parts**. Check Out always downloads the real file first.

Other ways to save space: turn off *Save tessellation data* (Tools → Options → Performance), delete unused configurations and embedded images, and check in at the end of a work session rather than after every tweak.

### Inside SolidWorks (add-in)

Setup offers to install the **Jarvis add-in** (SolidWorks 2020 or newer, tested on 2024 and 2026; Windows asks for admin permission once). To add it later, close SolidWorks and double-click **Install SolidWorks Add-in.cmd** from the setup zip.

In SolidWorks, click the **safe icon** tab in the Task Pane on the right. It shows the document you're looking at and, below it, **Files in use**: everything that document uses, without opening any of the parts.

* **Files in use tree.** For an assembly, every component instance at every level, like the FeatureManager tree (`bracket<1>`, `bracket<2>`); click the arrow to open a subassembly. For a drawing, the models it shows. Each row has:
  * a **dot**: 🟢 available, 🔵 checked out to you, 🔴 checked out by someone else (hover for who), ⚫ frozen, 🟩 in review, 🟠 edited without check-out;
  * the **version as on-your-PC/in-the-vault**, e.g. `4/4` (up to date) or `7/10` (**orange**: the vault has 3 newer check-ins, click Get Latest);
  * the **part number and description** (the file name when there's no part number).
  Suppressed components are greyed out, and files **outside the vault** are flagged in orange (teammates won't have them). The tree updates when you switch, open, close or save a document.
* **Tick boxes.** Tick rows to work on several files at once: click the box, Ctrl-click a row, or Shift-click to tick a range. Right-click a subassembly for **Tick this and everything inside**.
* **Check Out / Check In / Undo Check Out** work on the whole tree, and their labels say how many files they apply to ("Check Out… (5 available)", "Check In (2 ticked)").
  * With rows ticked, the ticked files are checked out or in straight away. Check In only asks for the comment; Check Out tells you which ticked files someone else has and checks out the rest.
  * With nothing ticked, a list of everything in the tree you can check out (or in) opens.
  * **Check Out** first gets the latest versions if the vault has newer ones, so you never edit an old copy. The files become editable right away.
  * **Check In** saves the files being checked in and checks them in. **You don't need to close anything**, including the assembly you edited a part in, even if that assembly isn't checked out.
  * **New files:** the first time a SolidWorks file is checked in, a window asks for its **description** (saved into the file's `Description` property, so it shows everywhere).
* **Saving a part inside an assembly that isn't checked out:** press Save as usual. The add-in saves the checked-out parts you changed and leaves the read-only assembly alone, instead of SolidWorks asking to save the assembly as a new file.
* **Get Latest**: as in the app. If files that need updating are open, it offers to close and reopen them.
* **Open a part from an assembly:** right-click a part or sub-assembly under Files in use and choose **Open**.
* **Use an older version (this PC only):** right-click a row under Files in use and choose **Use an older version…**, then pick a version. Your PC loads that version (read-only) and reopens the assembly with it, while the rest of the assembly stays at the latest; the vault and your teammates are not affected. The row shows the version in purple (e.g. `3/5`). Get Latest keeps your choice. Right-click again and choose **Return to the latest version** to switch back (the app's right-click menu has **Return to Latest Version** too). A file on an older version can't be checked out or in until it's back on the latest. To make an old version the newest one for everyone, use **History → Restore This Version** in the app.
* **Rename / Move**: the safe way to rename or move a file. It finds every assembly and drawing that uses the file, updates their references, and checks everything in together.
* **Import files…**: brings existing SolidWorks files into the vault, e.g. a new member's project. Pick their folder; Jarvis lists every part, assembly and drawing in it (plus files they use from other folders). In the table, pick each file's group (if the vault has part numbering) and edit its name and description; the new file names are shown. Choose the vault folder (Sandbox by default) and click **Import and Check In**: the files are copied in under the new names, every assembly and drawing is pointed at the copies, part numbers and descriptions are saved, and it's all checked in as one change with 3D previews. The original files aren't changed. Close the originals in SolidWorks first.
* **Submit for review**, and for admins **Approve & release** / **Reject**: see *Review before release* below. Approving a drawing here also saves its PDF.
* **Export BOM (Excel)**: for an assembly, saves a parts list as a CSV file that opens in Excel: every part at every level with its quantity, part number, description, material and released revision. Suppressed and "excluded from BOM" parts are left out.
* **Notify me when it's free**: for a file someone else has checked out.
* The card at the top shows the active document's state, its version (`7/10`), part number, which SolidWorks version saved it, where it's used, and whether a drawing may be out of date.
* **Open Jarvis**: opens the full app (vault view, history, restore).

When you open a file someone else has checked out, a warning tells you who has it. If the add-in misbehaves, send the file `%LocalAppData%\Jarvis\addin.log` to an admin.

### What the states mean

| Dot | State | Meaning |
|---|---|---|
| 🟢 | Available | In the vault, nobody has it. Check it out to edit. |
| 🔵 | Checked out to you | Yours to edit. Check it in when done. |
| 🔴 | Checked out by *name* | Someone else is editing it. You can open it read-only. |
| 🟠 | Edited WITHOUT check-out! | You changed a file you hadn't checked out. Check it out now (your edits are kept), or Undo Check Out. |
| 🟠 | Edited (check in to save) | A non-CAD file (e.g. a spreadsheet) with changes. Just check it in. |
| 🟣 | New, not in vault yet | Only on your PC. Check it in to add it. |
| 🟩 | In review (submitted by *name*) | Waiting for an admin to approve or reject it. Nobody can change it meanwhile. |
| ⚫ | Frozen by *name* | An admin froze it (e.g. released for production). Nobody can check it out or change it. Ask an admin if it needs to change. |
| ⚪ | …newer version in vault | Click Get Latest. |

A **read-only file means you haven't checked it out.** Don't clear the read-only flag by hand.

### SolidWorks rules

1. Always work inside `C:\CapstoneVault`. Never open copies from Downloads, Discord or email.
2. Check out before editing.
3. **Never rename or move files in Windows Explorer.** Use **Move to Folder** in Jarvis or **Rename / Move** in the SolidWorks pane: both update every assembly and drawing that uses the file.
4. Check in at least daily, with a real comment.
5. Save the file in SolidWorks before checking in from the app (only saved changes are checked in). The add-in saves it for you.
6. Follow the team's file naming scheme.

### When something goes wrong

Every error window has a **Copy** button. Paste the text into Discord when asking for help.

| Message | What it means / what to do |
|---|---|
| **Vault offline** | No internet, or GitHub is down. Nothing was changed. Try again when you're back online. The vault view still shows the last known status. |
| **Checked out by someone else** | *Name* has the file. Ask them to check it in. If they're unreachable and it's stale, ask an admin to **Break Lock**. |
| **Check-in not sent to the vault** | Your new version is saved on your PC but didn't reach GitHub (offline, or the vault refused it). Your files stay checked out to you. Fix the cause and click **Check In** again: it sends what's waiting. A yellow banner reminds you until it's sent. |
| **File in use** | Get Latest or Undo needs to replace a file that's open in SolidWorks. Close it (the add-in offers to close and reopen it) and retry. Check-in works with files open. |
| **Not checked out** | You edited a file without checking it out. Check it out (your edits are kept), then check in. |
| **Conflict** | The vault has a newer version of a file you also changed. Usually someone broke your check-out. Nothing was overwritten. Copy your version somewhere safe, Undo Check Out, Get Latest, then sort it out with whoever made the other change. |
| **Sign-in or permission problem** | Git Credential Manager will ask you to sign in again. If it still fails, ask an admin to check you're a collaborator on the repo. |

---

## For admins

### First-time vault setup

All in the app, no scripts or GitHub settings pages:

1. Install Jarvis (download `Jarvis.exe` from the latest release, open it, click **Install Jarvis**). The welcome screen signs you in to GitHub.
2. Click **New team vault…** (on the welcome screen, or **Vaults** → **New team vault…**). Enter:
   - the **vault name** (e.g. "Formula SAE Vault"),
   - the **GitHub owner**: your account, or an organization you can create repos in (with an organization, several people can be admins),
   - the **SolidWorks version** everyone saves in (the oldest one on the team; default 2024),
   - the **vault folder**, the same on every PC (e.g. `C:\ClubVault`; keep it outside OneDrive).
   
   The app creates a **private** GitHub repo with the starting folders you tick (**COTS** for purchased parts and **Sandbox** for trying things out, both ticked by default; rename them or add more), puts in the vault rules (`.gitattributes`, `.gitignore`), the team settings (`pdm.json` with your name, folder and version) and the vault guard, downloads it to the folder and opens it. If the upload fails halfway, fix the problem and click **Create vault** again: it reuses the empty repo. It never deletes anything on GitHub.
3. **Add your teammates:** click **Team** in the command bar (offered right after creating the vault too; also **Vaults** → select the vault → **Team…**). Type their GitHub usernames and click **Add to team**: GitHub emails each one an invitation, and they get **Write** access (check out and check in). Misspelled names and people without a GitHub account are listed so you can fix them. Then click **Copy welcome message** and send it on Discord or by email: one link to `Jarvis.exe` (with "Windows may warn you: More info → Run anyway"), then sign in and **Accept and download** on the welcome screen.

   The Team window also shows everyone who has access, invitations not accepted yet (**Cancel invite**; GitHub invitations expire after 7 days, so add them again if needed), and **Remove** for people who leave. Anyone on the team can see the list; only the repo's **Admin** role can add or remove people.
4. Optionally edit `pdm.json` in the vault and check it in:
   - `team`: GitHub username → real name (shown in the app and Discord),
   - `discordWebhookUrl`: Discord → Server Settings → Integrations → Webhooks → New Webhook → Copy URL.

Only the repo's **Admin** role can invite people (GitHub doesn't let Maintain do it). `scripts/Initialize-Vault.ps1` still works for a repo you created yourself.

`pdm.json` lives in the vault, so you can change names or the webhook later. Edit it in `C:\CapstoneVault` and check it in. Everyone picks up the change on their next Get Latest.

### Part numbering

Click **Numbering** in the toolbar (admins; it may be under **…**). Set a prefix (e.g. TLT), the number of digits (3) and groups with a number and a name (200 Turret, 300 Tension test, 400 Bending fixture). A group owns the numbers after it, up to the next group: 201 to 299 for 200. It's saved in the vault (`.pdm/numbering.json`), so everyone gets it at their next Get Latest. New parts checked in from SolidWorks then get the next free number in the group the person picks; existing files aren't changed.

### Deleting files from the vault

**Only files that nothing else uses can be deleted** (or requested): a part an assembly or drawing still uses would leave it with a missing reference. Remove it from those files and check them in first, or select them too, to delete them together. Jarvis knows what uses what from the vault index, so if an assembly or drawing isn't indexed yet, SolidWorks files can't be deleted until it is (that happens at the next check-in from SolidWorks by someone who has it).

Teammates use **Request Delete** (right-click a file) and write why. Admins get a banner when there are requests; filter by **Delete requested** to see them, with the reason in the details panel. Then right-click:

- **Delete from Vault… (admin)** deletes the file for everyone, with its index entry and 3D preview, in one check-in. It goes away from teammates' PCs at their next Get Latest. Admins can also do this without a request.
- **Reject Delete Request… (admin)** keeps the file; the reason goes to Discord.

Older versions stay in the vault's history (History, milestones), so a deleted file can be brought back, and it still counts toward GitHub's LFS storage. The vault guard lets teammates add or withdraw only their own requests.

### Who's an admin (and why typing a name doesn't work)

**Admins are the people GitHub lists as Admin or Maintain on the vault repo. Nothing else counts:** there's no admin list in `pdm.json` anyone could edit. Jarvis asks GitHub which account each PC is signed in as, using the sign-in Git already saved, so nobody can claim to be someone else by typing a name, and the vault guard checks the same GitHub roles. Settings shows the verified account. If a PC is offline, admin buttons stay hidden until it can check with GitHub.

On a **personal** GitHub repo only the owner can be Admin; collaborators can only get Write. To have more admins later, move the vault to a free GitHub **organization** (repo **Settings → Transfer**), then give those people the Admin or Maintain role. Local folders keep working: GitHub redirects the old URL.

### Stopping people from getting around the app with Git commands

The app's rules run on each PC, so someone typing raw Git commands could skip them. These steps move the protection onto GitHub, where nobody but you can change it:

1. **Install the vault guard.** In the app, click **Update Vault Setup** (it appears for admins whenever the guard is missing or was changed). This adds a GitHub Actions check (`.github/workflows/vault-guard.yml`) that runs after every push. It uses GitHub's record of who pushed and undoes any push where a non-admin changed `pdm.json`, the frozen list, `.github`, a frozen file, a file someone else has checked out, a file in review, someone else's check-in request or review, or pushed a milestone tag. It posts to Discord when it does. It also warns about force pushes and files saved in a newer SolidWorks than the team's. Pushes it checks show under the repo's **Actions** tab.
2. **Repo roles.** On GitHub, go to **Settings**, then **Collaborators**. Give teammates **Write** and admins **Admin** (or Maintain).
3. **Protect `main`.** On GitHub, go to **Settings**, then **Branches**, then **Add rule** for `main`. Leave **Allow force pushes** and **Allow deletions** off, so history can't be rewritten or erased. On a private repo this needs GitHub Pro, which is free with the GitHub Student Developer Pack.
4. **Test file locks once with two accounts.** Have a teammate check out a file, then try to push a change to it from your account with Git. If GitHub accepts it, the lock was only enforced by the app; the vault guard still catches frozen files but not ordinary check-outs.

The guard is also why the app warns admins if `.github` is changed: it's the one thing a determined person could tamper with, and **Update Vault Setup** puts it back. If the guard is removed or edited, the admin's app also **posts an alert to Discord** (once per change).

**Security notes**

* **Forced unlocks.** GitHub lets anyone with Write access force-unlock a file with raw Git (`git lfs unlock --force`). The app only shows Break Lock to admins, but it can't stop the raw command. What it can do: the vault guard undoes a push that changes a file someone else had checked out, and every version stays in the history. If a lock disappears unexpectedly, look at the repo's **Actions** tab and Discord.
* **Keep the Discord webhook URL private.** Anyone with it can post messages as the bot. It's stored in `pdm.json`, so everyone with vault access can read it; don't paste it anywhere public. If it leaks, delete the webhook in Discord, make a new one and update `pdm.json`.
* **The SolidWorks add-in folder** (`C:\ProgramData\Jarvis\SolidWorksAddin`) can only be changed by Windows administrators, so another account on the PC can't swap in a DLL that SolidWorks would load.
* **Updates are checked** against the SHA-256 checksums published with each release before they're installed.

### Breaking a stale lock

Select the file, then click **Break Lock**. The button only appears for admins. The team is told in Discord. The person who had it loses their check-out. Any changes they hadn't checked in stay on their PC only, and they'll see a conflict or "edited without check-out" state.

GitHub decides whether a forced unlock is allowed. If it's refused, your GitHub role on the repo may need to be raised (Maintain or Admin).

### Requesting a check-in

Someone has a file checked out and isn't around, but a teammate needs it? Select the file and click **Request Check-In** (in the app or the SolidWorks pane), and add a short note.

* That person's own Jarvis, either the app or the SolidWorks add-in, checks in **their last saved version** and releases the file automatically. It waits about **3 minutes** after first seeing the request, so they get a chance to save their work, and the file shows "check-in requested" in the meantime. If their PC is on it usually happens within about 5 minutes. They get a message explaining what happened, and the team sees it in Discord.
* Changes they hadn't **saved** in SolidWorks aren't included, because those aren't on disk.
* If their PC is off, the request waits until they next open Jarvis or SolidWorks. If you can't wait, **Break Lock** releases the file right away, but their saved work then stays on their PC only.
* The file shows "check-in requested by *name*" until it's done.

### Freezing files (released for production)

Select files in the app (or open one in SolidWorks) and click **Freeze**, then give a reason such as "Released for production, rev A". Only admins see Freeze and Unfreeze. In SolidWorks you can freeze an assembly's parts at the same time.

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

Jarvis records each SolidWorks file's custom properties, references, saved-in version and 3D preview whenever it's checked in: from the SolidWorks pane directly, and from the Jarvis app by asking the add-in in SolidWorks when SolidWorks is open (if it isn't, the check-in still goes ahead and the files are recorded later; 3D previews of files checked in from the app always come with the next check-in from SolidWorks). Anything still missing or out of date (files from before the add-in, or checked in when SolidWorks wasn't available) is caught up automatically: every check-in from the SolidWorks pane also records up to 10 such files. Nobody has to run anything. The app recognizes common property names: `PartNo`/`Part Number`/`Number`, `Description`, `Material`.

### Review before release

Teammates submit finished files with **Submit for Review…** (app or SolidWorks pane) and a note. Admins see a banner with how many files are waiting; filter the vault view by **In review**.

* **Approve & Release…** freezes the files with the next revision letter, like Freeze. Approve drawings from the SolidWorks pane to also save and freeze their PDFs.
* **Reject…** sends them back with your reason (shown in Discord). The submitter can edit them again.
* While a file is in review, its submitter keeps the lock, so nobody (the submitter included) can change it. The list lives in `.pdm/reviews.json`, and the vault guard stops non-admins from editing other people's reviews or faking approvals.

Freeze still works directly, for when you don't need the review step.

### Milestones

Click **Milestones**, type a name such as "Design Review 1" and an optional description, then click **Create**. This labels the vault exactly as it is now; only admins can create milestones. Everyone can view one later. The team gets a Discord notice.

### Revisions and drawing PDFs

Every freeze is a release with the next revision letter: A, B, C, and so on, skipping I, O, Q, S, X and Z as drawing standards do. The letter shows in the **Rev** column and the SolidWorks pane, and the sequence continues after unfreezing and refreezing. **Freezing a drawing from the SolidWorks pane also saves `<name> Rev X.pdf`** next to it, covering all sheets, and checks in and freezes that PDF with the same revision. Freezing from the desktop app gives the revision but no PDF, because making a PDF needs SolidWorks.

### SolidWorks versions

The PDM itself works with any version, and the add-in supports SolidWorks 2020 and newer. But **SolidWorks files can't be opened in an older SolidWorks than the one that last saved them.** Agree on one SolidWorks version for all vault files (the oldest one anyone uses), or a single save in a newer version locks everyone on the older one out of that file and every assembly that uses it.

### What's tracked

`.gitattributes` makes these **LFS + lockable** (read-only until checked out): `.sldprt .sldasm .slddrw .step .stp .pdf .dxf` (lower- and upper-case). Everything else (spreadsheets, notes, `pdm.json`) is a normal Git file: it's versioned but not lockable, so you edit it and check it in. `.gitignore` excludes SolidWorks temp files (`~$*`) and backups.

### Using it for another project (e.g. a club)

The tool isn't tied to the capstone, and nothing in Jarvis.exe names a team. For a new team (a club, another class):

1. The team lead clicks **Vaults** → **New team vault…** (see *First-time vault setup*), e.g. "Club Vault" in `C:\ClubVault`. The folder is saved in the vault's `pdm.json` as `localPath`, so everyone who downloads it gets the same folder.
2. Click **Team**, add your teammates' GitHub usernames, and send them the welcome message.
3. Members who already have Jarvis: **Vaults** → **Find my vaults** → pick the vault → **Download and open**. New members download the same `Jarvis.exe`, install it, and accept the invitation on the welcome screen.

The vault guard is included from the start. `Setup-Jarvis.ps1 -RepoUrl <url> -VaultPath <folder>` still downloads a vault during setup if you want that.

### Several groups on one PC

One install of Jarvis works with every group you're in; each group has its own vault (repo and folder).

* **Add a group's vault:** click **Vaults** (next to the vault dropdown), then **Find my vaults**. It lists every GitHub repo you can access that has a `pdm.json`. Pick one and click **Download and open**; it goes into the folder that vault's `pdm.json` names (`localPath`), or `C:\Vaults\<repo name>`. If a group's vault is missing from the list, accept its GitHub invite (check your email) or ask its admin to invite you. A vault that's already on your PC can be added with **Add a folder on this PC…**.
* **Switch** with the **Vault** dropdown in the app.
* **In SolidWorks you don't switch at all:** the pane works in whichever vault the open file is in, so you can have a capstone assembly and a club part open and the pane follows the active window. Its header shows the vault's name.
* Each vault has its own admins (from that repo's GitHub roles), frozen files, reviews, milestones and Discord channel. Removing a vault from the list doesn't delete its files.

If the club should get app updates from a different code repo (for example your own fork), change `<UpdateRepo>` in `Directory.Build.props` before building.

### GitHub LFS storage

GitHub limits free LFS **storage and bandwidth**, and every Get Latest that downloads CAD files counts toward bandwidth. **Check the current limits** in GitHub's docs before relying on the free tier. Watch usage under the repo's *Settings → Billing / Git LFS*. If you outgrow it, a paid data pack, GitLab, or a self-hosted Gitea are drop-in replacements: change the repo URL and nothing else.

---

## For developers

### Layout

```
src/Jarvis.Core/     netstandard2.0. All vault logic, no UI. Reused by the Phase 2 add-in.
  Git/                    GitRunner (runs git.exe), GitErrorClassifier (stderr → typed errors), GitHubApi (create repo, invite), GitHubSignIn (first run)
  Vault/                  VaultService (all PDM commands), models, Git output parsers, VaultCatalog (find/download), VaultCreator (new team vault), VaultInvites
  Notifications/          Discord webhook
  Updates/                automatic updates from GitHub Releases (SHA-256 checked)
  Config/                 pdm.json (team) and settings.json (per PC)
  Errors/                 PdmException and subclasses, with user-facing messages
src/Jarvis.Client/   WPF app (.NET 8), MVVM-lite
src/Jarvis.SwAddin/  SolidWorks add-in (.NET Framework 4.8): task pane, reference-aware check-out/in
tests/Jarvis.Core.Tests/  xUnit: parsers, error classification, and integration tests against real Git
vault-template/           .gitattributes, .gitignore, pdm.json for a new vault
scripts/                  Setup (teammates), Initialize-Vault (admin), Publish-Client (build the zip), Release-Client (publish an update)
```

### Build, test, run

Requires the .NET 8 SDK and Git.

```powershell
dotnet build Jarvis.sln
dotnet test Jarvis.sln
dotnet run --project src/Jarvis.Client
```

The integration tests use a local bare repo in place of GitHub. A local remote has no LFS lock server, so locking itself is only covered by parser and classifier tests. **Test check-out and check-in against a real GitHub test repo before the team relies on it.**

### Publishing the app

```powershell
.\scripts\Publish-Client.ps1
```

This produces the release files: a self-contained single-file `Jarvis.exe` (teammates don't need .NET), `Jarvis-SolidWorksAddin.zip` (with a READ ME FIRST note for anyone who opens it) and `SHA256SUMS`. It also builds `dist/Jarvis-Setup.zip` (`Setup.cmd`, `Setup-Jarvis.ps1` and the exe) for anyone who prefers a script; releases don't include it. Release notes always start with "download Jarvis.exe". Setup prints the installed exe's SHA-256 so it can be compared with the release.

`Jarvis.exe` installs itself: run from anywhere but `%LocalAppData%\Programs\Jarvis`, it shows **Install Jarvis** (`Views/InstallWindow`, logic in `Core/Install/AppInstaller.cs`): copy itself there (removing the download mark), Git for Windows with `winget --silent --scope user` (falling back to a machine-wide install, or git-scm.com without winget), shortcuts, then start the installed copy. Development builds (under `bin\`) never offer it. To try it without touching your real install, set `JARVIS_INSTALL_ROOT` to an empty folder and run a published `Jarvis.exe`: everything goes inside that folder. `Jarvis.exe --welcome` shows the welcome screen even on a PC that already has vaults.

### Automatic updates

The app checks this repo's **GitHub Releases** each time it starts. When there's a newer version it asks, downloads it (and the SolidWorks add-in, if installed; Windows asks for permission for that part), **checks every file against the release's `SHA256SUMS`**, swaps the exe and restarts. To publish a version:

1. Raise `<Version>` in `Directory.Build.props`, commit and push.
2. Run `.\scripts\Release-Client.ps1 -Notes "What changed"`. It builds everything and creates release `v<Version>` with the files attached, using your saved GitHub sign-in.

**The code repo is public**, so anyone can download the app from its Releases page (the install link in invite messages) and every copy can update itself without GitHub access to it. The code contains no passwords or keys, and each team's vault stays private. (A fork kept private works too, but then teammates need **Read** access to it for installs and updates.) The repo the app updates from is set by `<UpdateRepo>` in `Directory.Build.props` and baked into the build, so nobody can point the team at a different exe by editing a vault file.

### How the commands map to Git

| Command | Git |
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
| Rename / Move, Move to Folder | `VaultService.PlanMoveAsync` / `MoveAsync` (Core): find referencing files with an `IReferenceEditor` (the add-in's SolidWorks, or the app's `SolidWorksAutomation`, which drives the team's SolidWorks over COM, started hidden if needed), check out, move, `ReplaceReferencedDocument` on each, move index entries and previews, check in old paths + new paths + referencing files; everything is put back if a reference can't be updated |
| Import files | `VaultImport.CopyAndRepoint` (Core): copy into the vault under new names, `ReplaceReferencedDocument` on each copy so it uses the other copies (copies are deleted again if one fails); then the add-in writes PartNumber/Description, makes records and previews, and checks the copies in as one commit |
| Identity | `git credential fill` (no prompt) → GitHub API `/user` + repo permissions; admin = verified login in `pdm.json` admins, or repo admin/maintainer |
| Vault guard | GitHub Actions on push; guard script read from the commit before the push; undoes a rule-breaking push by restoring the touched files in one new commit |
| 3D previews | add-in exports coarse binary STL to `.pdm/preview/<path>.stl` (LFS) at check-in (and the catch-up); over 12,000 triangles (usually assemblies with detailed hardware) `PreviewSimplifier` merges nearby points until it fits (about 600 KB at most); app renders with HelixToolkit |
| Freeze / Unfreeze | admin keeps (or releases) the `lfs lock`; `.pdm/frozen.json` committed and pushed |
| Request Check-In | admin adds an entry to `.pdm/checkin-requests.json`; the owner's client sees it on its next vault check, commits the saved file plus the updated list, pushes and unlocks |
| Vault view | `fetch`, `lfs locks --verify --json`, `status`, `ls-files`, `log --name-only` |

### SolidWorks add-in

* **Building** needs SolidWorks installed (it references the interop DLLs in `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS`; override with `/p:SolidWorksDir=...`). Without SolidWorks, build the other projects individually.
* **Version support:** built against the SolidWorks **2024** interop files with *Embed Interop Types* on. It only uses API calls that already existed in SolidWorks 2017 (the multi-size task pane icon, with a fallback for older versions), so one build supports **2020 and newer**. It has been tested on 2024 and 2026. Don't add newer API calls without a version check (`ISldWorks.RevisionNumber()`; 2020 is 28, 2024 is 32, 2026 is 34).
* **Install for development:** close SolidWorks, build, then run `scripts\Install-SolidWorksAddin.ps1`. It copies the Release (or Debug) build to `C:\ProgramData\Jarvis\SolidWorksAddin` and registers it with RegAsm (admin prompt). Run with `-Uninstall` to remove it. Re-run after every rebuild.
* **Design:** `JarvisAddin` (COM entry point and registration), `VaultPane` (WinForms task pane: watches the active document with a 1-second timer instead of SolidWorks events, then calls Core), `FileTreeView` (owner-drawn files-in-use tree with tick boxes), `SolidWorksSession` (component tree, references, properties, save, close/reopen), `AssemblySaveGuard` (the one SolidWorks event hook: Save in a read-only assembly saves only checked-out parts), `DescriptionForm` (descriptions for new files).
* **Files in use:** `SolidWorksSession.GetFilesInUse` walks `GetRootComponent3().GetChildren()`, falling back to `IAssemblyDoc.GetComponents(false)` grouped by `GetParent()` (the root walk comes back empty for read-only assemblies whose parts aren't loaded), then to `GetDocumentDependencies2`. `Core/Vault/FileTree.cs` turns that into rows (unit-tested). The tree rebuilds when the active path, its save time or its component count changes.
* **3D previews:** `Core/Preview/PreviewMesh.cs` reads the STL, welds vertices, smooths normals within a 35° crease angle and finds feature edges; `Client/Services/PreviewModel.cs` turns that into WPF geometry (satin material, edge lines as thin tubes, studio lights). `Jarvis.exe --render-preview in.stl out.png` renders one to an image for checking.
* **Older versions on this PC:** `VaultService.UseVersionAsync` runs `git restore --source=<commit> --worktree` and records the choice in `.git/capstone-pinned.json` (never committed). Snapshots treat pinned files as unchanged, Get Latest restores and re-pins them around the merge, and check-out/check-in refuse them.
* **SolidWorks doesn't lock files.** It keeps a *shared* handle on every loaded file, so Git can read a saved file while it's open. Check-in therefore saves the targets in place (`SaveTargets`) instead of closing documents, and Core only refuses files nobody can read (`FileUtil.IsUnreadable`). Get Latest and Undo still close and reopen, because they replace files on disk.
* **Dependencies:** SolidWorks is the host process, so binding redirects don't apply. `AssemblyResolver` loads System.Text.Json and friends from the add-in folder.
* **Log:** `%LocalAppData%\Jarvis\addin.log`.
* **Rename / Move** (`VaultPane.RenameAsync`) and the app's **Move to Folder** share `VaultService.MoveAsync`. Referencing files are found by reading them with `GetDocumentDependencies2` rather than trusting the index (the index is only used for files that aren't downloaded). The app calls SolidWorks by name through `IDispatch` (`Type.InvokeMember`): `dynamic` fails when two SolidWorks versions are installed, because their type information isn't available.

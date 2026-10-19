# Capstone PDM Spec

Oct 18, 2026 · @Master

## Overview

Build a lightweight PDM on top of Git LFS so 8 Windows users can check files out and in, never overwrite each other, and roll back any version. It should feel like the PDM the team already knows, with no server to host.

Goals, in priority order:

1. No two people can edit the same SolidWorks file at once.
2. Every check-in is a version anyone can restore.
3. Assembly references never break because of the tool.
4. The team sees check-outs and check-ins in Discord without asking.

## Architecture

A private GitHub repo with Git LFS is the vault; a small Windows client wraps it in PDM-style commands; a Discord webhook announces activity.

&#91;embedded content: system architecture · 3 parts\]

- **Vault:** a private GitHub (or GitLab) repo. SolidWorks files are stored with Git LFS and marked lockable, so Git makes them read-only until checked out.
- **Local vault folder:** the same path on every PC, e.g. `C:\CapstoneVault`. Identical paths keep assembly references resolving for everyone.
- **Client:** a C# (.NET) Windows app (tray app or small window) with Check Out, Check In, Get Latest, Undo Check Out, and History. Under the hood it calls `git lfs lock`, `git commit`, `git push`, `git pull`, and `git lfs unlock`.
- **Notifications:** the client posts to a Discord channel webhook on every check-out and check-in.
- **Storage note:** GitHub limits free LFS storage and bandwidth. Check current limits; if you outgrow them, GitLab, a paid LFS pack, or a self-hosted Gitea on a spare PC are drop-in swaps.

## Phase 1: MVP (target: before CAD starts)

Phase 1 delivers locking, versioning and notifications with no SolidWorks API work.

- [ ] `.gitattributes` marks `*.sldprt`, `*.sldasm`, `*.slddrw` and other binary formats (STEP, PDF, DXF) as LFS + lockable
- [ ] `.gitignore` excludes SolidWorks temp files (`~$*`) and backups
- [ ] Check Out: pulls latest, locks the file, makes it writable
- [ ] Check In: requires a comment, commits, pushes, unlocks
- [ ] Undo Check Out: discards local changes and unlocks
- [ ] Get Latest: pulls without touching files the user has checked out
- [ ] Vault view: every file with its status (available, checked out by me, checked out by someone else) and last check-in comment
- [ ] History: list versions of a file and restore any one as a new check-in
- [ ] Discord webhook posts on check-out and check-in
- [ ] One-click setup script: installs Git and Git LFS, clones to the standard path, sets user name
- [ ] Admin override to break a stale lock

## Phases 2 and 3

Add SolidWorks awareness only after Phase 1 is trusted by the whole team.

**Phase 2: SolidWorks add-in**

- Task pane inside SolidWorks with Check Out / Check In buttons for the active document
- Warn when opening a file someone else has checked out
- Reference-aware check-out: offer to check out an assembly's children too
- Rename and move done through the add-in, so references update

**Phase 3: Data and release**

- Read custom properties (part number, description, material) into the vault view
- Simple states: Work in Progress, In Review, Released
- BOM export from an assembly to CSV
- Discord slash commands, e.g. `/whohas bracket.sldprt`

## SolidWorks rules for the team

Until Phase 2 exists, these habits do the job the add-in would do.

1. Always work inside the vault folder at the standard path; never open copies from Downloads or Discord.
2. Check out before editing. A read-only file means you haven't.
3. Never rename or move files in Windows Explorer. Use Save As inside SolidWorks with references, or ask whoever owns the assembly.
4. Check in at least daily with a real comment ("added mounting holes," not "update").
5. Close the file in SolidWorks before checking in, so the saved version is complete.
6. Agree on a file naming scheme before anyone starts modeling.

## Open decisions and first prompt

Settle these before building, then paste the prompt below into Claude Code or Codex in an empty folder.

- [ ] Check whether the school provides SOLIDWORKS PDM or 3DEXPERIENCE; if so, compare before building
- [ ] Host: GitHub, GitLab, or self-hosted Gitea
- [ ] Client language: decided, C# (.NET), so the client and the Phase 2 SolidWorks add-in share code
- [ ] Who is admin (can break locks, manage the repo)
- [ ] File naming scheme

**First prompt:**

```markdown
Build a lightweight PDM client for SolidWorks on Windows, for an 8-person student team.

Backend: a private GitHub repo using Git LFS with file locking. SolidWorks files (.sldprt, .sldasm, .slddrw) plus .step, .pdf, .dxf are LFS-tracked and lockable. Everyone clones to C:\CapstoneVault.

Phase 1 only. Features: Check Out (pull, git lfs lock, make writable), Check In (required comment, commit, push, unlock), Undo Check Out, Get Latest (never overwrite my checked-out files), a vault view showing each file's status and who holds the lock, per-file history with restore, a Discord webhook post on check-out and check-in, an admin lock override, and a setup script that installs Git + Git LFS and clones the repo.

Write it in C# (.NET) as a Windows app, structured so a SolidWorks add-in can reuse the vault logic in Phase 2. Use PDM vocabulary in the UI. Handle errors clearly: offline, lock held by someone else, push rejected. Write a README for teammates. Start by proposing the project structure and asking me any questions before coding.
```

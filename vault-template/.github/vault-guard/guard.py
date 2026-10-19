#!/usr/bin/env python3
"""Capstone PDM vault guard.

Runs on GitHub after every push to the vault branch, so it sees who really pushed
(GitHub's record, not the name typed into a commit) and can't be skipped by using Git
commands instead of the app. It checks the push against the vault rules:

  * only admins change pdm.json, .pdm/frozen.json or .github/ (judged by the admin list
    as it was BEFORE the push, so nobody can make themselves admin in the same push);
  * nobody adds admin check-in requests, or removes requests made of someone else;
  * non-admins don't change frozen files;
  * (warning only) files saved in a newer SolidWorks than the team's version, force pushes.

A push that breaks a rule is undone straight away with a new commit that puts every file it
touched back the way it was, and the team is told in Discord. History keeps the undone push.
"""

import json
import os
import re
import subprocess
import sys
import urllib.request

ZERO = "0" * 40
ADMIN_ONLY_FILES = ("pdm.json", ".pdm/frozen.json")
ADMIN_ONLY_PREFIXES = (".github/",)
REQUESTS = ".pdm/checkin-requests.json"
FROZEN = ".pdm/frozen.json"
INDEX_PREFIX = ".pdm/index/"


def git(*args, stdin=None, check=True):
    result = subprocess.run(["git", *args], input=stdin, capture_output=True, text=True)
    if check and result.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)} failed: {result.stderr.strip()}")
    return result


def show(ref, path):
    result = git("show", f"{ref}:{path}", check=False)
    return result.stdout if result.returncode == 0 else None


def strip_json_comments(text):
    """pdm.json allows // and /* */ comments; drop them without touching strings (URLs contain //)."""
    out, i, n, in_string = [], 0, len(text), False
    while i < n:
        c = text[i]
        if in_string:
            out.append(c)
            if c == "\\" and i + 1 < n:
                out.append(text[i + 1])
                i += 2
                continue
            if c == '"':
                in_string = False
            i += 1
            continue
        if c == '"':
            in_string = True
            out.append(c)
            i += 1
        elif text.startswith("//", i):
            end = text.find("\n", i)
            i = n if end < 0 else end
        elif text.startswith("/*", i):
            end = text.find("*/", i + 2)
            i = n if end < 0 else end + 2
        else:
            out.append(c)
            i += 1
    return "".join(out)


def load_json(text):
    if not text:
        return {}
    try:
        value = json.loads(re.sub(r",(\s*[}\]])", r"\1", strip_json_comments(text)))
        return value if isinstance(value, dict) else {}
    except ValueError:
        return {}


def lower_set(items):
    return {str(item).lower() for item in items if item}


def frozen_paths(text):
    return lower_set(f.get("path") for f in load_json(text).get("files", []) if isinstance(f, dict))


def request_keys(text):
    return {
        (str(r.get("path", "")).lower(), str(r.get("owner", "")).lower())
        for r in load_json(text).get("requests", [])
        if isinstance(r, dict)
    }


def is_repo_admin(actor):
    token, repo = os.environ.get("GH_TOKEN"), os.environ.get("REPO")
    if not token or not repo:
        return False
    request = urllib.request.Request(
        f"https://api.github.com/repos/{repo}/collaborators/{actor}/permission",
        headers={"Authorization": f"Bearer {token}", "Accept": "application/vnd.github+json", "User-Agent": "CapstonePDM-VaultGuard"},
    )
    try:
        with urllib.request.urlopen(request, timeout=15) as response:
            data = json.load(response)
        return data.get("permission") == "admin" or data.get("role_name") in ("admin", "maintain")
    except Exception:
        return False


def changed_paths(before, after):
    output = git("diff", "--name-only", "--no-renames", "-z", before, after).stdout
    return [p for p in output.split("\0") if p]


def undo(before, changed, actor, branch):
    """Put every file the push touched back to how it was before, as one new commit."""
    git("config", "user.name", "Vault Guard")
    git("config", "user.email", "vault-guard@users.noreply.github.com")
    existed = set(git("ls-tree", "-r", "--name-only", "-z", before).stdout.split("\0"))
    restore = [p for p in changed if p in existed]
    remove = [p for p in changed if p not in existed]
    if restore:
        git("checkout", before, "--pathspec-from-file=-", "--pathspec-file-nul", stdin="\0".join(restore) + "\0")
    if remove:
        git("rm", "-q", "--ignore-unmatch", "--pathspec-from-file=-", "--pathspec-file-nul", stdin="\0".join(remove) + "\0")
    commit = git("commit", "-q", "-m", f"Vault Guard: undid a push by {actor} that broke vault rules", check=False)
    if commit.returncode != 0:
        return False
    return git("push", "origin", f"HEAD:{branch}", check=False).returncode == 0


def notify(config, actor, violations, warnings, undone):
    url = config.get("discordWebhookUrl")
    if not url:
        return
    lines = []
    if violations:
        lines.append(f"\U0001F6E1️ **Vault Guard**: **{actor}** pushed changes that break the vault rules:")
        lines += [f"• {v}" for v in violations]
        lines.append("It was undone automatically." if undone else "It could NOT be undone automatically. An admin should check the vault.")
    if warnings:
        lines.append(f"⚠️ **Vault Guard** warnings for {actor}'s push:")
        lines += [f"• {w}" for w in warnings]
    body = json.dumps({"username": "Vault Guard", "content": "\n".join(lines)[:1990], "allowed_mentions": {"parse": []}}).encode()
    request = urllib.request.Request(url, data=body, headers={"Content-Type": "application/json", "User-Agent": "CapstonePDM-VaultGuard"})
    try:
        urllib.request.urlopen(request, timeout=15).close()
    except Exception as error:
        print(f"Couldn't post to Discord: {error}")


def main():
    before, after, actor = os.environ["BEFORE"], os.environ["AFTER"], os.environ["ACTOR"]
    forced = os.environ.get("FORCED", "false") == "true"
    branch = os.environ.get("BRANCH", "main")
    if before == ZERO:
        print("New branch: nothing to compare against.")
        return 0
    if git("cat-file", "-e", f"{before}^{{commit}}", check=False).returncode != 0:
        git("fetch", "-q", "origin", before, check=False)

    config = load_json(show(before, "pdm.json"))
    admin = actor.lower() in lower_set(config.get("admins", [])) or is_repo_admin(actor)
    changed = changed_paths(before, after)
    violations, warnings = [], []

    if not admin:
        for path in changed:
            if path in ADMIN_ONLY_FILES or path.startswith(ADMIN_ONLY_PREFIXES):
                violations.append(f"changed {path}, which only admins may change")
        if REQUESTS in changed:
            old, new = request_keys(show(before, REQUESTS)), request_keys(show(after, REQUESTS))
            if new - old:
                violations.append("added an admin check-in request")
            if [key for key in old - new if key[1] != actor.lower()]:
                violations.append("removed a check-in request made of someone else")

    frozen_before, frozen_after = frozen_paths(show(before, FROZEN)), frozen_paths(show(after, FROZEN))
    for path in changed:
        if path.lower() in frozen_before and path.lower() in frozen_after:
            (warnings if admin else violations).append(f"changed the frozen file {path}")

    team = config.get("solidWorksVersion")
    if isinstance(team, int):
        for path in changed:
            if path.startswith(INDEX_PREFIX) and path.endswith(".json"):
                saved = load_json(show(after, path)).get("savedIn")
                if isinstance(saved, int) and saved > team:
                    warnings.append(f"{path[len(INDEX_PREFIX):-5]} was saved in SolidWorks {saved}, newer than the team's {team}")

    if forced:
        warnings.append("force-pushed (rewrote history). Turn on branch protection to block this.")

    if not violations and not warnings:
        print(f"Push by {actor}: all good ({len(changed)} file(s)).")
        return 0

    undone = bool(violations) and not forced and undo(before, changed, actor, branch)
    notify(config, actor, violations, warnings, undone)
    for line in violations:
        print(f"VIOLATION: {line}")
    for line in warnings:
        print(f"warning: {line}")
    print("Undone." if undone else "Not undone." if violations else "")
    return 1 if violations else 0


if __name__ == "__main__":
    sys.exit(main())

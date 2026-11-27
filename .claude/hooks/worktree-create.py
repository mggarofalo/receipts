"""WorktreeCreate hook: creates a git worktree and installs dependencies.

Replaces Claude Code's default git worktree behavior so that:

- When git-cow (https://github.com/gabrielkoerich/git-cow) is installed, the
  worktree is a copy-on-write clone (APFS clonefile / reflink) of git-cow's
  extra checkout, so bin/, obj/ and node_modules/ come along at ~0 disk cost.
  The extra checkout lives at .claude/worktrees/.git-cow/<repo>; it is
  gitignored, Claude Code's stale-worktree sweep never matches its name, and
  this hook never deletes it.
- Without git-cow (Windows, Linux CI, a fresh Mac) or with GIT_COW=0, it runs
  plain `git worktree add`. git-cow also falls back to that by itself when the
  volume can't clone.
- npm dependencies are installed unless the clone already brought a
  node_modules that matches the worktree's package-lock.json.

Removal needs no hook: Claude Code's default `git worktree remove --force`
works on git-cow worktrees, and never touches the extra checkout.

Input (stdin JSON):
  - name: slug identifier for the worktree (may contain "/")
  - cwd: current working directory (repo root)

Output (stdout):
  - Absolute path to the created worktree directory (must be the only stdout)
"""

import json
import os
import shutil
import subprocess
import sys


def find_git_cow():
    """Path to the git-cow binary, or None when it's absent or disabled."""
    if os.environ.get("GIT_COW") == "0":
        return None
    # Hooks may not inherit the interactive shell's PATH, so also check
    # cargo's default install dir.
    found = shutil.which("git-cow")
    if found:
        return found
    cargo_bin = os.path.join(os.path.expanduser("~"), ".cargo", "bin", "git-cow")
    return cargo_bin if os.access(cargo_bin, os.X_OK) else None


def create_worktree(cwd, worktree_dir, branch_name):
    git_cow = find_git_cow()
    if git_cow:
        print(f"Creating worktree with git-cow ({git_cow})...", file=sys.stderr)
        cmd = [git_cow, "add", "-b", branch_name, worktree_dir]
    else:
        cmd = ["git", "worktree", "add", worktree_dir, "-b", branch_name]
    # Both commands print to stdout; keep stdout clean for the path.
    subprocess.run(cmd, cwd=cwd, check=True, stdout=sys.stderr, stderr=sys.stderr)


def node_modules_current(client_dir):
    """True when node_modules already matches package-lock.json.

    npm records what it installed in node_modules/.package-lock.json. Every
    installed package must match the lockfile, and the only lockfile entries
    allowed to be missing are optional ones (other-platform binaries).
    """
    try:
        with open(os.path.join(client_dir, "package-lock.json")) as f:
            wanted = json.load(f)["packages"]
        with open(os.path.join(client_dir, "node_modules", ".package-lock.json")) as f:
            installed = json.load(f)["packages"]
    except (OSError, ValueError, KeyError):
        return False
    wanted.pop("", None)
    if any(wanted.get(path) != entry for path, entry in installed.items()):
        return False
    return all(
        entry.get("optional") for path, entry in wanted.items() if path not in installed
    )


def install_npm_dependencies(client_dir):
    if not os.path.exists(os.path.join(client_dir, "package.json")):
        return
    if node_modules_current(client_dir):
        print(f"node_modules in {client_dir} is current, skipping npm install", file=sys.stderr)
        return
    npm = shutil.which("npm")
    if not npm:
        print("WARNING: npm not found in PATH, skipping install", file=sys.stderr)
        return
    print(f"Installing npm dependencies in {client_dir}...", file=sys.stderr)
    subprocess.run([npm, "install"], cwd=client_dir, check=True, stdout=sys.stderr, stderr=sys.stderr)


def main():
    payload = json.loads(sys.stdin.read())
    name = payload["name"]
    cwd = payload["cwd"]

    # If cwd doesn't exist (e.g., stale worktree path), resolve the repo root
    if not os.path.isdir(cwd):
        try:
            cwd = subprocess.run(
                ["git", "rev-parse", "--show-toplevel"],
                capture_output=True, text=True, check=True,
            ).stdout.strip()
        except subprocess.CalledProcessError:
            print(f"ERROR: cwd {cwd} does not exist and git toplevel not found", file=sys.stderr)
            sys.exit(1)

    # Names may contain "/" (e.g. feat/x). Keep the folder flat: git-cow puts
    # its extra checkout beside the worktree's parent, so a nested folder would
    # get its own extra checkout.
    worktree_dir = os.path.join(cwd, ".claude", "worktrees", name.replace("/", "-"))
    create_worktree(cwd, worktree_dir, f"worktree-{name}")
    install_npm_dependencies(os.path.join(worktree_dir, "src", "client"))

    # Print the worktree path for Claude Code (must be the only stdout)
    print(worktree_dir)


if __name__ == "__main__":
    main()

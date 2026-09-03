"""Shared helpers for probing agent CLIs, and the one-click installers.

Three vendors ship a CLI that can state its own quota without spending any:

* ``claude -p "/usage"`` — Claude Code, text answer, 0 turns / $0.00;
* ``agy -p "/usage" --output-format json`` — Antigravity CLI, structured;
* ``codex`` — already probed over its app-server JSON-RPC (see _codex_probe).

A CLI is the most reliable source there is: it authenticates the way the vendor
intends, and the number comes from the vendor's own server rather than from a
file FastPrompter guessed the meaning of. This module is only the plumbing —
locating the executable, running it under a hard deadline without a console
flash, and describing how the user can install one.

Nothing here ever runs an installer on its own. ``INSTALLERS`` is data: the exact
command, where it lands, and who publishes it. The UI shows that command and only
runs it after the user explicitly confirms, because piping a remote script into
a shell is exactly the kind of action that must never be implicit.
"""

from __future__ import annotations

import dataclasses
import os
import shutil
import subprocess
import time
from pathlib import Path

# CREATE_NO_WINDOW: a 3-minute quota sweep must not flash a console window.
_NO_WINDOW = 0x08000000 if os.name == "nt" else 0


@dataclasses.dataclass(frozen=True)
class CliTool:
    """One agent CLI: how to find it, and how the user installs it."""

    key: str                  # provider_id it feeds
    binary: str               # executable stem, resolved on PATH
    label: str                # human name for the UI
    # Where the official installer puts it, checked when PATH has not been
    # refreshed yet (a freshly installed CLI is invisible to an app that
    # started before the installer edited PATH).
    fallback_paths: tuple[str, ...] = ()
    install_command: str = ""     # verbatim, shown to the user before running
    install_source: str = ""      # who publishes it, so the user can judge
    install_target: str = ""      # where the binary lands


INSTALLERS = (
    CliTool(
        key="claude",
        binary="claude",
        label="Claude Code CLI",
        fallback_paths=("~/.local/bin/claude.exe", "~/.local/bin/claude"),
        install_command="irm https://claude.ai/install.ps1 | iex",
        install_source="claude.ai (Anthropic)",
        install_target="%USERPROFILE%\\.local\\bin\\claude.exe",
    ),
    CliTool(
        key="antigravity",
        binary="agy",
        label="Antigravity CLI",
        fallback_paths=("~/AppData/Local/agy/bin/agy.exe",
                        "~/.local/bin/agy"),
        install_command="irm https://antigravity.google/cli/install.ps1 | iex",
        install_source="antigravity.google (Google)",
        install_target="%LOCALAPPDATA%\\agy\\bin\\agy.exe",
    ),
    CliTool(
        key="codex",
        binary="codex",
        label="Codex CLI",
        fallback_paths=("~/.local/bin/codex.exe", "~/.local/bin/codex"),
        install_command=(
            'powershell -ExecutionPolicy ByPass -c '
            '"irm https://chatgpt.com/codex/install.ps1 | iex"'),
        install_source="chatgpt.com/codex (OpenAI)",
        install_target="on PATH (installer-chosen directory)",
    ),
)

INSTALLERS_BY_KEY = {tool.key: tool for tool in INSTALLERS}


def resolve_binary(tool: CliTool | str) -> str:
    """Absolute path to the CLI, or ``""``.

    PATH first, then the installer's own directory: the CLI a user installs
    while FastPrompter is running is NOT on this process's PATH (Windows hands
    every process a PATH snapshot at launch), so a PATH-only lookup would
    report "not installed" until the app restarts.
    """
    if isinstance(tool, str):
        tool = INSTALLERS_BY_KEY.get(tool) or CliTool(key=tool, binary=tool,
                                                      label=tool)
    found = shutil.which(tool.binary)
    if found:
        return found
    for candidate in tool.fallback_paths:
        path = Path(os.path.expanduser(candidate))
        if path.is_file():
            return str(path)
    return ""


def run_cli(argv: list[str], deadline: float, *,
            cwd: str | None = None, env: dict | None = None) -> dict:
    """Run a CLI under an absolute ``time.monotonic()`` deadline.

    Returns ``{"ok", "stdout", "error"}``. Never raises, never blocks past the
    deadline, never uses a shell (so no argument can be reinterpreted), and
    never opens a console window. A timeout kills the child.
    """
    remaining = deadline - time.monotonic()
    if remaining <= 0.1:
        return {"ok": False, "stdout": "", "error": "deadline_exceeded"}
    try:
        result = subprocess.run(
            argv, capture_output=True, text=True, encoding="utf-8",
            errors="replace", timeout=min(remaining, 30.0), cwd=cwd,
            env=env, creationflags=_NO_WINDOW, check=False,
        )
    except subprocess.TimeoutExpired:
        return {"ok": False, "stdout": "", "error": "timeout"}
    except OSError as exc:
        return {"ok": False, "stdout": "", "error": f"{type(exc).__name__}"}
    if result.returncode != 0:
        # stderr can carry an auth hint ("Not logged in"); it is a vendor
        # message about the user's own account, never a credential.
        detail = (result.stderr or "").strip().splitlines()
        return {"ok": False, "stdout": result.stdout or "",
                "error": (detail[0][:160] if detail else
                          f"exit {result.returncode}")}
    return {"ok": True, "stdout": result.stdout or "", "error": ""}


def install_status(key: str) -> dict:
    """Whether one CLI is installed, and what installing it would run."""
    tool = INSTALLERS_BY_KEY.get(key)
    if tool is None:
        return {"key": key, "known": False, "installed": False}
    path = resolve_binary(tool)
    return {
        "key": key,
        "known": True,
        "label": tool.label,
        "installed": bool(path),
        "path": path,
        "command": tool.install_command,
        "source": tool.install_source,
        "target": tool.install_target,
    }

#!/usr/bin/env python3
"""LIMISAW multi-vendor quota probe.

Read-only. Asks every installed agent CLI what quota IT says is left, and
reports one flat snapshot the tray app renders. Supported vendors:

* **Codex** - `codex app-server --stdio` JSON-RPC (`account/rateLimits/read`),
  one account per CODEX_HOME (`~/.codex`, `~/.codex-account2`, ...).
* **Claude Code** - `claude -p "/usage"` (0 turns, $0.00), the status-line
  bridge cache and local transcripts as fallbacks.
* **Antigravity** - `agy -p "/usage" --output-format json`, plus the IDE's own
  refusal journal. Bills Gemini models and Claude/GPT models against separate
  quota pools, so its windows carry a pool group.

Never parses auth.json, never prints tokens, never installs anything: the
`cli` block only *describes* each vendor's published install command so the
UI can show it and let the user decide.

The provider adapters under Scripts/limisaw_limits/ are vendored from
FastPrompter (src/fastprompter/core/usage_limits) so LIMISAW ships standalone.

Usage:
    python limisaw_probe.py --json            # machine-readable snapshot
    python limisaw_probe.py                   # human summary
    python limisaw_probe.py --cli-json        # CLI install status only (fast)
    python limisaw_probe.py --provider claude # one vendor only
"""
from __future__ import annotations

import argparse
import json
import re
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from limisaw_limits import cli_tools, model  # noqa: E402
from limisaw_limits.providers.antigravity import AntigravityProvider  # noqa: E402
from limisaw_limits.providers.claude import ClaudeProvider  # noqa: E402
from limisaw_limits.providers.codex import CodexProvider  # noqa: E402

# One sweep must finish well inside the app's own kill timer (90s), so a single
# wedged vendor CLI can never cost the whole snapshot. Both numbers are measured,
# not guessed: the FIRST `codex app-server` of a session pays a cold start and
# was seen answering in 14-19s, and `agy -p "/usage"` normally answers in ~3s but
# has been observed at 14s+. A 12s per-account budget turned those tails into a
# permanent "first Codex account is broken" and a phantom
# "Antigravity UNAVAILABLE".
TOTAL_BUDGET_S = 66.0
PER_ACCOUNT_BUDGET_S = 26.0

PROVIDERS = (
    ("codex", "Codex", CodexProvider),
    ("claude", "Claude Code", ClaudeProvider),
    ("antigravity", "Antigravity", AntigravityProvider),
)

WINDOW_LABELS = {
    model.FIVE_HOUR: "5h",
    model.WEEKLY: "week",
    model.MONTHLY: "month",
}


def _window_label(key: str) -> str:
    base = model.base_key(key)
    return WINDOW_LABELS.get(base, base.replace("_", " "))


def _iso(epoch) -> str | None:
    if not isinstance(epoch, (int, float)) or epoch <= 0:
        return None
    try:
        return time.strftime("%Y-%m-%dT%H:%M:%S", time.localtime(epoch))
    except Exception:
        return None


def _pct(value) -> int | None:
    if not isinstance(value, (int, float)):
        return None
    return int(round(max(0.0, min(100.0, float(value)))))


def snapshot_to_dict(index: int, provider_id: str, provider_label: str,
                     snap) -> dict:
    """One account, flattened to what the tray app needs and nothing more."""
    windows = []
    for w in model.resolved_windows(getattr(snap, "windows", ()) or ()):
        windows.append({
            "key": w.key,
            "base": model.base_key(w.key),
            "label": _window_label(w.key),
            "group": w.group or "",
            "group_label": w.group_label or "",
            "available": bool(w.available),
            "remaining_percent": _pct(w.remaining_percent),
            "resets_at": _iso(w.resets_at_epoch),
            "gated_by": w.gated_by or None,
            "assumed_full": bool(w.assumed_full),
            "duration_minutes": w.duration_minutes,
        })
    quiet = getattr(snap, "error_code", "") in model.EXPECTED_QUIET_CODES
    return {
        "index": index,
        "provider": provider_id,
        "provider_label": provider_label,
        "name": snap.account.display_name,
        "status": snap.status,
        "ok": snap.status == model.OK,
        "quiet": quiet,
        "plan": snap.plan_type,
        "error": (snap.error_summary or snap.error_code) or None,
        "windows": windows,
    }


def probe_all(only: str | None = None) -> dict:
    accounts: list[dict] = []
    deadline = time.monotonic() + TOTAL_BUDGET_S
    index = 0
    for provider_id, provider_label, factory in PROVIDERS:
        if only and only != provider_id:
            continue
        try:
            provider = factory()
            refs = provider.discover_accounts()
        except Exception as exc:  # a broken vendor must not kill the sweep
            accounts.append({
                "index": index, "provider": provider_id,
                "provider_label": provider_label, "name": provider_label,
                "status": model.ERROR, "ok": False, "quiet": False,
                "plan": None, "error": f"{type(exc).__name__}", "windows": [],
            })
            index += 1
            continue
        for ref in refs:
            if not ref.enabled:
                continue
            budget = min(PER_ACCOUNT_BUDGET_S, max(0.0, deadline - time.monotonic()))
            if budget <= 0.2:
                break
            try:
                snap = provider.probe(ref, time.monotonic() + budget)
                accounts.append(snapshot_to_dict(index, provider_id,
                                                 provider_label, snap))
            except Exception as exc:
                accounts.append({
                    "index": index, "provider": provider_id,
                    "provider_label": provider_label,
                    "name": ref.display_name, "status": model.ERROR,
                    "ok": False, "quiet": False, "plan": None,
                    "error": f"{type(exc).__name__}", "windows": [],
                })
            index += 1
        try:
            provider.shutdown()
        except Exception:
            pass
    return {
        "fetched_at": time.strftime("%Y-%m-%dT%H:%M:%S"),
        "accounts": accounts,
        "cli": cli_status(),
    }


def cli_status() -> list[dict]:
    """Install state + the vendor's own published install command.

    ``command`` is the vendor's published form, shown verbatim to the user.
    ``powershell`` is the same thing reduced to the pipeline that goes inside
    one PowerShell session, so the UI never has to nest one shell in another
    (OpenAI publishes theirs already wrapped in ``powershell -c "..."``).
    """
    out = []
    for tool in cli_tools.INSTALLERS:
        info = cli_tools.install_status(tool.key)
        command = info.get("command", "")
        inner = re.search(r'-c(?:ommand)?\s+"(.+)"\s*$', command)
        out.append({
            "key": tool.key,
            "label": info.get("label", tool.label),
            "installed": bool(info.get("installed")),
            "path": info.get("path", ""),
            "command": command,
            "powershell": inner.group(1) if inner else command,
            "source": info.get("source", ""),
            "target": info.get("target", ""),
        })
    return out


def _print_human(payload: dict) -> None:
    for acc in payload["accounts"]:
        head = f"[{acc['provider']}] {acc['name']}  {acc['status']}"
        if acc["plan"]:
            head += f"  plan={acc['plan']}"
        print(head)
        if acc["error"]:
            print(f"         error: {acc['error']}")
        for w in acc["windows"]:
            pool = f" ({w['group_label']})" if w["group_label"] else ""
            rem = "--" if w["remaining_percent"] is None else f"{w['remaining_percent']}%"
            extra = "  gated" if w["gated_by"] else ""
            print(f"         {w['label']:<6}{pool} {rem:>5}  reset={w['resets_at']}{extra}")
    print()
    for tool in payload["cli"]:
        mark = "installed" if tool["installed"] else "MISSING"
        print(f"{tool['label']:<18} {mark:<10} {tool['path'] or tool['command']}")


def main() -> int:
    ap = argparse.ArgumentParser(description="LIMISAW multi-vendor quota probe")
    ap.add_argument("--json", action="store_true", help="emit machine-readable JSON")
    ap.add_argument("--cli-json", action="store_true",
                    help="emit only the CLI install status (no vendor calls)")
    ap.add_argument("--provider", choices=[p[0] for p in PROVIDERS],
                    help="probe only this vendor")
    ap.add_argument("--write-cache", metavar="PATH",
                    help="write the JSON snapshot to PATH atomically")
    args = ap.parse_args()

    if args.cli_json:
        print(json.dumps({"cli": cli_status()}, indent=2))
        return 0

    payload = probe_all(args.provider)

    if args.write_cache:
        p = Path(args.write_cache)
        try:
            p.parent.mkdir(parents=True, exist_ok=True)
        except Exception:
            pass
        tmp = p.with_suffix(p.suffix + ".tmp")
        tmp.write_text(json.dumps(payload, indent=2, default=str), encoding="utf-8")
        tmp.replace(p)
    if args.json:
        print(json.dumps(payload, indent=2, default=str))
    elif not args.write_cache:
        _print_human(payload)
    # Exit 0 whenever at least one account answered; the tray shows "--" for
    # the rest instead of losing the whole snapshot.
    return 0 if any(a["ok"] for a in payload["accounts"]) else 1


if __name__ == "__main__":
    raise SystemExit(main())

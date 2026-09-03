#!/usr/bin/env python3
"""Functional tests for the vendored quota providers.

Run:  python tests/test_limits.py
Exit: 0 = all PASS, 1 = failures.

These are the rules where a wrong answer is invisible but wrong on screen:

1. Antigravity bills two INDEPENDENT model pools, each with its own weekly AND
   5-hour limit. A ``disabled`` 5-hour bucket is a window the pool really has —
   dropping it hid the Claude/GPT 5-hour limit entirely, and trusting its
   ``remaining_fraction: 1`` showed "100% free" on a pool that refuses work.
2. Gating is per pool: a spent Claude/GPT weekly must not zero a Gemini window.
3. A window whose own reset time has passed is full without a new probe.
"""
import json
import os
import sys

ROOT = os.path.abspath(os.path.dirname(os.path.abspath(__file__)) + os.sep + '..')
sys.path.insert(0, os.path.join(ROOT, 'Scripts'))

from limisaw_limits import model                                    # noqa: E402
from limisaw_limits.providers import _antigravity_cli as agy        # noqa: E402

fails = 0


def check(name, cond, detail=''):
    global fails
    if cond:
        print('PASS ', name, ('  -> ' + detail) if detail else '')
    else:
        fails += 1
        print('FAIL ', name, '  -> ' + detail)


# The payload `agy -p "/usage" --output-format json` returns when the Claude/GPT
# pool is spent: its 5-hour bucket is marked disabled with a full fraction.
PAYLOAD = json.loads(r'''
{"status":"SUCCESS","command":{"name":"usage","data":{"groups":[
 {"name":"Gemini Models","buckets":[
   {"id":"gemini-weekly","window":"weekly","remaining_fraction":0.6389,
    "reset_time":"2026-09-08T20:06:36Z"},
   {"id":"gemini-5h","window":"5h","remaining_fraction":0.8411,
    "reset_time":"2026-09-03T17:41:31Z"}]},
 {"name":"Claude and GPT models","buckets":[
   {"id":"3p-weekly","window":"weekly","remaining_fraction":0,
    "reset_time":"2026-09-04T16:16:03Z"},
   {"id":"3p-5h","window":"5h","disabled":true,"remaining_fraction":1}]}]}}}
''')


def by_pool(rows):
    return {(r['group'], r['key']): r for r in rows}


def main():
    rows = agy.parse_usage_payload(PAYLOAD)
    got = by_pool(rows)

    check('every pool reports both of its windows', len(rows) == 4,
          '%d windows: %s' % (len(rows), sorted(k[1] + '@' + k[0] for k in got)))
    check('the Claude/GPT pool has a 5-hour window at all',
          ('claude_and_gpt_models', 'five_hour') in got,
          'keys: ' + ', '.join(sorted(k[1] + '@' + k[0] for k in got)))

    disabled = got.get(('claude_and_gpt_models', 'five_hour'), {})
    check('a disabled bucket is flagged, not trusted',
          disabled.get('disabled') is True and disabled.get('remaining') is None,
          'disabled=%s remaining=%s' % (disabled.get('disabled'), disabled.get('remaining')))
    check('a disabled bucket never reads as free',
          disabled.get('remaining') != 100.0,
          'remaining=%s' % (disabled.get('remaining'),))

    gemini_5h = got.get(('gemini_models', 'five_hour'), {})
    check('a live bucket keeps the vendor number',
          round(gemini_5h.get('remaining') or 0) == 84,
          'gemini 5h remaining=%s' % (gemini_5h.get('remaining'),))

    # ── the app-facing shape: what LIMISAW actually renders ──
    from limisaw_limits.providers.antigravity import AntigravityProvider
    from limisaw_limits.model import AccountRef

    class FakeCli:
        @staticmethod
        def read_usage(deadline, binary='', now=None):
            return {'windows': agy.parse_usage_payload(PAYLOAD),
                    'captured_at': 1788442333.0,
                    'source': 'antigravity-cli-usage'}

    provider = AntigravityProvider()
    real = provider.__class__.__module__
    module = sys.modules[real]
    saved = module._antigravity_cli
    module._antigravity_cli = FakeCli
    try:
        account = AccountRef(provider_id='antigravity', stable_id='test',
                             display_name='Antigravity', source_kind='configured')
        snap = provider._cli_snapshot(account, float('inf'))
    finally:
        module._antigravity_cli = saved

    windows = {w.key: w for w in model.resolved_windows(snap.windows, now=1788442333.0)}
    check('the snapshot carries all four windows', len(windows) == 4,
          ', '.join(sorted(windows)))

    spent = windows.get('five_hour@claude_and_gpt_models')
    check('a disabled 5h window whose pool weekly is spent reads 0%, not "--"',
          spent is not None and spent.available and spent.remaining_percent == 0.0,
          'available=%s remaining=%s gated_by=%s' % (
              spent.available, spent.remaining_percent, spent.gated_by))
    check('and it says WHICH window locked it',
          spent is not None and spent.gated_by == 'weekly@claude_and_gpt_models',
          'gated_by=%s' % (spent.gated_by if spent else None,))

    gem5 = windows.get('five_hour@gemini_models')
    check('a spent pool does not zero the other pool',
          gem5 is not None and gem5.gated_by is None and round(gem5.remaining_percent) == 84,
          'gemini 5h remaining=%s gated_by=%s' % (
              gem5.remaining_percent, gem5.gated_by))

    gemw = windows.get('weekly@gemini_models')
    check('the untouched pool keeps its weekly number too',
          gemw is not None and round(gemw.remaining_percent) == 64,
          'gemini weekly=%s' % (gemw.remaining_percent,))

    # A pool with NO spent longer window has nothing to infer from: a disabled
    # bucket there must stay unavailable rather than being guessed at 0.
    lonely = json.loads(r'''
    {"command":{"name":"usage","data":{"groups":[
      {"name":"Claude and GPT models","buckets":[
        {"id":"3p-weekly","window":"weekly","remaining_fraction":0.5,
         "reset_time":"2026-09-04T16:16:03Z"},
        {"id":"3p-5h","window":"5h","disabled":true,"remaining_fraction":1}]}]}}}
    ''')

    class LonelyCli:
        @staticmethod
        def read_usage(deadline, binary='', now=None):
            return {'windows': agy.parse_usage_payload(lonely),
                    'captured_at': 1788442333.0,
                    'source': 'antigravity-cli-usage'}

    module._antigravity_cli = LonelyCli
    try:
        snap2 = provider._cli_snapshot(
            AccountRef(provider_id='antigravity', stable_id='t2',
                       display_name='Antigravity', source_kind='configured'),
            float('inf'))
    finally:
        module._antigravity_cli = saved
    w2 = {w.key: w for w in snap2.windows}
    idle = w2.get('five_hour@claude_and_gpt_models')
    check('a disabled window with quota left in its pool stays "--", not 0%',
          idle is not None and not idle.available and idle.remaining_percent is None,
          'available=%s remaining=%s' % (idle.available, idle.remaining_percent))

    print('---')
    print('FAILED' if fails else 'PASS', '(%d failure(s))' % fails)
    return 1 if fails else 0


if __name__ == '__main__':
    sys.exit(main())

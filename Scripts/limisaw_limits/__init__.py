"""Vendored usage-limit provider stack for LIMISAW.

Copied verbatim (imports rewritten) from FastPrompter
``src/fastprompter/core/usage_limits`` so LIMISAW ships standalone: the tray
app must work on a machine that has no FastPrompter checkout.

Read-only by contract - every provider asks the vendor's own CLI/cache what
quota is left, never touches auth files, never prints credentials.
"""

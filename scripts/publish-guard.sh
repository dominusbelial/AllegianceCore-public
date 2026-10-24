#!/usr/bin/env bash
# Public-repo push guard (replaces the private->public publish.sh gates, 2026-10-24).
# Usage: scripts/publish-guard.sh <base> <head>     (checks the commits in base..head)
# Fails on: non-noreply identities, secret patterns, personal email, /home/<user> paths in code,
# Meshy paths. Lists new/changed media and fails unless ALLOW_MEDIA=1 (set it after reviewing them).
set -eu
# The guard's own source contains the patterns it searches for, so it is excluded from the content scans.
BASE=$1; HEAD=$2; RANGE="$BASE..$HEAD"
MEDIA_RE='\.(png|jpe?g|gif|webp|bmp|tga|mp4|webm|mov|fbx|fbm|obj|glb|gltf|blend|mtl|psd)$'
SECRET_RE='sk-[A-Za-z0-9]{20,}|ghp_[A-Za-z0-9]{20,}|github_pat_|AKIA[0-9A-Z]{16}|-----BEGIN [A-Z ]*PRIVATE KEY-----|xox[bap]-[A-Za-z0-9-]{10,}|[0-9]{8,10}:[A-Za-z0-9_-]{35}|msy_[A-Za-z0-9]{10,}|hf_[A-Za-z0-9]{20,}'
fail=0; say(){ echo "[publish-guard] $*"; }
[ -z "$(git rev-list "$RANGE")" ] && { say "no new commits"; exit 0; }
BAD_ID=$(git log --format='%ae%n%ce' "$RANGE" | sort -u | grep -v 'users.noreply.github.com$' || true)
[ -z "$BAD_ID" ] || { say "non-noreply author/committer: $BAD_ID"; fail=1; }
SEC=$(git log -p "$RANGE" -- . ":(exclude)scripts/publish-guard.sh" | grep -cE "$SECRET_RE" || true)
[ "$SEC" = 0 ] || { say "$SEC secret-pattern hit(s)"; fail=1; }
PRIV=$(git log -p "$RANGE" -- . ":(exclude)scripts/publish-guard.sh" | grep -E '^\+' | grep -iE '@gmail\.com|dominusbelial@' | grep -viE 'users\.noreply\.github\.com' | wc -l)
[ "$PRIV" = 0 ] || { say "$PRIV personal-email hit(s)"; fail=1; }
NH=$(git log -p "$RANGE" -- src tests scripts tools docker .github ":(exclude)scripts/publish-guard.sh" | grep -E '^\+' | grep -cE '/home/[a-z]+/' || true)
[ "$NH" = 0 ] || { say "$NH '/home/<user>' line(s) added outside docs/"; fail=1; }
if [ -f docs/release/meshy-history-paths.txt ]; then
  MESHY=$(git diff --name-only --diff-filter=AM "$BASE" "$HEAD" | python3 -c '
import sys,fnmatch
pats=[l.strip() for l in open("docs/release/meshy-history-paths.txt") if l.strip() and not l.startswith("#")]
for f in sys.stdin.read().split():
    for p in pats:
        k,_,v=p.partition(":")
        if (k=="literal" and f==v) or (k=="glob" and fnmatch.fnmatch(f,v)): print(f); break')
  [ -z "$MESHY" ] || { say "Meshy path(s) added (never public): $MESHY"; fail=1; }
fi
BRIEFS=$(git diff --name-only --diff-filter=AM "$BASE" "$HEAD" | grep -E '(^|/)[A-Z0-9]+_BRIEF\.md$|(^|/)hermes-[^/]*-prompt\.md$' || true)
[ -z "$BRIEFS" ] || { say "delegate brief/prompt file(s) committed (internal, never public): $BRIEFS"; fail=1; }
MEDIA=$(git diff --name-only --diff-filter=AM "$BASE" "$HEAD" | grep -iE "$MEDIA_RE" || true)
if [ -n "$MEDIA" ]; then
  say "new/changed media ($(echo "$MEDIA" | wc -l)) — review: no Meshy ships, no private content:"; echo "$MEDIA" | sed 's/^/   /'
  [ "${ALLOW_MEDIA:-0}" = 1 ] || { say "set ALLOW_MEDIA=1 after reviewing the media above"; fail=1; }
fi
[ $fail = 0 ] && say "OK ($(git rev-list --count "$RANGE") commit(s))"
exit $fail

#!/usr/bin/env bash
# Verify roll + strafe end to end against a real in-process server, feeding the
# REPL via stdin with real wall-clock gaps. The server simulates a parked ship
# continuously and only *broadcasts* once a control push dirties it, so the
# first push also reveals accumulated hidden cruise — hence a warm-up push first,
# then clean same-length legs between live statuses:
#   ACTIVATE : `thrust stop` (state=0, begins continuous broadcast)
#   LEG A    : cruise-only (no strafe), 4s  -> displacement should be ~forward
#              (dominant -Z), negligible lateral X  -> proves the ship WAS
#              moving forward already, and X~0 is the control for LEG B.
#   LEG B    : `strafe left` added, 4s        -> displacement must gain a real
#              lateral (X) component on TOP of the same forward cruise, i.e.
#              the strafe bits alone produce distinct lateral movement.
#   ROLL     : `roll left` held, 4s -> the `up` vector rotates away from the
#              pre-roll sample (a real orientation change, not a static value);
#              `roll level` stops it (second sample holds a rolled up).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
OUT=/tmp/roll-strafe.out
{
    sleep 6
    printf 'thrust stop\n'                   # ACTIVATE: begin continuous broadcast
    sleep 3
    printf 'status\n'                        # P0  cruise-only baseline
    sleep 4
    printf 'status\n'                        # P1  LEG A end (X should stay ~0)
    printf 'strafe left\n'
    sleep 4
    printf 'status\n'                        # P2  LEG B end (X lateral gained)
    printf 'strafe stop\n'
    sleep 2
    printf 'status\n'                        # P3  settled (pre-roll up sample)
    printf 'thrust fwd\n'                    # COMPOSE: add forward WHILE strafe-left
    printf 'strafe left\n'                   #  stays set -> 0x60 both bits
    sleep 4
    printf 'status\n'                        # C1  after fwd+strafe together (moves)
    printf 'thrust stop\n'
    printf 'strafe stop\n'
    sleep 2
    printf 'status\n'                        # C2  settled again
    printf 'roll left\n'
    sleep 4
    printf 'status\n'                        # R1  after roll left held (up changed)
    printf 'roll level\n'
    sleep 2
    printf 'status\n'                        # R2  roll leveled (up holds, stable)
    printf 'quit\n'
} | dotnet run --project "$ROOT/src/Allegiance.ClientCore.Explorer" -- 0 verify >"$OUT" 2>&1
RC=$?
echo "dotnet run exit=$RC  (output file: $OUT)"
echo "=== status lines ==="
grep -E '^\s*\[status\]' "$OUT"

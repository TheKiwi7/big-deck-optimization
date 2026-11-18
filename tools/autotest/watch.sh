#!/usr/bin/env bash
# A Monitor's source for a battery: prints only what needs a look.
#
#   watch.sh <log> <done-marker> [--grace 300]
#
# Reads <log> (battery.ps1's output file, results/battery/<name>.log) as it grows and prints
#   - FAIL lines (a failed STEP line too when no FAIL line follows it), CHECK FAILED, BATTERY ABORT;
#   - STALL when the grace period has passed and no file under the results folder changed for 10 minutes (once per 10 minutes);
#   - the line holding <done-marker> (e.g. "BATTERY DONE"), then exits 0 (1 for "BATTERY ABORT").
# The first minutes are quiet while sessions boot: no stall before --grace seconds (default 300) after the start.
# Environment (tests): RESULTS_DIR (default: ./results next to this script), STALL_SEC (600), POLL_SEC (15).
set -u
log="${1:?usage: watch.sh <log> <done-marker> [--grace 300]}"
marker="${2:?usage: watch.sh <log> <done-marker> [--grace 300]}"
grace=300
if [ "${3:-}" = "--grace" ]; then grace="${4:?--grace needs seconds}"; fi
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
results="${RESULTS_DIR:-$here/results}"
stall="${STALL_SEC:-600}"
poll="${POLL_SEC:-15}"
start=$(date +%s)
offset=0
last_warn=0
pending=""
while true; do
    if [ -f "$log" ]; then
        size=$(wc -c < "$log")
        if [ "$size" -lt "$offset" ]; then offset=0; fi          # the log was started over
        if [ "$size" -gt "$offset" ]; then
            chunk="$(tail -c +$((offset + 1)) "$log" | head -c $((size - offset)))"
            offset=$size
            while IFS= read -r line; do
                line="${line%$'\r'}"
                # A failed STEP line is followed by its FAIL line (the detail): only that one is printed, the STEP line alone when none follows.
                if [ -n "$pending" ]; then
                    case "$line" in "FAIL "*) ;; *) echo "$pending" ;; esac
                    pending=""
                fi
                case "$line" in
                    *"$marker"*) echo "$line"; case "$line" in *ABORT*) exit 1;; *) exit 0;; esac ;;
                    "FAIL "*|"CHECK FAILED"*|"BATTERY ABORT"*) echo "$line" ;;
                    "STEP "*) if [[ "$line" =~ exit\ ([0-9]+)\ problems\ ([0-9]+) ]] && { [ "${BASH_REMATCH[1]}" != 0 ] || [ "${BASH_REMATCH[2]}" != 0 ]; }; then pending="$line"; fi ;;
                esac
            done <<< "$chunk"
        fi
    fi
    now=$(date +%s)
    if [ $((now - start)) -ge "$grace" ] && [ $((now - last_warn)) -ge "$stall" ]; then
        # Newest modification time (seconds) of any file under the results folder.
        newest=$(find "$results" -type f -printf '%T@\n' 2>/dev/null | sort -n | tail -1 | cut -d. -f1)
        newest="${newest:-0}"
        if [ $((now - newest)) -ge "$stall" ]; then
            echo "STALL no results file changed for $(((now - newest) / 60)) min ($(date +%H:%M))"
            last_warn=$now
        fi
    fi
    sleep "$poll"
done

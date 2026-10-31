#!/usr/bin/env bash
# Summarise a co-op test run from its logs: teleport bounces, state-hash
# differences, container (building/vehicle) moves, held moves, flee/rally,
# round reached, and a deduplicated WARNING/ERROR sweep.
# usage: tools/check-run.sh <host Latest.log> <guest Latest.log> [...]
for f in "$@"; do
  echo "================ $f"
  echo "-- #95 bounce (A->B then B->A for same unit within 3 s):"
  grep -a "\[DESYNC\]   moving '" "$f" | sed -E "s/^\[([0-9]+):([0-9]+):([0-9]+)\.([0-9]+)\].*moving '([^']+)' \(([0-9]+,[0-9]+)\) -> \(([0-9]+,[0-9]+)\).*/\1 \2 \3 \4 \5 \6 \7/" |
    awk '{t=$1*3600+$2*60+$3+$4/1000; u=$5; a=$6; b=$7;
          if ((u SUBSEP b SUBSEP a) in last && t-last[u,b,a]<3) { printf "  BOUNCE %s %s->%s->%s (%.2fs)\n", u,b,a,b,t-last[u,b,a]; n++ }
          last[u,a,b]=t } END { if (!n) print "  none" }'
  echo "-- hash differences / persists:"
  echo "  differs: $(grep -a -c 'State hash differs' "$f")   persists: $(grep -a -c 'Difference persists' "$f")"
  grep -a "State hash differs" "$f" | sed -E 's/.*at turn ([0-9]+:[0-9]+).*/  turn \1/' | sort | uniq -c
  echo "-- #94 container moves that left a unit outside:"
  grep -a "\[CONTAINER\]" "$f" | grep -a "stillContained=False" | cut -c1-200 | sed 's/^/  /'
  echo "-- #96 held/released/refused player moves:"
  echo "  held $(grep -a -c "Held partner's" "$f")  released $(grep -a -c "Released held" "$f")  refused-player $(grep -a -c "accepted=False 'player_squad" "$f")"
  echo "-- #97 player flee/rally:"
  grep -a -E "scenario prout|\[FLEE\] Sent .*\(owner\)|\(owner's\)|SetFaction threw|stranded in faction|at Menace.Tactical.Actor.SetFaction" "$f" | cut -c1-200 | sed 's/^/  /'
  echo "-- round reached: $(grep -a -o 'NextFaction (round [0-9]*' "$f" | tail -1)"
  echo "-- WARNING/ERROR sweep (digits normalised):"
  grep -a -E "\[(WARNING|ERROR)\]|Exception|threw|REFUSED" "$f" | sed -E 's/^\[[0-9:.]+\] //; s/[0-9]+/N/g' | cut -c1-140 | sort | uniq -c | sort -rn | head -40
done

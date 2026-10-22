import json, sys, collections
r = json.load(open(sys.argv[1], encoding="utf-8"))
print("keys:", list(r.keys()))
print("acts:", len(r.get("acts", [])), "floors:", sum(len(a) for a in r.get("map_point_history", [])))
for p in r.get("players", []):
    print("player keys:", list(p.keys()))
    deck = p.get("deck", [])
    print("player", p.get("id"), p.get("character"), "deck:", len(deck), "relics:", len(p.get("relics", [])), "potions:", len(p.get("potions", [])))
    if deck:
        print("  card keys:", list(deck[0].keys()))
        print("  sample:", json.dumps(deck[0])[:300])
        up = [c.get("current_upgrade_level", 0) or 0 for c in deck]
        print("  max upgrade:", max(up), "sum upgrades:", sum(up))
        ench = [c for c in deck if c.get("enchantment")]
        print("  enchanted:", len(ench))
        print("  top cards:", collections.Counter(c.get("id") for c in deck).most_common(8))

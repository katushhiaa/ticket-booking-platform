#!/usr/bin/env bash
# Data Distribution Matrix (ЛР7): записів на кожному шарді, частка даних і частка операцій запису
# (n_tup_ins + n_tup_upd + n_tup_del з pg_stat_user_tables з моменту останньої генерації).
set -euo pipefail
BASE="${BASE_URL:-http://localhost}"
curl -s "$BASE/api/v1/lab/sharding/distribution" | python3 -c '
import json, sys
d = json.load(sys.stdin)
print("Стратегія:", d["strategy"])
print()
print("| Шард / Вузол | Кількість записів | Частка даних (%) | Операцій запису | Частка Write Operations (%) |")
print("|---|---:|---:|---:|---:|")
sp = lambda n: format(n, ",").replace(",", " ")
for s in d["shards"]:
    print("| %s | %s | %.2f | %s | %.2f |" % (s["shard"], sp(s["orders"]), s["dataShare"], sp(s["writeOps"]), s["writeShare"]))
'

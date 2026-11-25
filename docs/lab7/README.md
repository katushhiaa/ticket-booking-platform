# ЛР7: шардинг і партиціонування — матеріали

| Файл | Зміст |
|---|---|
| [distribution-matrix.md](distribution-matrix.md) | Розподіл 200 тис. замовлень: усі стратегії × випадкові / префіксні id |
| [hot-shard.md](hot-shard.md) | Hot Shard: Modulo vs VirtualBuckets під навантаженням запису, CPU шардів |
| [cross-shard-transactions.md](cross-shard-transactions.md) | Передача замовлення між шардами: 2PC, in-doubt, відновлення, Saga |
| [resharding-model.md](resharding-model.md) | Модель решардингу: hash % N vs consistent hashing vs virtual buckets |
| [benchmark.md](benchmark.md) | Scatter-Gather і бенчмарк Single Node vs Sharded |
| [shard-replication.md](shard-replication.md) | Primary + Replica на кожному шарді: аналітика з реплік, ізоляція записів, каскад через fallback |

Скрипти — `scripts/sharding/` (`sharding-demo.sh`, `hot-shard-demo.sh`, `generate-orders.sh`, `distribution.sh`, `shard-replication-status.sh`, `resharding_sim.py`), сирі дані — `experiments/raw/lab7_*`, результати k6 — `load-tests/results/lab7_E_*`. Повний звіт і теорія — [README, розділ 12](../../README.md#12-лабораторна-робота-7-шардинг-і-партиціонування-data-scaling).

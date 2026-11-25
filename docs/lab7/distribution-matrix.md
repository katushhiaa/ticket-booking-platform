# ЛР7: Data Distribution Matrix

> Згенеровано з `experiments/raw/lab7_*` і `load-tests/results/lab7_E_*`. Повний звіт — README, розділ 12.

## 12.3. Розподіл даних (Data Distribution Baseline)

Тестовий масив: 200 000 замовлень, 20 000 користувачів, 20 подій (`POST /api/v1/lab/sharding/generate` або `scripts/sharding/generate-orders.sh`). Кожен рядок маршрутизує той самий `ShardRouter`, що й робочі запити, і вставляється на свій шард через `COPY BINARY`. Частка операцій запису — з `pg_stat_user_tables` (лічильники скидаються перед генерацією).

**Базовий розподіл (обрана стратегія `VirtualBuckets`, випадкові UUID):**

| Шард / Вузол | Кількість записів | Частка даних (%) | Частка Write Operations (%) |
|---|---:|---:|---:|
| shard-a | 99 924 | 49.96 | 49.96 |
| shard-b | 100 076 | 50.04 | 50.04 |

**Data Distribution Matrix: усі стратегії × два способи генерації id** (`experiments/raw/lab7_distribution_*.json`):

| Стратегія | Ключі користувачів | shard-a: записів | shard-b: записів | Частка даних A / B, % | Частка Write Operations A / B, % |
|---|---|---:|---:|---:|---:|
| `Modulo` | випадкові UUID v4 | 99 509 | 100 491 | 49.8 / 50.2 | 49.8 / 50.2 |
| `Modulo` | з префіксом `c0ffee03-…` | 0 | 200 000 | **0.0 / 100.0** | 0.0 / 100.0 |
| `Hash` | випадкові UUID v4 | 99 924 | 100 076 | 50.0 / 50.0 | 50.0 / 50.0 |
| `Hash` | з префіксом `c0ffee03-…` | 99 243 | 100 757 | 49.6 / 50.4 | 49.6 / 50.4 |
| `VirtualBuckets` | випадкові UUID v4 | 99 924 | 100 076 | 50.0 / 50.0 | 50.0 / 50.0 |
| `VirtualBuckets` | з префіксом `c0ffee03-…` | 99 243 | 100 757 | 49.6 / 50.4 | 49.6 / 50.4 |

Наївний `Modulo` бере перший сегмент UUID як число. Для випадкових UUID v4 він рівномірний, але id з фіксованим префіксом (так генерує k6, так виглядають id з namespace / типом об'єкта, а в UUID v7 перші 48 біт — це час) дають **100% даних на одному шарді**. Хешування всього ключа робить розподіл незалежним від того, як влаштовані id.

**Детермінованість** (`scripts/sharding/sharding-demo.sh routing`): кожне нове замовлення того самого користувача потрапляє на той самий шард, а на іншому шарді рядків цього користувача немає.

```text
=== 2.2 Детермінована маршрутизація: UserId → hash → бакет → шард ===
стратегія: VirtualBuckets
користувач ddc0c740-fde6-4679-a073-923ab1a9ef14 → shard-b bucket 27 hash 525a9d99d612611b
   POST /orders → X-Shard: shard-b
   POST /orders → X-Shard: shard-b
   рядків цього користувача на shard-a: 0
   рядків цього користувача на shard-b: 2
користувач 5bda8883-cb53-41e8-b328-4d5c5aadc10c → shard-b bucket 55 hash 3b349150ac3d6937
   POST /orders → X-Shard: shard-b
   POST /orders → X-Shard: shard-b
   рядків цього користувача на shard-a: 0
   рядків цього користувача на shard-b: 2
користувач 87f86f1f-78cc-402d-9c75-1f4d2e853522 → shard-b bucket 35 hash 547cb4910b1be8a3
   POST /orders → X-Shard: shard-b
   POST /orders → X-Shard: shard-b
   рядків цього користувача на shard-a: 0
   рядків цього користувача на shard-b: 2
```

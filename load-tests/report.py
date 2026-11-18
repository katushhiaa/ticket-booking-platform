#!/usr/bin/env python3
"""Зведення результатів k6 (load-tests/results/*.json + *.env.txt) у Markdown-таблиці та Mermaid-графіки.

Використання:
    python3 load-tests/report.py                 # усі прогони -> load-tests/results/REPORT.md
    python3 load-tests/report.py A_read_2inst_cache B_write_2inst   # лише вибрані мітки (у stdout)
"""
import json
import re
import sys
from pathlib import Path

RESULTS = Path(__file__).resolve().parent / "results"


def load(label):
    data = json.loads((RESULTS / f"{label}.json").read_text())
    env_file = RESULTS / f"{label}.env.txt"
    data["env"] = parse_env(env_file.read_text()) if env_file.exists() else {}
    return data


def parse_env(text):
    env = {}
    m = re.search(r"api_instances=(\d+)", text)
    env["instances"] = int(m.group(1)) if m else None
    m = re.search(r"cache_enabled=(\w*)", text)
    env["cache"] = (m.group(1) or "true") if m else "?"
    m = re.search(r"before=(\d+)\|(\d+)\|(\d+)\|(\d+) after=(\d+)\|(\d+)\|(\d+)\|(\d+)", text)
    if m:
        v = list(map(int, m.groups()))
        env["xact"] = v[4] - v[0]
        env["tup_read"] = (v[5] - v[1]) + (v[6] - v[2])
        env["tup_written"] = v[7] - v[3]
    m = re.search(r"keyspace_hits:(\d+) keyspace_misses:(\d+)", text)
    if m:
        env["redis_hits"], env["redis_misses"] = int(m.group(1)), int(m.group(2))

    # частка CFS-періодів, у яких контейнери API впирались у ліміт CPU
    def throttle_block(name):
        block = text.split(f"cgroup cpu.stat {name}:")[1] if f"cgroup cpu.stat {name}:" in text else ""
        block = block.split("cgroup cpu.stat")[0]
        res = {}
        for line in block.strip().splitlines():
            parts = line.split()
            if not parts:
                continue
            kv = dict(p.split("=") for p in parts[1:] if "=" in p)
            res[parts[0]] = {k: int(v) for k, v in kv.items()}
        return res

    before, after = throttle_block("before"), throttle_block("after")
    periods = throttled = 0
    for name, a in after.items():
        if "-api-" in name and name in before:
            periods += a["nr_periods"] - before[name]["nr_periods"]
            throttled += a["nr_throttled"] - before[name]["nr_throttled"]
    env["api_throttled_pct"] = 100.0 * throttled / periods if periods else None
    return env


def total_requests(d):
    return sum(r["requests"] for r in d["rows"])


def fmt(v, digits=1):
    return "—" if v is None else f"{v:,.{digits}f}".replace(",", " ")


def table(d):
    extra_cols = sorted({k for r in d["rows"] for k in r if k not in
                         {"vus", "requests", "rps", "avg", "p50", "p90", "p95", "p99", "max", "errorRate"}})
    head = "| VUs | Запитів | RPS | avg, мс | p50, мс | p95, мс | p99, мс | max, мс | Помилки, % |"
    sep = "|---:|---:|---:|---:|---:|---:|---:|---:|---:|"
    for c in extra_cols:
        head += f" {c} |"
        sep += "---:|"
    lines = [head, sep]
    for r in d["rows"]:
        line = (f"| {r['vus']} | {r['requests']} | {fmt(r['rps'])} | {fmt(r['avg'])} | {fmt(r['p50'])} | "
                f"{fmt(r['p95'])} | {fmt(r['p99'])} | {fmt(r['max'])} | {fmt(r['errorRate'] * 100, 2)} |")
        for c in extra_cols:
            v = r.get(c)
            line += f" {fmt(v * 100 if c == 'cache_hit' else v, 1 if c == 'cache_hit' else 2)}{'%' if c == 'cache_hit' and v is not None else ''} |"
        lines.append(line)
    return "\n".join(lines)


def env_line(d):
    e = d["env"]
    parts = [f"інстансів API: **{e.get('instances')}**", f"кеш: **{e.get('cache')}**"]
    if "xact" in e:
        n = total_requests(d)
        parts.append(f"транзакцій БД: **{e['xact']:,}** ({1000 * e['xact'] / n:.0f} на 1000 запитів)".replace(",", " "))
        parts.append(f"рядків прочитано: {e['tup_read']:,}".replace(",", " "))
    if "redis_hits" in e:
        h, m = e["redis_hits"], e["redis_misses"]
        parts.append(f"Redis hit ratio: {100 * h / max(1, h + m):.2f}% ({h} / {m})")
    if e.get("api_throttled_pct") is not None:
        parts.append(f"API CFS throttled: {e['api_throttled_pct']:.0f}% періодів")
    return "; ".join(parts)


def chart(title, runs, metric, y_label):
    """Mermaid xychart: одна лінія на прогін, вісь X — рівні VU. Елемент runs: (назва, дані[, метрика])."""
    runs = [(r[0], r[1], r[2] if len(r) > 2 else metric) for r in runs]
    levels = runs[0][1]["config"]["levels"]
    vmax = max(r.get(m) or 0 for _, d, m in runs for r in d["rows"])
    out = ["```mermaid", "xychart-beta", f'    title "{title}"',
           f"    x-axis \"VUs\" [{', '.join(map(str, levels))}]",
           f'    y-axis "{y_label}" 0 --> {int(vmax * 1.1) + 1}']
    for _, d, m in runs:
        by_vus = {r["vus"]: r.get(m) for r in d["rows"]}
        out.append(f"    line [{', '.join(f'{(by_vus.get(v) or 0):.1f}' for v in levels)}]")
    out.append("```")
    legend = " · ".join(f"лінія {i + 1} — `{name}`" for i, (name, _, _) in enumerate(runs))
    return "\n".join(out) + f"\n\n<sub>{legend}</sub>"


def main():
    labels = sys.argv[1:] or sorted(p.stem for p in RESULTS.glob("*.json") if not p.stem.startswith("before-fix"))
    sections = []
    for label in labels:
        d = load(label)
        sections.append(f"### {label} — {d['scenario']}\n\n{env_line(d)}\n\n{table(d)}\n")
    text = "\n".join(sections)
    if len(sys.argv) > 1:
        print(text)
    else:
        (RESULTS / "REPORT.md").write_text("# Зведення прогонів k6 (згенеровано load-tests/report.py)\n\n" + text)
        print(f"written {RESULTS / 'REPORT.md'}")


if __name__ == "__main__":
    main()

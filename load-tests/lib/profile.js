// Спільний профіль навантаження для всіх сценаріїв (ЛР5, п. 2.3).
//
// Warm-up (WARMUP_SECONDS при WARMUP_VUS) → для кожного рівня з LEVELS: перехід RAMP_SECONDS + утримання HOLD_SECONDS.
// Кожен запит отримує тег stage = warmup | ramp | vus_010 | vus_025 | ... — у звіт ідуть лише вікна утримання,
// тому метрики кожного рівня не змішуються з розігрівом і переходами.
import exec from 'k6/execution';

export const BASE_URL = __ENV.BASE_URL || 'http://localhost';
export const LEVELS = (__ENV.LEVELS || '10,25,50,100,200').split(',').map(Number);
export const HOLD = Number(__ENV.HOLD_SECONDS || 30);
const RAMP = Number(__ENV.RAMP_SECONDS || 5);
const WARMUP = Number(__ENV.WARMUP_SECONDS || 20);
const WARMUP_VUS = Number(__ENV.WARMUP_VUS || 10);

export const stageTag = (vus) => `vus_${String(vus).padStart(3, '0')}`;

const windows = (() => {
  const w = [{ from: 0, to: WARMUP, tag: 'warmup' }];
  let t = WARMUP;
  for (const level of LEVELS) {
    w.push({ from: t, to: t + RAMP, tag: 'ramp' });
    t += RAMP;
    w.push({ from: t, to: t + HOLD, tag: stageTag(level) });
    t += HOLD;
  }
  return w;
})();

/** Тег поточного ступеня за часом від старту тесту. */
export function currentStage() {
  const s = exec.instance.currentTestRunDuration / 1000;
  const w = windows.find((x) => s >= x.from && s < x.to);
  return w ? w.tag : 'tail';
}

/** Опції k6: ramping-vus зі ступенями + "порожні" пороги, щоб k6 зберіг субметрики кожного ступеня у підсумку. */
export function buildOptions(extraPerStageMetrics = []) {
  const stages = [{ duration: `${WARMUP}s`, target: WARMUP_VUS }];
  for (const level of LEVELS) {
    stages.push({ duration: `${RAMP}s`, target: level });
    stages.push({ duration: `${HOLD}s`, target: level });
  }

  const thresholds = {};
  for (const level of LEVELS) {
    const tag = stageTag(level);
    thresholds[`http_req_duration{stage:${tag}}`] = ['max>=0'];
    thresholds[`http_reqs{stage:${tag}}`] = ['count>=0'];
    thresholds[`http_req_failed{stage:${tag}}`] = ['rate>=0'];
    for (const m of extraPerStageMetrics) {
      thresholds[`${m.name}{stage:${tag}}`] = [m.type === 'rate' ? 'rate>=0' : 'count>=0'];
    }
  }

  return {
    scenarios: {
      load: { executor: 'ramping-vus', startVUs: 0, stages, gracefulRampDown: '5s', gracefulStop: '10s' },
    },
    thresholds,
    summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
    // Без цього k6 рахує 4xx/5xx як "failed", що й потрібно: очікуваних 4xx у сценаріях немає
    discardResponseBodies: false,
  };
}

/** Підсумок: таблиця по ступенях у stdout + JSON для звіту (load-tests/report.py). */
export function buildSummary(data, scenario, extraPerStageMetrics = []) {
  const rows = LEVELS.map((level) => {
    const tag = stageTag(level);
    const d = data.metrics[`http_req_duration{stage:${tag}}`]?.values || {};
    const reqs = data.metrics[`http_reqs{stage:${tag}}`]?.values?.count || 0;
    const failed = data.metrics[`http_req_failed{stage:${tag}}`]?.values?.rate || 0;
    const row = {
      vus: level,
      requests: reqs,
      rps: reqs / HOLD,
      avg: d.avg, p50: d.med, p90: d['p(90)'], p95: d['p(95)'], p99: d['p(99)'], max: d.max,
      errorRate: failed,
    };
    for (const m of extraPerStageMetrics) {
      const v = data.metrics[`${m.name}{stage:${tag}}`]?.values || {};
      row[m.name] = m.type === 'rate' ? v.rate : (v.count || 0) / HOLD;
    }
    return row;
  });

  const fmt = (x, digits = 1) => (x === undefined || x === null ? '—' : Number(x).toFixed(digits));
  const header = 'VUs   RPS      avg ms   p50 ms   p95 ms   p99 ms   max ms   errors %';
  const lines = rows.map((r) =>
    [String(r.vus).padEnd(5), fmt(r.rps).padStart(7), fmt(r.avg).padStart(8), fmt(r.p50).padStart(8),
     fmt(r.p95).padStart(8), fmt(r.p99).padStart(8), fmt(r.max).padStart(8), fmt(r.errorRate * 100, 2).padStart(10)].join(' '));

  const result = {
    scenario,
    label: __ENV.LABEL || scenario,
    config: { baseUrl: BASE_URL, levels: LEVELS, holdSeconds: HOLD, rampSeconds: RAMP, warmupSeconds: WARMUP, think: __ENV.THINK_SECONDS },
    rows,
  };

  const out = { stdout: `\n=== ${result.label} (${scenario}) ===\n${header}\n${lines.join('\n')}\n` };
  if (__ENV.RESULT_FILE) {
    out[__ENV.RESULT_FILE] = JSON.stringify(result, null, 2);
  }
  return out;
}

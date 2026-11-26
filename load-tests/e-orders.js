// Сценарій E — шардовані замовлення (ЛР7). MODE обирає тип операції:
//   write   — POST /orders (шард-локальний запис; шард обирає ShardRouter за userId)
//   local   — GET /users/{id}/orders (шард-локальне читання: 1 шард)
//   scatter — той самий запит з ?scatter=true (fan-out на всі шарди + злиття)
//   stats   — GET /orders/stats?eventId=… (аналітика: Scatter-Gather з GROUP BY на кожному шарді)
//   mixed   — 90% write + 10% stats: чи гальмує важка аналітика запис (аналітика на Primary шардів vs на репліках)
// Користувачі — ті самі «префіксні» id, що генерує /lab/sharding/generate з userIds=prefixed:
// c0ffee03-0000-4000-8000-{n}, n = 1..USERS.
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Trend } from 'k6/metrics';
import { BASE_URL, buildOptions, buildSummary, currentStage } from './lib/profile.js';

const MODE = __ENV.MODE || 'write';
const USERS = Number(__ENV.USERS || 20000);
const EVENTS = Number(__ENV.EVENTS || 20);
const THINK = Number(__ENV.THINK_SECONDS || 0.2);

const pad = (n) => String(n).padStart(12, '0');
const userId = (n) => `c0ffee03-0000-4000-8000-${pad(n)}`;
const eventId = (n) => `c0ffee0e-0000-4000-8000-${pad(n)}`;
const rand = (max) => 1 + Math.floor(Math.random() * max);

// Для mixed: латентність і пропускна здатність записів і аналітики окремо
const EXTRA = MODE === 'mixed'
  ? [{ name: 'write_ms', type: 'trend' }, { name: 'stats_ms', type: 'trend' },
     { name: 'writes', type: 'counter' }, { name: 'stats', type: 'counter' }]
  : [];
const writeMs = new Trend('write_ms', true);
const statsMs = new Trend('stats_ms', true);
const writes = new Counter('writes');
const statsCount = new Counter('stats');

export const options = buildOptions(EXTRA);

function write(tags) {
  const tickets = rand(4);
  const res = http.post(`${BASE_URL}/api/v1/orders`,
    JSON.stringify({ userId: userId(rand(USERS)), eventId: eventId(rand(EVENTS)), ticketCount: tickets, amount: tickets * 700 }),
    { headers: { 'Content-Type': 'application/json' }, tags });
  check(res, { 'order 201': (x) => x.status === 201 });
  return res;
}

function stats(tags) {
  const res = http.get(`${BASE_URL}/api/v1/orders/stats?eventId=${eventId(rand(EVENTS))}`, { tags });
  check(res, { 'stats 200': (x) => x.status === 200 });
  return res;
}

export default function () {
  const stage = currentStage();
  const tags = { stage, name: MODE };
  let res;

  if (MODE === 'mixed') {
    if (Math.random() < 0.9) {
      res = write({ stage, name: 'write' });
      writeMs.add(res.timings.duration, { stage });
      writes.add(1, { stage });
    } else {
      res = stats({ stage, name: 'stats' });
      statsMs.add(res.timings.duration, { stage });
      statsCount.add(1, { stage });
    }
  } else if (MODE === 'write') {
    res = write(tags);
  } else if (MODE === 'local' || MODE === 'scatter') {
    const scatter = MODE === 'scatter' ? '&scatter=true' : '';
    res = http.get(`${BASE_URL}/api/v1/users/${userId(rand(USERS))}/orders?limit=20${scatter}`, { tags });
    check(res, { 'orders 200': (x) => x.status === 200 });
  } else {
    res = stats(tags);
  }

  sleep(THINK);
}

export const handleSummary = (data) => buildSummary(data, `E: Orders (${MODE})`, EXTRA);

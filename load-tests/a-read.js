// Сценарій A — Read-Intensive: перегляд каталогу та схеми залу популярної події.
// Мікс запитів: 70% схема залу (1000 квитків, кешується), 20% каталог подій, 10% квитки демо-події.
// Профіль ресурсів: Cache Layer (Redis), читання PostgreSQL при MISS, серіалізація JSON, мережа.
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate } from 'k6/metrics';
import { BASE_URL, buildOptions, buildSummary, currentStage } from './lib/profile.js';

const READ_EVENT = 'c0ffee00-0000-4000-8000-0000000000a1';
const DEMO_EVENT = 'e5b87192-3c2b-4d43-9821-82d2cbb54d01';
const THINK = Number(__ENV.THINK_SECONDS || 0.2);

const cacheHit = new Rate('cache_hit');
const EXTRA = [{ name: 'cache_hit', type: 'rate' }];

export const options = buildOptions(EXTRA);

export default function () {
  const stage = currentStage();
  const r = Math.random();
  let res;
  if (r < 0.7) {
    res = http.get(`${BASE_URL}/api/v1/events/${READ_EVENT}/tickets?status=Available`, { tags: { stage, name: 'GET seat map (1000 tickets)' } });
  } else if (r < 0.9) {
    res = http.get(`${BASE_URL}/api/v1/events`, { tags: { stage, name: 'GET catalog' } });
  } else {
    res = http.get(`${BASE_URL}/api/v1/events/${DEMO_EVENT}/tickets?status=Available`, { tags: { stage, name: 'GET demo tickets' } });
  }

  check(res, { 'status 200': (x) => x.status === 200 });
  cacheHit.add(res.headers['X-Cache'] === 'HIT', { stage });
  sleep(THINK);
}

export const handleSummary = (data) => buildSummary(data, 'A: Read-Intensive', EXTRA);

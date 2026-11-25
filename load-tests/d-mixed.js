// Сценарій D — Mixed Read/Write (ЛР6, п. 2.6): типовий для продажу квитків профіль «багато читань, мало записів».
// READ_SHARE (за замовчуванням 0.8) ітерацій — читання (схема залу / каталог), решта — бронювання унікального квитка.
// Для порівняння Single Primary vs Primary + Replica запускається з вимкненим кешем (CACHE_ENABLED=false),
// щоб читання справді доходили до БД: з кешем репліка майже нічого не отримує (див. 11.6).
import http from 'k6/http';
import exec from 'k6/execution';
import { check, sleep } from 'k6';
import { Rate } from 'k6/metrics';
import { BASE_URL, buildOptions, buildSummary, currentStage } from './lib/profile.js';
import { writeTicketId } from './lib/ids.js';

const USER_ID = '8a7d183f-6712-401c-b26a-9f5e13d1fa82';
const READ_EVENT = 'c0ffee00-0000-4000-8000-0000000000a1';
const READ_SHARE = Number(__ENV.READ_SHARE || 0.8);
const THINK = Number(__ENV.THINK_SECONDS || 0.2);

// Частка читань, які обслужила репліка (заголовок X-Db-Node)
const fromReplica = new Rate('replica_reads');
const EXTRA = [{ name: 'replica_reads', type: 'rate' }];

export const options = buildOptions(EXTRA);

export default function () {
  const stage = currentStage();

  if (Math.random() < READ_SHARE) {
    const res = Math.random() < 0.5
      ? http.get(`${BASE_URL}/api/v1/events/${READ_EVENT}/tickets?status=Available`, { tags: { stage, name: 'GET seat map' } })
      : http.get(`${BASE_URL}/api/v1/events`, { tags: { stage, name: 'GET catalog' } });
    check(res, { 'read 200': (x) => x.status === 200 });
    if (res.headers['X-Db-Node']) {
      fromReplica.add(res.headers['X-Db-Node'] === 'replica', { stage });
    }
  } else {
    const n = exec.scenario.iterationInTest + 1;
    const res = http.post(`${BASE_URL}/api/v1/bookings`,
      JSON.stringify({ userId: USER_ID, ticketIds: [writeTicketId(n)] }),
      { headers: { 'Content-Type': 'application/json' }, tags: { stage, name: 'POST booking' } });
    check(res, { 'booking 201': (x) => x.status === 201 });
  }

  sleep(THINK);
}

export const handleSummary = (data) => buildSummary(data, 'D: Mixed Read/Write', EXTRA);

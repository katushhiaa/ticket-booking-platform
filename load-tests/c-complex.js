// Сценарій C — Complex Workflow: наскрізний шлях покупця.
// Логін (раз на VU) → каталог → схема залу → бронювання → оплата → перегляд броні.
// Профіль ресурсів: змішаний — кешовані читання + дві транзакції запису + читання з БД за Id.
import http from 'k6/http';
import exec from 'k6/execution';
import { check, sleep } from 'k6';
import { Counter } from 'k6/metrics';
import { BASE_URL, buildOptions, buildSummary, currentStage } from './lib/profile.js';
import { writeTicketId } from './lib/ids.js';

const USER_ID = '8a7d183f-6712-401c-b26a-9f5e13d1fa82';
const READ_EVENT = 'c0ffee00-0000-4000-8000-0000000000a1';
const TICKET_OFFSET = Number(__ENV.TICKET_OFFSET || 200000);
const THINK = Number(__ENV.THINK_SECONDS || 0.3);
const JSON_HEADERS = { 'Content-Type': 'application/json' };

const flows = new Counter('flows_completed');
const EXTRA = [{ name: 'flows_completed', type: 'counter' }];

export const options = buildOptions(EXTRA);

let token = null; // JWT на рівні VU: "користувач" логіниться один раз за сесію

export default function () {
  const stage = currentStage();
  const tags = (name) => ({ stage, name });
  const auth = () => ({ ...JSON_HEADERS, Authorization: `Bearer ${token}` });

  if (!token) {
    const login = http.post(`${BASE_URL}/api/v1/auth/login`,
      JSON.stringify({ email: 'katerina@example.com', password: 'Katerina123' }),
      { headers: JSON_HEADERS, tags: tags('POST login') });
    if (!check(login, { 'login 200': (x) => x.status === 200 })) return;
    token = login.json('accessToken');
  }

  check(http.get(`${BASE_URL}/api/v1/events`, { tags: tags('GET catalog') }), { 'catalog 200': (x) => x.status === 200 });
  sleep(THINK);

  check(http.get(`${BASE_URL}/api/v1/events/${READ_EVENT}/tickets?status=Available`, { tags: tags('GET seat map') }),
    { 'seat map 200': (x) => x.status === 200 });
  sleep(THINK);

  const n = TICKET_OFFSET + exec.scenario.iterationInTest + 1;
  const created = http.post(`${BASE_URL}/api/v1/bookings`,
    JSON.stringify({ userId: USER_ID, ticketIds: [writeTicketId(n)] }),
    { headers: auth(), tags: tags('POST booking') });
  if (!check(created, { 'booking 201': (x) => x.status === 201 })) return;
  const bookingId = created.json('id');
  sleep(THINK);

  const confirmed = http.post(`${BASE_URL}/api/v1/bookings/${bookingId}/confirm`,
    JSON.stringify({ paymentTransactionId: `LOAD-${n}` }),
    { headers: auth(), tags: tags('POST confirm') });
  if (!check(confirmed, { 'confirm 200': (x) => x.status === 200 })) return;

  check(http.get(`${BASE_URL}/api/v1/bookings/${bookingId}`, { headers: auth(), tags: tags('GET booking') }),
    { 'booking 200': (x) => x.status === 200 });
  flows.add(1, { stage });
  sleep(THINK);
}

export const handleSummary = (data) => buildSummary(data, 'C: Complex Workflow', EXTRA);

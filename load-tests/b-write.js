// Сценарій B — Write-Intensive: створення бронювань (холдування квитків).
// Кожна ітерація бронює унікальний квиток пулу WRITE (детермінований UUID за номером ітерації), тож
// конфліктів (409) за дизайном немає й навантаження чисто транзакційне.
// Профіль ресурсів: транзакції PostgreSQL (BEGIN/SELECT/INSERT/UPDATE/COMMIT), row locks, OCC, WAL I/O, пул з'єднань.
import http from 'k6/http';
import exec from 'k6/execution';
import { check, sleep } from 'k6';
import { BASE_URL, buildOptions, buildSummary, currentStage } from './lib/profile.js';
import { writeTicketId } from './lib/ids.js';

const USER_ID = '8a7d183f-6712-401c-b26a-9f5e13d1fa82';
const TICKET_OFFSET = Number(__ENV.TICKET_OFFSET || 0); // B: 1..200000, C: 200001..400000
const THINK = Number(__ENV.THINK_SECONDS || 0.2);

export const options = buildOptions();

export default function () {
  const stage = currentStage();
  const n = TICKET_OFFSET + exec.scenario.iterationInTest + 1;

  const res = http.post(`${BASE_URL}/api/v1/bookings`,
    JSON.stringify({ userId: USER_ID, ticketIds: [writeTicketId(n)] }),
    { headers: { 'Content-Type': 'application/json' }, tags: { stage, name: 'POST booking' } });

  check(res, { 'status 201': (x) => x.status === 201 });
  sleep(THINK);
}

export const handleSummary = (data) => buildSummary(data, 'B: Write-Intensive');

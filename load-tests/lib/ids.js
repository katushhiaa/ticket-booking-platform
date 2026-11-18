// Детерміновані UUID квитків пулу WRITE з load-tests/seed.sql: c0ffee02-0000-4000-8000-{n:012}
export const writeTicketId = (n) => `c0ffee02-0000-4000-8000-${String(n).padStart(12, '0')}`;

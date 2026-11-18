-- Сід для навантажувального тестування (ЛР5). Ідемпотентний: повторний запуск нічого не дублює.
-- Детерміновані UUID дозволяють k6 обчислювати ID квитка за номером ітерації (без завантаження списків з API).
--
--   Подія READ  c0ffee00-0000-4000-8000-0000000000a1 : 1 000 квитків   c0ffee01-0000-4000-8000-{n:012}   (сценарій A, схема залу)
--   Подія WRITE c0ffee00-0000-4000-8000-0000000000b1 : 400 000 квитків c0ffee02-0000-4000-8000-{n:012}   (сценарії B і C)

INSERT INTO "Venues" ("Id", "Name", "City", "Address", "TotalCapacity")
VALUES ('c0ffee00-0000-4000-8000-000000000001', 'Load Test Arena', 'Київ', 'вул. Тестова, 1', 500000)
ON CONFLICT ("Id") DO NOTHING;

INSERT INTO "Sections" ("Id", "VenueId", "Name", "Type", "Capacity")
VALUES ('c0ffee00-0000-4000-8000-000000000002', 'c0ffee00-0000-4000-8000-000000000001', 'Load Zone', 'Standing', 500000)
ON CONFLICT ("Id") DO NOTHING;

INSERT INTO "Events" ("Id", "VenueId", "Title", "Description", "StartsAt", "Status")
VALUES
  ('c0ffee00-0000-4000-8000-0000000000a1', 'c0ffee00-0000-4000-8000-000000000001',
   'Load Test: популярний концерт (читання)', 'Подія для сценарію A: 1000 квитків', now() + interval '90 days', 'Published'),
  ('c0ffee00-0000-4000-8000-0000000000b1', 'c0ffee00-0000-4000-8000-000000000001',
   'Load Test: розпродаж (запис)', 'Подія для сценаріїв B і C: 400000 квитків', now() + interval '90 days', 'Published')
ON CONFLICT ("Id") DO NOTHING;

INSERT INTO "Tickets" ("Id", "EventId", "SectionId", "SeatSlotId", "BookingId", "Price", "Status", "Version", "Barcode")
SELECT ('c0ffee01-0000-4000-8000-' || lpad(n::text, 12, '0'))::uuid,
       'c0ffee00-0000-4000-8000-0000000000a1', 'c0ffee00-0000-4000-8000-000000000002',
       NULL, NULL, 500 + (n % 5) * 100, 'Available', 0, NULL
FROM generate_series(1, 1000) AS n
ON CONFLICT ("Id") DO NOTHING;

INSERT INTO "Tickets" ("Id", "EventId", "SectionId", "SeatSlotId", "BookingId", "Price", "Status", "Version", "Barcode")
SELECT ('c0ffee02-0000-4000-8000-' || lpad(n::text, 12, '0'))::uuid,
       'c0ffee00-0000-4000-8000-0000000000b1', 'c0ffee00-0000-4000-8000-000000000002',
       NULL, NULL, 700, 'Available', 0, NULL
FROM generate_series(1, 400000) AS n
ON CONFLICT ("Id") DO NOTHING;

VACUUM ANALYZE "Tickets";

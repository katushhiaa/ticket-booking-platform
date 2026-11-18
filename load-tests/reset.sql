-- Повертає навантажувальні події до стану після сіду (між прогонами k6).
-- Демо-дані з DbSeeder (подія e5b87192-...) не чіпаються.
UPDATE "Tickets" SET "Status" = 'Available', "BookingId" = NULL, "Barcode" = NULL, "Version" = 0
WHERE "EventId" IN ('c0ffee00-0000-4000-8000-0000000000a1', 'c0ffee00-0000-4000-8000-0000000000b1')
  AND ("Status" <> 'Available' OR "BookingId" IS NOT NULL);

-- Бронювання, у яких не лишилося квитків (тобто створені навантажувальними тестами)
DELETE FROM "Bookings" b WHERE NOT EXISTS (SELECT 1 FROM "Tickets" t WHERE t."BookingId" = b."Id");

VACUUM ANALYZE "Tickets";
VACUUM ANALYZE "Bookings";

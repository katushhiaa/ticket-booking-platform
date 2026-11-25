# Concert Ticketing Engine (High-Load & Stateless Booking Platform)

Платформа для онлайн-бронювання та продажу квитків на концерти в умовах високої конкуренції за спільні ресурси (High Contention / Flash Crowds). Систему спроєктовано з урахуванням пікових навантажень на старті продажів популярних подій із забезпеченням транзакційної цілісності, захисту від подвійного продажу (Double-Booking) та **горизонтального масштабування (Stateless Architecture)**.

Підтримуються комбіновані типи майданчиків:

- **Сидячі сектори (Seated):** із фіксованими координатами крісел (ряд, номер місця).
- **Зони вільного входу (Standing / General Admission):** фан-зони з контролем загальної місткості (Capacity).

---

## Зміст

1. [Аудит та класифікація стану](#1-аудит-та-класифікація-стану-state-audit-report)
   - [1.1. Рефакторинг та декомпозиція локального стану](#11-рефакторинг-та-декомпозиція-локального-стану-stateless-architecture)
   - [1.2. Специфікація Stateless Request Flow](#12-специфікація-stateless-request-flow)
2. [Архітектура системи](#2-архітектура-системи-c4-container--component-model)
3. [Схема даних](#3-схема-даних-erd--persistence-layer)
4. [Специфікація REST API](#4-специфікація-rest-api)
5. [Матриця відповідальності (RACI)](#5-матриця-відповідальності-raci)
6. [Інструкція із детермінованого запуску системи](#6-інструкція-із-детермінованого-запуску-системи)
7. [Результати аналізу вузьких місць](#7-результати-аналізу-вузьких-місць-bottleneck-analysis)
8. [Лабораторна робота №3: балансування, відмовостійкість, масштабування](#8-лабораторна-робота-3-балансування-відмовостійкість-масштабування)
9. [Лабораторна робота №4: розподілене кешування (Redis)](#9-лабораторна-робота-4-розподілене-кешування-redis)
10. [Лабораторна робота №5: базовий рівень продуктивності](#10-лабораторна-робота-5-базовий-рівень-продуктивності-load-performance-baseline)
11. [Лабораторна робота №6: реплікація БД та Read/Write Separation](#11-лабораторна-робота-6-реплікація-бд-та-readwrite-separation)

---

## 1. Аудит та класифікація стану (State Audit Report)

Для підготовки backend-сервісу `TicketBooking.Api` до горизонтального масштабування та переведення в безстатусну архітектуру (**Stateless Application Architecture**) було проведено аудит вихідного коду проєкту. Усі компоненти та типи даних, якими оперує сервіс під час обробки запитів, класифіковано за трьома категоріями.

### Таблиця класифікації стану системи

| Категорія стану | Компоненти та елементи коду | Сховище та час життя | Статус та обґрунтування Stateless-придатності |
|---|---|---|---|
| **1. Ephemeral / Local Safe**<br/>*(Тимчасовий / локально безпечний)* | • HTTP-контекст запиту (`HttpContext`)<br/>• Вхідні DTO (`CreateBookingRequest`, `ConfirmPaymentRequest`, `RegisterRequest`, `LoginRequest`)<br/>• Локальні стекові змінні методів у контролерах<br/>• Контекст логування (Trace ID, HTTP Request logs) | RAM .NET-процесу<br/>*(Managed Heap, Gen 0 / Stack frame)* | **Безпечно.** Існує виключно протягом обробки одного HTTP-запиту. Автоматично знищується Garbage Collector (GC) після формування відповіді. Не впливає на обробку інших запитів. |
| **2. Shared / Externalized**<br/>*(Спільний / зовнішній бізнес-стан)* | • Каталог подій, секторів та крісел (`Events`, `Sections`, `SeatSlots`)<br/>• Квитки та їх транзакційні статуси (`Tickets`: `Available`, `Reserved`, `Sold`)<br/>• Бронювання та TTL-резерви (`Bookings`: `Pending`, `Confirmed`, `Expired`, `Cancelled`)<br/>• Користувачі та хеші паролів (`Users`, PBKDF2)<br/>• Сесії та авторизаційні дані | **Primary DB:** PostgreSQL 16<br/>**Distributed Cache (каталог подій):** Redis 7<br/>**Client:** JWT Bearer tokens | **Винесено у зовнішній шар.** Увесь транзакційний та бізнес-стан зберігається в централізованих зовнішніх сховищах. Будь-який інстанс API може прочитати або змінити цей стан через асинхронні запити. |
| **3. Critical Stateful Dependencies**<br/>*(Критичні stateful-залежності, підлягають вилученню)* | • In-Memory колекції (`List<T>`, `ConcurrentDictionary` для збереження сесій чи броней)<br/>• Local Locks (`lock()`, `SemaphoreSlim` для блокування квитків)<br/>• In-Memory кеш (`IMemoryCache`)<br/>• Локальні лічильники / Singleton state<br/>• Sticky Sessions / Session Cookies | **Пам'ять (RAM) конкретного контейнера/процесу** | **Повністю усунено / відсутні.** Жодні бізнес-дані, прапорці стану або внутрішньопроцесні блокування не зберігаються у пам'яті C#-процесу. Інстанси є 100% взаємозамінними (Interchangeable Workers). |

### Детальний аналіз та докази усунення In-Memory залежностей у коді

1. **Безстатусна автентифікація (Stateless JWT Authentication).**
   У файлах `AuthController.cs` та `JwtTokenService.cs` авторизація побудована на стандарті RFC 7519 (JWT). Усі дані про користувача зашифровані в підписаному токені (HMAC-SHA256). Сервер не зберігає таблиці активних сесій у локальній пам'яті (`SessionState`). Кожен екземпляр API верифікує токен самостійно за допомогою єдиного ключа `Jwt:Key`, що дозволяє будь-якому інстансу обробити запит `GET /api/v1/auth/me`.

2. **Відмова від Application-Level Locks (`lock` / `SemaphoreSlim`) на користь DB ACID та OCC.**
   У `BookingsController.cs` під час створення броні не використовуються C#-блокування. Натомість виконується відкриття асинхронної транзакції бази даних `BeginTransactionAsync()` та перевірка токена оптимістичного блокування `Version` (`[ConcurrencyCheck]` на сутності `Ticket`). Захист від Double-Booking працює на рівні PostgreSQL, а не у пам'яті .NET-процесу.

3. **Розподілений фоновий сервіс (`ExpiredBookingsCleaner`).**
   У файлі `Services/ExpiredBookingsCleaner.cs` фонова задача оновлення прострочених бронювань спирається на прямий SQL-запит до бази даних (`WHERE Status = Pending AND ExpiresAt < UtcNow`). Сервіс не зберігає таймери або списки броней у RAM, спираючись на індекс `IX_Bookings_Status_ExpiresAt`.

4. **Централізований Distributed Cache (Redis 7).**
   Каталог подій (`GET /api/v1/events`, TTL 5 хв) і доступність квитків (`GET /api/v1/events/{id}/tickets`, TTL 30 с + явна інвалідація після кожної зміни статусу квитків) кешуються в Redis за патерном Cache-Aside (`Infrastructure/Caching/ResilientCache.cs`). Кеш спільний для всіх інстансів, тому запис з одного вузла видно на інших. Якщо Redis недоступний, дані віддаються з БД (збій кешу не валить API). Деталі — [розділ 9 (ЛР4)](#9-лабораторна-робота-4-розподілене-кешування-redis).

---

## 1.1. Рефакторинг та декомпозиція локального стану (Stateless Architecture)

На основі проведеного аудиту було виконано рефакторинг архітектури backend-сервісу `TicketBooking.Api` з дотриманням принципів безстатусності (Stateless Application Architecture).

1. **Забезпечення взаємозамінності вузлів (Interchangeable Workers).**
   - Усі контролери та сервіси позбавлені локального стану (`static` колекцій, in-memory сесій чи синглтон-прапорців).
   - Сервіс спирається на безстатусну JWT-автентифікацію (HMAC-SHA256). Будь-який вузол за балансувальником Nginx здатний обробити довільний HTTP-запит, спираючись на стан у PostgreSQL 16 та заголовок `Authorization: Bearer <token>`. Redis використовується лише як кеш читань (каталог, доступність квитків), а не як сховище сесій.

2. **Декомпозиція та винесення кешу в Redis 7.**
   - Зареєстровано `AddStackExchangeRedisCache`; кеш читань живе у зовнішньому сховищі `redis:6379`, а не в пам'яті процесу (`IMemoryCache` не використовується).
   - Щоб уникнути застарілих даних (Stale Data Cache), ключі доступності квитків видаляються після кожного коміту, що змінює статус квитків (бронювання, оплата, скасування, протермінування). Сама перевірка доступності при бронюванні завжди йде в PostgreSQL (OCC), кеш на неї не впливає.

3. **Розподілена транзакційність та захист від Race Condition.**
   - Захист від подвійного бронювання (Double-Booking) перенесено на рівень **PostgreSQL ACID-транзакцій** (`BeginTransactionAsync`) та токена оптимістичного блокування **`Version` (OCC)**.
   - Оскільки перевірка та оновлення статусу квитка є атомарною операцією СУБД, цілісність даних гарантується без використання локальних C#-блокувань (`lock`, `SemaphoreSlim`).

4. **Механізм ідентифікації вузлів (`X-Instance-ID`).**
   - Впроваджено HTTP Middleware, яке автоматично додає до всіх відповідей заголовок `X-Instance-ID: Environment.MachineName`. Це дозволяє фіксувати конкретний контейнер/вузол, що обробив запит під час балансування Nginx.

---

## 1.2. Специфікація Stateless Request Flow

Для підтвердження безстатусності сервісу було зафіксовано та деталізовано сквозний потік виконання запиту (Request Flow) для ключового бізнес-сценарію — **транзакційне створення бронювання квитків (`POST /api/v1/bookings`)**.

### Схема сквозного потоку (Sequence Diagram)

```mermaid
sequenceDiagram
    autonumber
    actor Client as Клієнт / Postman
    participant Nginx as Nginx Balancer (Port 80)
    participant API1 as Backend Instance 1 (Node A)
    participant Redis as Redis 7 (Catalog Cache)
    participant Postgres as PostgreSQL 16 (Primary DB)
    participant API2 as Backend Instance 2 (Node B)

    Note over Client, Nginx: Крок 1: Створення бронювання
    Client->>Nginx: POST /api/v1/bookings + Bearer JWT
    Nginx->>API1: Round-Robin проксіювання запиту

    Note over API1: 1. Inbound & Validation<br/>2. Node ID Header (X-Instance-ID: node-a)
    Note over API1: 3. Перевірка JWT-підпису (HMAC-SHA256),<br/>без звернення до Redis

    API1->>Postgres: 4. Read Context: SELECT * FROM Tickets WHERE Id IN (...)
    Postgres-->>API1: Tickets Found (Status = Available)

    API1->>Postgres: 5. Atomic Mutation: BeginTransactionAsync()
    API1->>Postgres: UPDATE Tickets SET Status='Reserved', Version=Version+1
    API1->>Postgres: INSERT INTO Bookings (Status='Pending', ExpiresAt=15m)
    API1->>Postgres: CommitAsync()
    Postgres-->>API1: Transaction Committed

    API1-->>Client: 201 Created (X-Instance-ID: node-a, JSON Booking)
    Note over API1: 6. Zero Memory Retention<br/>(Garbage Collection clears Request Context)

    Note over Client, Nginx: Крок 2: Підтвердження оплати (Interchangeability Test)
    Client->>Nginx: POST /api/v1/bookings/{id}/confirm + Bearer JWT
    Nginx->>API2: Round-Robin проксіювання запиту

    Note over API2: 1. Node ID Header (X-Instance-ID: node-b)
    API2->>Postgres: 2. Read Context: SELECT * FROM Bookings WHERE Id = {id}
    Postgres-->>API2: Booking Found (Status = Pending, ExpiresAt > UtcNow)

    API2->>Postgres: 3. Atomic Mutation: UPDATE Bookings & Tickets (Status = Sold)
    API2-->>Client: 200 OK (X-Instance-ID: node-b, Barcodes)
```

### Фіксація параметрів та дій за вимогами

1. **Вхідні параметри (Inbound Parameters & Token).**
   - HTTP-маршрут: `POST /api/v1/bookings`
   - Header: `Authorization: Bearer <jwt_access_token>` (самодостатній токен з `UserId` та `Email`).
   - Body: `{"userId": "8a7d183f-...", "ticketIds": ["7b0a793c-..."]}`

2. **Вичитування контексту зі Shared Storage.**
   - Перевірка токена виконується без звернення до локальної пам'яті (криптографічний верифікатор `JwtOptions`).
   - Читання статусу квитків з **PostgreSQL 16** (`SELECT ... FROM Tickets WHERE Id = ANY(@ids)`). Redis на цьому кроці не використовується.

3. **Атомарні зміни стану.**
   - Відкриття явної транзакції `BeginTransactionAsync()`.
   - Оновлення статусу квитків із токеном версіонування `Version += 1` (OCC-блокування).
   - Фіксація броні у СУБД та `CommitAsync()`.

4. **Відсутність залишкових мутацій у пам'яті (Zero Memory Retention).**
   - Після відправки відповіді `201 Created` усі об'єкти запиту виходять із зони видимості й утилізуються Garbage Collector (GC).
   - У RAM `Instance 1` не залишається локальних локів або прапорців броні.
   - Наступний запит `POST /confirm` перенаправляється на `Instance 2`, який зчитує створену бронь напряму з PostgreSQL і успішно завершує оплату.

---

## 2. Архітектура системи (C4 Container & Component Model)

Архітектурна схема з кількома інстансами API за балансувальником Nginx (upstream `api_servers`, алгоритм Round Robin за замовчуванням; без sticky sessions). Усі інстанси ходять у спільний PostgreSQL 16 (бізнес-стан) і Redis 7 (розподілений кеш); жодного стану в пам'яті контейнера немає, тому будь-який інстанс може обробити будь-який запит.

З ЛР6 база — це топологія **Primary + Read Replica**: записи й строгі читання йдуть на Primary, читання з допустимою eventual consistency — на репліку, яка асинхронно програє WAL Primary. Маршрут обирає `DbRouter` у шарі доступу до даних (розділ [11](#11-лабораторна-робота-6-реплікація-бд-та-readwrite-separation)).

```mermaid
flowchart TB
    classDef person fill:#08427b,stroke:#073b6f,color:#fff;
    classDef container fill:#1168bd,stroke:#0e5296,color:#fff;
    classDef component fill:#1f77b4,stroke:#0e5296,color:#fff;
    classDef db fill:#2b82c9,stroke:#1168bd,color:#fff;
    classDef infra fill:#6c757d,stroke:#495057,color:#fff;
    classDef ext fill:#888888,stroke:#555555,color:#fff;

    Customer["<b>Клієнт</b><br/><small>[Person: Web Browser / Mobile App]</small><br/>Переглядає афішу, обирає місця та оплачує квитки"]:::person

    subgraph SystemBoundary ["Ticket Booking System [System Boundary]"]

        Nginx["<b>Nginx Load Balancer</b><br/><small>[Container: nginx:1.27, порт 80]</small><br/>upstream api_servers: Round Robin (за замовч.)<br/>passive health: max_fails=2, fail_timeout=10s"]:::infra

        subgraph ApiCluster ["Ticket Booking API [Container: ASP.NET Core (.NET 9), replicas: 2]"]
            direction TB
            subgraph Node1 ["Instance 1 (ticket-booking-api-1)"]
                Ctrl1["<b>API Controllers</b><br/><small>[Component]</small><br/>Events, Bookings, Auth"]:::component
                Svc1["<b>Business & Domain Layer</b><br/><small>[Component]</small><br/>TTL резервів, холдування квитків"]:::component
            end
            subgraph Node2 ["Instance 2 (ticket-booking-api-2)"]
                Ctrl2["<b>API Controllers</b><br/><small>[Component]</small><br/>Events, Bookings, Auth"]:::component
                Svc2["<b>Business & Domain Layer</b><br/><small>[Component]</small><br/>TTL резервів, холдування квитків"]:::component
            end
        end

        Db[("<b>Primary Database</b><br/><small>[Container: PostgreSQL 16, ticket-booking-db]</small><br/>Усі записи й транзакції, strong-читання<br/>(логін, статус оплати)")]:::db
        Replica[("<b>Read Replica</b><br/><small>[Container: PostgreSQL 16 Hot Standby, ticket-booking-db-replica]</small><br/>Лише читання: каталог, схема залу,<br/>історія бронювань (Read-Your-Writes)")]:::db
        Redis[("<b>Distributed Cache</b><br/><small>[Container: Redis 7, 128 МБ, allkeys-lru]</small><br/>Cache-Aside: каталог подій, доступність квитків<br/>(спільний для інстансів, без persistence)")]:::db

    end

    PaymentGateway["<b>Payment Gateway</b><br/><small>[External System: LiqPay / Stripe API]</small><br/>Обробка безготівкових транзакцій"]:::ext

    Customer -->|"HTTP / REST (JSON), JWT<br/>http://localhost (порт 80)"| Nginx
    Nginx -->|"upstream api_servers (API 1)"| Ctrl1
    Nginx -->|"upstream api_servers (API 2)"| Ctrl2
    Ctrl1 --> Svc1
    Ctrl2 --> Svc2
    Svc1 -->|"W + strong R: TCP 5432, OCC + транзакції"| Db
    Svc2 -->|"W + strong R: TCP 5432, OCC + транзакції"| Db
    Svc1 -.->|"eventual / RYW R (DbRouter)"| Replica
    Svc2 -.->|"eventual / RYW R (DbRouter)"| Replica
    Db ==>|"WAL streaming (async), replicator"| Replica
    Svc1 -->|"TCP 6379: GET/SET (Cache-Aside), DEL після коміту"| Redis
    Svc2 -->|"TCP 6379: GET/SET (Cache-Aside), DEL після коміту"| Redis
    Svc1 -->|"HTTPS / REST (TLS 1.3)<br/>Верифікація оплати"| PaymentGateway
    Svc2 -->|"HTTPS / REST (TLS 1.3)<br/>Верифікація оплати"| PaymentGateway
```

## 3. Схема даних (ERD & Persistence Layer)

Модель нормалізована до 3NF. Топологія майданчика (`Venue`, `Section`, `SeatSlot`) строго відділена від комерційних сутностей події (`Event`, `Ticket`), що дозволяє повторно використовувати конфігурацію майданчика для різних концертів без дублювання записів геометрії.

```mermaid
erDiagram
    VENUE ||--o{ SECTION : "has"
    VENUE ||--o{ EVENT : "hosts"
    SECTION ||--o{ SEAT_SLOT : "contains (if Seated)"
    SECTION ||--o{ TICKET : "categorizes"
    EVENT ||--o{ TICKET : "offers"
    SEAT_SLOT ||--o{ TICKET : "maps to (nullable)"
    USER ||--o{ BOOKING : "places"
    BOOKING ||--o{ TICKET : "includes"

    VENUE {
        uuid Id PK
        varchar Name
        varchar City
        varchar Address
        int TotalCapacity
    }

    SECTION {
        uuid Id PK
        uuid VenueId FK
        varchar Name
        varchar Type "Seated | Standing"
        int Capacity
    }

    SEAT_SLOT {
        uuid Id PK
        uuid SectionId FK
        int RowNumber
        int SeatNumber
    }

    EVENT {
        uuid Id PK
        uuid VenueId FK
        varchar Title
        text Description
        timestamptz StartsAt
        varchar Status "Draft | Published | Completed | Cancelled"
    }

    TICKET {
        uuid Id PK
        uuid EventId FK
        uuid SectionId FK
        uuid SeatSlotId FK "Nullable"
        uuid BookingId FK "Nullable"
        numeric Price
        varchar Status "Available | Reserved | Sold"
        int Version "Concurrency Token"
        varchar Barcode
    }

    BOOKING {
        uuid Id PK
        uuid UserId FK
        varchar Status "Pending | Confirmed | Cancelled | Expired"
        numeric TotalAmount
        timestamptz ExpiresAt
        timestamptz CreatedAt
        varchar PaymentTransactionId "Nullable"
    }

    USER {
        uuid Id PK
        varchar Email "Unique"
        varchar FullName
        varchar PasswordHash "PBKDF2"
        timestamptz CreatedAt
    }
```

### Ключові оптимізації бази даних

- **Фан-зони:** для квитків стоячих секторів поле `SeatSlotId` встановлюється в `NULL`, а контроль переповнення виконується на рівні квоти сектора `Section.Capacity`.
- **Індексація:** складений індекс `IX_Tickets_EventId_Status` оптимізує фільтрацію доступних квитків під час перегляду схеми залу.
- **Concurrency Check:** поле `Version` у сутності `Ticket` реалізує токен оптимістичного блокування (OCC) для контролю паралельного доступу.
- **TTL резервів:** індекс `IX_Bookings_Status_ExpiresAt` використовується фоновим сервісом `ExpiredBookingsCleaner`, який кожні 30 с переводить прострочені `Pending`-броні в `Expired` і повертає квитки у продаж.
- **Унікальність:** `IX_Users_Email` (логін) та `IX_Tickets_Barcode` (штрихкод квитка).

---

## 4. Специфікація REST API

Інтерактивна документація ендпоінтів та тестування доступні через Swagger UI:

- **Swagger URL:** <http://localhost/swagger> (через Nginx)

### Перелік контрактів системи

| Метод | Маршрут | Опис | Відповідає |
|---|---|---|---|
| `GET` | `/api/v1/events` | Отримання каталогу подій з фільтрацією за містом | Катерина |
| `GET` | `/api/v1/events/{id}/tickets` | Перегляд схеми залу та доступних квитків за подією | Катерина |
| `POST` | `/api/v1/bookings` | Транзакційне холдування квитків (TTL 15 хв, захист від Race Condition) | Напарник |
| `POST` | `/api/v1/bookings/{id}/confirm` | Підтвердження оплати, генерація штрихкодів, статус `Sold` | Напарник |
| `DELETE` | `/api/v1/bookings/{id}` | Скасування бронювання та повернення квитків у продаж | Напарник |
| `GET` | `/api/v1/bookings/{id}` | Перегляд бронювання з квитками та залишком TTL | Напарник |
| `GET` | `/api/v1/users/{userId}/bookings` | Історія бронювань користувача (ЛР6: Read-Your-Writes, репліка або Primary) | Напарник |
| `POST` | `/api/v1/auth/register` | Реєстрація користувача (пароль хешується PBKDF2), повертає JWT | Напарник |
| `POST` | `/api/v1/auth/login` | Автентифікація за email/паролем, повертає JWT (HMAC-SHA256, 60 хв) | Напарник |
| `GET` | `/api/v1/auth/me` | Профіль поточного користувача (`Authorization: Bearer <token>`) | Напарник |
| `GET` | `/health` | Health check для оркестрації контейнерів | Напарник |

### Коди відповідей бронювання

| Ендпоінт | Успіх | Помилки |
|---|---|---|
| `POST /api/v1/bookings` | `201 Created` + `Location` | `400` валідація / квитки з різних подій, `404` користувач або квиток не існує, `409` квиток уже `Reserved`/`Sold` (Double-Booking) або паралельний конфлікт `Version` |
| `POST /api/v1/bookings/{id}/confirm` | `200 OK` + штрихкоди `TCK-XXXXXXXX` | `400` валідація, `404` бронь не існує, `409` статус не `Pending` або вичерпано TTL (бронь → `Expired`, квитки → `Available`) |
| `DELETE /api/v1/bookings/{id}` | `204 No Content` | `404` бронь не існує, `409` бронь уже `Confirmed` / `Cancelled` / `Expired` |

### Захист від Double-Booking

1. Бронювання виконується в явній транзакції (`BeginTransactionAsync`).
2. Перевіряється, що **всі** квитки мають статус `Available`; інакше — `RollbackAsync` і `409 Conflict`.
3. Під час оновлення квитків `Version += 1`. Оскільки `Version` позначено `[ConcurrencyCheck]`, EF Core генерує `UPDATE ... WHERE "Id" = @id AND "Version" = @old`. Якщо дві транзакції одночасно прочитали той самий вільний квиток, друга після коміту першої оновить 0 рядків → `DbUpdateConcurrencyException` → `409 Conflict`. Таким чином подвійний продаж неможливий навіть за гонки між перевіркою і записом.

### Input Validation (FluentValidation)

| DTO | Правила |
|---|---|
| `CreateBookingRequest` | `UserId` ≠ `Guid.Empty`; `TicketIds` не `null` і не порожній; не більше **4** квитків в одні руки; без дублікатів; жоден id ≠ `Guid.Empty` |
| `ConfirmPaymentRequest` | `PaymentTransactionId` обов'язковий, 6–100 символів, лише `[A-Za-z0-9_-]` |
| `RegisterRequest` | коректний email ≤ 255; пароль 8–128 символів, щонайменше одна літера й одна цифра; `FullName` обов'язковий |
| `LoginRequest` | коректний email, пароль обов'язковий |

Валідатори підключені глобальним фільтром `FluentValidationFilter` — контролери отримують уже перевірені дані.

### Формат помилок (RFC 7807 ProblemDetails)

Усі помилки (валідація, 401/404/409, неперехоплені винятки) повертаються як `application/problem+json`. Глобальний `GlobalExceptionHandler` (`IExceptionHandler` + `UseExceptionHandler`) не віддає стек-трейси назовні.

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.10",
  "title": "Квитки вже заброньовані або продані",
  "status": 409,
  "detail": "Один або кілька вибраних квитків недоступні для бронювання.",
  "instance": "POST /api/v1/bookings",
  "traceId": "00-...",
  "unavailableTickets": [ { "ticketId": "7b0a793c-...", "status": "Reserved" } ]
}
```

### Тестові дані (seed)

| Сутність | Значення |
|---|---|
| Користувач | `8a7d183f-6712-401c-b26a-9f5e13d1fa82`, `katerina@example.com` / пароль `Katerina123` |
| Подія | `e5b87192-3c2b-4d43-9821-82d2cbb54d01` — «Океан Ельзи. Світовий тур» |
| Квитки | 2 місця в партері (850 грн) + 30 квитків фан-зони (600 грн) |

### Колекція запитів для захисту

- `postman/TicketBooking.postman_collection.json` — імпортувати в Postman (File → Import). Містить 4 папки: сценарій захисту за регламентом, негативні кейси, авторизацію та тест персистентності. ID події/квитків/броней зберігаються у змінні колекції автоматично, кожен запит має автотести (вкладка *Test Results*).
- `TicketBooking.Api.http` — той самий сценарій для VS Code REST Client / Visual Studio.

---

## 5. Матриця відповідальності (RACI)

Розподіл інженерних завдань між учасниками команди згідно з вимогами лабораторних робіт № 1 та № 2:

| Етап / Модуль розробки | Катерина | Напарник |
|---|---|---|
| **Проєктування домену, структури секторів та ER-моделі** | **A / R** | C |
| **Шар персистентності (EF Core, Npgsql, DDL, Seeder)** | **A / R** | I |
| **Модуль каталогу подій (`EventsController`)** | **A / R** | I |
| **Інтеграція та налаштування OpenAPI / Swagger UI** | **A / R** | I |
| **Проведення аудиту стану (State Audit Report) та класифікація за 3 категоріями** | **A / R** | C |
| **Рефакторинг Stateless Architecture та Middleware `X-Instance-ID`** | **A / R** | I |
| **Специфікація Stateless Request Flow (Sequence Diagram)** | **A / R** | I |
| **Модуль бронювання (`BookingsController`) та автентифікації (`AuthController`)** | C | **A / R** |
| **Input Validation (FluentValidation) та глобальний Middleware помилок** | I | **A / R** |
| **Контейнеризація, Nginx Balancer та `docker-compose.yml` (`--scale api=2`)** | I | **A / R** |
| **Resilience & Cross-Instance Consistency Testing (`docker kill`)** | I | **A / R** |
| **Аналіз вузьких місць (Bottleneck Analysis за шаблоном)** | C | **A / R** |
| **Колекція API-запитів (Postman/Bruno з негативними кейсами)** | I | **A / R** |

---

## 6. Інструкція із детермінованого запуску системи

Потрібні лише Docker та Docker Compose (локальні .NET SDK, PostgreSQL і Redis не потрібні). Порти `80` (Nginx) і `5432` (PostgreSQL) мають бути вільні.

```bash
# 1. Клонування репозиторію
git clone https://github.com/katushhiaa/ticket-booking-platform.git
cd ticket-booking-platform

# 2. Збірка і запуск: PostgreSQL 16, Redis 7, 2 інстанси API та Nginx
docker compose up -d --build --scale api=2

# 3. Перевірка статусу: db і redis — healthy; api-1, api-2, nginx — Up
docker compose ps
docker compose logs api
```

- **Після `--scale`** перезапустіть Nginx (`docker compose restart nginx`): він запам'ятовує адреси upstream при старті, тому без перезапуску бачить лише старі репліки.
- **Топологія:** клієнт → Nginx (`http://localhost`, порт 80) → `api` (2 репліки, Round-Robin) → спільні PostgreSQL та Redis. Порт API напряму назовні не публікується: усі запити йдуть через Nginx.
- **Перевірка балансування:** кожна відповідь містить заголовок `X-Instance-ID` (ID контейнера, що обробив запит). Послідовні запити до `http://localhost/api/v1/events` мають чергувати два різні значення:

  ```bash
  for i in 1 2 3 4; do curl -s -D - -o /dev/null http://localhost/api/v1/events | grep -i x-instance-id; done
  ```

- **Swagger UI:** <http://localhost/swagger>
- При старті API сам створює схему (`EnsureCreated`) і заповнює тестові дані (див. розділ «Тестові дані (seed)»).
- **Тестовий логін:** `POST /api/v1/auth/login` з `{"email": "katerina@example.com", "password": "Katerina123"}` → JWT; для `GET /api/v1/auth/me` додати заголовок `Authorization: Bearer <token>`.
- **Postman:** File → Import → `postman/TicketBooking.postman_collection.json`, далі Run collection (папки виконувати по порядку 01 → 02 → 03 → 05; змінні зберігаються між запитами). Колекційний тест перевіряє `X-Instance-ID` у кожній відповіді.
- **Newman (CLI):**

  ```bash
  npx newman run postman/TicketBooking.postman_collection.json \
    --folder "01 Сценарій захисту (регламент викладача)" \
    --folder "02 Негативні кейси (валідація, 404, 409)" \
    --folder "03 Авторизація (JWT)" \
    --folder "05 Cross-Instance (2 репліки за Nginx)"
  ```

- **Реплікація БД (ЛР6):** разом зі стеком стартує `ticket-booking-db-replica` (порт 5433 на хості), яка при першому запуску сама знімає копію Primary. Стан: `scripts/replication-status.sh`; з якого вузла прочитано відповідь — заголовки `X-Db-Node` / `X-Db-Route`. Деталі — [розділ 11](#11-лабораторна-робота-6-реплікація-бд-та-readwrite-separation).
- **Скидання бази (чистий стан перед повторним прогоном):**

  ```bash
  docker compose down -v && docker compose up -d --build
  ```

- **Тест персистентності:** створити бронювання, виконати `docker compose restart db`, дочекатися `healthy` у `docker compose ps` і перевірити `GET /api/v1/bookings/{id}` (статус `Pending`) та `GET /api/v1/events/{eventId}/tickets` (квиток `Reserved`). Дані зберігаються у volume `pgdata`; «мертві» з'єднання в пулі Npgsql після рестарту БД перевіряються й замінюються автоматично (`Infrastructure/PooledConnectionValidator.cs`).
- **Сценарій А (Cross-Instance Consistency):** логін → створення броні → перегляд → оплата. Запити обробляють різні інстанси (`X-Instance-ID` у відповідях), але стан однаковий: броня, створена на одному вузлі, видна і підтверджується на іншому. Папка `05` у Postman робить це автоматично.
- **Сценарій Б (Instance Loss):** створити бронь, знайти інстанс, що її обробив, вбити його `docker kill ticket-booking-api-1`, одразу підтвердити оплату. Запит проходить через уцілілий `api-2`, квиток стає `Sold`. Вбитий контейнер Docker сам не піднімає (ручна зупинка вимикає `restart`), тому повернути другу репліку можна так: `docker compose up -d --scale api=2`.
- **Зупинка:** `docker compose down` (дані зберігаються), `docker compose down -v` (разом з даними).

### 6.1. Інтеграційні тести

Проєкт `tests/TicketBooking.IntegrationTests` (xUnit + `WebApplicationFactory` + Testcontainers). Кожен запуск сам піднімає тимчасові `postgres:16-alpine` і `redis:7-alpine` у Docker, стартує справжнє API в пам'яті й ганяє запити по HTTP; між тестами база скидається до стану після сідера.

Покрито: успішне бронювання, Double-Booking (у т.ч. 20 паралельних запитів → рівно один `201`), валідацію (`400`), `404`/`409`, оплату і штрихкоди, TTL (прострочена бронь → `409`, квитки знову `Available`), скасування, авторизацію (JWT), кеш (Cache-Aside, інвалідація, gzip, fallback без Redis — див. 9.8).

```bash
# Варіант 1: повністю в Docker (потрібен лише Docker)
docker compose --profile tests run --rm tests

# Варіант 2: локально (потрібні .NET 9 SDK/runtime і запущений Docker)
dotnet test tests/TicketBooking.IntegrationTests
```

> **Примітка.** Варіант 1 збирає проєкт усередині Linux-контейнера й перезаписує `bin/`/`obj/`; перед локальним `dotnet test` може знадобитися `dotnet restore`.

---

## 7. Результати аналізу вузьких місць (Bottleneck Analysis)

### 7.1. Row Lock Contention (транзакційне блокування при купівлі популярних місць)

- **Точка відмови:** таблиця `Tickets` під час одночасного виконання транзакцій бронювання (`POST /api/v1/bookings`) за наявності тисяч запитів на обмежений пул квитків.
- **Причина:** конкуренція за ексклюзивне блокування рядків у реляційній базі даних. У разі використання песимістичного блокування виникає черга очікування локів (Lock Wait Timeout) і ризик виникнення Deadlock.
- **Вплив:** різке зростання p99-затримки відповіді з <100 мс до кількох секунд, масові помилки `504 Gateway Timeout` та `409 Conflict`.
- **Шляхи оптимізації:** використання черги повідомлень (RabbitMQ/Kafka) для серіалізації запитів на бронювання або застосування in-memory сховища (Redis) з атомарними операціями через Lua-скрипти перед записом у реляційну БД.

### 7.2. Database Connection Pool Exhaustion (вичерпання пулу з'єднань БД)

- **Точка відмови:** пул підключень Npgsql / PostgreSQL (`Max Pool Size`).
- **Причина:** під час сплеску трафіку (Flash Crowd) кількість одночасних HTTP-запитів значно перевищує ліміт пулу з'єднань до бази даних (за замовчуванням 100 підключень).
- **Вплив:** потоки ASP.NET Core блокуються в очікуванні вільного підключення з пулу, що призводить до вичерпання пулу потоків веб-сервера (Thread Pool Starvation) і відмови всього API.
- **Шляхи оптимізації:** впровадження connection pooler (PgBouncer) на рівні інфраструктури, агресивне кешування незмінних даних (каталог подій, геометрія залу) на рівні Redis/CDN, скорочення часу утримання відкритих транзакцій до мінімуму.

### 7.3. Read Amplification & I/O Overhead при читанні схеми залу

- **Точка відмови:** дискова підсистема СУБД (Disk I/O) під час масового опитування схеми квитків (`GET /api/v1/events/{id}/tickets`).
- **Причина:** користувачі постійно оновлюють сторінку вибору місць, генеруючи важкі `SELECT`-запити з `JOIN` до таблиць `Sections`, `SeatSlots` і `Tickets`.
- **Вплив:** вичерпання буферного пулу PostgreSQL (`shared_buffers`), зростання черги читання з диска, деградація продуктивності всієї системи.
- **Шляхи оптимізації:** використання HTTP-кешування із заголовками `ETag` / `If-None-Match`, оновлення стану зайнятих місць на клієнті через Server-Sent Events (SSE) або WebSockets замість частих опитувань (polling).

---

## 8. Лабораторна робота №3: балансування, відмовостійкість, масштабування

Усі числа нижче отримано на одному MacBook (Docker Desktop, спільна VM на всі контейнери) інструментом ApacheBench (`ab`): 2000 запитів при паралельності 50 до `GET /api/v1/events/{eventId}/tickets` (на момент ЛР3 читав PostgreSQL напряму; кеш додано в ЛР4). Сирі виводи лежать у `experiments/raw/`.

> **Застереження про точність.** На одній машині шум між прогонами ±20–30%, а між серіями, виконаними в різний час, значення помітно відрізняються. Тому порівнюються лише прогони **однієї серії**, а висновки є напрямними, а не абсолютними. Для точних цифр потрібні окремі хости.

### 8.1. Алгоритми балансування (пункт 2.3)

Конфіги лежать у `nginx/algorithms/`. Перемикання: `scripts/switch-balancer.sh <алгоритм>` (копіює конфіг в `nginx/active/default.conf` і перезавантажує Nginx). Замір: `scripts/bench.sh <мітка>`.

| Алгоритм | Конфіг | Запис у upstream | Поведінка |
|---|---|---|---|
| **Round Robin** (базовий) | `round_robin.conf` | `server api:8080` | По черзі на кожен сервер; розподіл рівний (≈50/50) |
| **Least Connections** | `least_conn.conf` | `least_conn;` | Новий запит — на сервер з найменшою кількістю активних з'єднань |
| **IP Hash** | `ip_hash.conf` | `ip_hash;` | Хеш від IP клієнта: один клієнт → один сервер (псевдо-sticky) |

Результати (2 інстанси, пул Npgsql = 20, два прогони кожного алгоритму):

| Алгоритм | RPS (прогін 1 / 2) | Середнє RPS | p99, мс | Розподіл запитів |
|---|---|---|---|---|
| Round Robin | 1390 / 1102 | **1246** | 132 / 133 | ≈50/50 (1001 / 999 у прогоні з пулом 50) |
| Least Connections | 1404 / 1529 | **1467** | 125 / 107 | ≈50/50 (998 / 1002 у прогоні з пулом 50) |
| IP Hash | 1552 | 1552 | 81 | **2000 / 0** (усі запити з одного IP → один вузол) |

**Висновок.** На симетричних інстансах Round Robin і Least Connections дають однаковий розподіл, різниця в RPS у межах шуму. **IP Hash** на тестовому стенді зламаний за дизайном: `ab`, `curl` та Newman працюють з одного IP, тож весь трафік іде на один вузол, інший простоює. Для реальних клієнтів (багато IP) розподіл був би рівномірнішим, але при NAT (багато користувачів за одним IP) ситуація повторюється. Тому для stateless API за Nginx кращий Round Robin або Least Connections.

### 8.2. Асиметричне навантаження (затримка на одному інстансі)

Експеримент: пул із трьох серверів — `api` (2 репліки) і `api-slow`, у якого кожен API-запит затримується на 300 мс (`Lab:SimulatedLatencyMs=300`, сервіс у профілі `experiment`). Конфіги: `round_robin_asymmetric.conf`, `least_conn_asymmetric.conf`.

| Алгоритм | RPS | p50, мс | p95, мс | p99, мс | Запитів на повільний вузол |
|---|---|---|---|---|---|
| Round Robin | 400 | 10 | 323 | **639** | 668 з 2000 (33%) |
| Least Connections | **1136** | 14 | 309 | **344** | 316 з 2000 (16%) |

**Висновок.** Round Robin шле третину трафіку на повільний вузол незалежно від його стану, і кожен такий запит тримає клієнта 300+ мс. Least Connections бачить, що повільний вузол має більше активних з'єднань, і відправляє йому вдвічі менше. Результат: RPS вищий приблизно у 2.8 раза, p99 нижчий на 46% (639 → 344 мс). Цифри p50 майже однакові, бо більшість запитів обслуговують швидкі вузли; різниця видна саме в хвості розподілу, тобто в p99.

### 8.3. Passive Health Checks та відмовостійкість (пункт 2.4)

Директиви в усіх конфігах:

```nginx
upstream api_servers {
    server api:8080 max_fails=2 fail_timeout=10s;
}
# у location /
proxy_next_upstream error timeout http_500 http_502 http_503;   # у ЛР6 http_503 прибрано, див. 11.5
proxy_next_upstream_tries 3;
```

Як працює: після 2 помилок поспіль сервер виключається з ротації на 10 с (`max_fails`/`fail_timeout`), а `proxy_next_upstream` повторює запит на наступному сервері. Повтор робиться **до** того, як відповідь пішла клієнту, тож клієнт помилки не бачить.

Сценарій: 8000 запитів при паралельності 20, через 2 с після старту вбиваємо `ticket-booking-api-2` (`docker kill`). Скрипт: `scripts/failover-experiment.sh <мітка>`.

| Конфігурація | Запитів | Помилок для клієнта (Failed / Non-2xx) | Записів про недоступний upstream у логах Nginx |
|---|---|---|---|
| **З `proxy_next_upstream`** (наш конфіг) | 8000 | **0 / 0** | 161 |
| **Контроль** `proxy_next_upstream off` (`round_robin_no_failover.conf`) | 8000 | **24 / 24** (502) | 243 |

**Висновок.** Без повторів (контроль) за секунду падіння клієнти отримують 502 (0.3% запитів). З повторами втрат немає: Nginx фіксує помилки на вбитому інстансі (ті самі записи в логах) і переадресовує запит на живий. Важливо: **за замовчуванням** Nginx уже має `proxy_next_upstream error timeout`, тож відмова з'єднання повторюється і без наших директив; явний рядок у конфігу додає повтори для 5xx-відповідей (`http_500/502/503`), які самі по собі за замовчуванням не повторюються.

Відновлення: Docker не перезапускає вручну зупинений (`docker kill`) контейнер, тому повертаємо репліку `docker compose up -d --scale api=2`, а потім `docker compose restart nginx`, щоб Nginx побачив нову адресу.

### 8.4. Масштабування: 1, 2, 3+ інстанси (пункт 2.5)

Серія: Round Robin, пул Npgsql = 20, по 3 прогони на кожну кількість інстансів (2000 запитів, паралельність 50, усі в межах одного часу).

| Інстанси API | RPS (прогони) | Середнє RPS | p99, мс (прогони) | Помилки |
|---|---|---|---|---|
| 1 | 2346 / 2169 / 2600 | **2372** | 55 / 48 / 35 | 0 |
| 2 | 1223 / 1729 / 1882 | **1611** | 302 / 92 / 65 | 0 |
| 3 | 1198 / 1688 / 1584 | **1490** | 184 / 100 / 105 | 0 |

Завантаження CPU при 3 інстансах (`docker stats`, під навантаженням, 1645 RPS): `api-1..3` разом ~366%, PostgreSQL ~153%, Nginx ~42%, Redis <1%.

**Висновок 1: горизонтальне масштабування на одній машині не додає пропускної здатності.** З 1 до 3 інстансів RPS падає з ~2370 до ~1490, а p99 росте. Причина: усі інстанси ділять один CPU-ресурс (Docker VM), а кожен новий вузол додає накладні витрати (додатковий hop через Nginx, ще один пул з'єднань, ще одна ASP.NET-процес з GC і JIT). Масштабування має сенс, коли компонент, що обмежує, — сам обчислювальний шар, а на цьому стенді обмежує спільний ресурс.

**Висновок 2: після масштабування обчислень вузьке місце переходить до бази.** Перевірено на практиці:

1. **Пул з'єднань БД (підтверджено).** За замовчуванням Npgsql тримає до 100 з'єднань на інстанс, а PostgreSQL за замовчуванням приймає 100 клієнтів загалом. Три інстанси під навантаженням вичерпують ліміт: `FATAL: sorry, too many clients already` (зафіксовано в логах API, а на рівні `psql` клієнт не міг підключитися). Виправлено обмеженням `Maximum Pool Size=20` у рядку підключення: 3×20 = 60 з'єднань, помилок немає. Побічний ефект: пул 20 на одному інстансі обмежує паралельність і без збільшення `max_connections` скоро стане новою межею.
2. **CPU спільної VM (виміряно).** PostgreSQL сам споживає ~153% CPU, а API разом ~366%. Додавання інстансів збільшує конкуренцію за той самий CPU, а не потужність.
3. **Locks на рядках квитків (за дизайном, не відтворено під масштабуванням).** Конфлікти на одному квитку вирішуються OCC (`Version`) і `UPDATE ... WHERE Version = @v`: при 20 паралельних бронюваннях одного квитка рівно один отримує 201, решта 409 (інтеграційний тест). Під високою конкуренцією за **різні** квитки цей механізм не дає блокувань, але кожен невдалий OCC-запит — це повтор від клієнта, тобто додаткове навантаження на БД.
4. **Network I/O та Nginx (очікування, не виміряно окремо).** Кожен запит проходить додатковий hop клієнт → Nginx → API → БД. Nginx при 3 інстансах споживає ~42% CPU; окремого стенду для вимірювання мережевого I/O у цій роботі немає. За власним оцінюванням, при багатьох інстансах на одному хості Nginx і мережа стають наступним обмеженням після БД.

**Шляхи подальшої оптимізації.** Пул з'єднань за інстансом або PgBouncer (поширює обмеження `max_connections`), реплікація читань для `GET /tickets` (вони не потребують узгодженості з записом), окремі хости для API, БД і Nginx (щоб CPU не був спільним).

### 8.5. Як відтворити

```bash
docker compose up -d --build --scale api=2              # базовий стенд
scripts/switch-balancer.sh round_robin                  # або least_conn | ip_hash
scripts/bench.sh "мітка" 2000 50                        # замір RPS, p99, розподіл по інстансах
scripts/failover-experiment.sh "мітка" ticket-booking-api-2   # вбиття інстансу під навантаженням

docker compose --profile experiment up -d --scale api=2 # + api-slow з затримкою 300 мс
scripts/switch-balancer.sh round_robin_asymmetric
```

---

## 9. Лабораторна робота №4: розподілене кешування (Redis)

### 9.1. Які ендпоінти кешуємо і чому (Read-Intensive)

Продаж квитків — типовий read-heavy профіль: на кожну спробу бронювання припадають десятки переглядів афіші й карти залу (користувачі оновлюють сторінку, чекаючи на вільні місця). Аналіз контролерів:

| Ендпоінт | Частота читання | Частота зміни | Вартість для БД | Рішення |
|---|---|---|---|---|
| `GET /api/v1/events[?city=]` — афіша | дуже висока (головна сторінка) | майже ніколи (через API подій не змінюють) | `JOIN Events + Venues` | **кешуємо**, TTL 5 хв |
| `GET /api/v1/events/{id}/tickets[?status=]` — карта залу | найвища (polling вибору місць) | при кожному бронюванні / оплаті / скасуванні | `JOIN Tickets + Sections + SeatSlots`; для залу на 1000 місць — 1000 рядків і ~149 КБ JSON | **кешуємо**, TTL 30 с + явна інвалідація |
| `GET /api/v1/bookings/{id}` | низька (власник броні) | часто (оплата, протермінування) | 1 рядок за PK | не кешуємо: дешево і має бути строго актуальним |
| `GET /api/v1/auth/me` | низька | — | 1 рядок за PK | не кешуємо |
| `POST/…` бронювання, оплата, скасування | — | — | транзакції з OCC | ніколи не кешуємо; **перевірка доступності при бронюванні завжди йде в PostgreSQL**, тож застарілий кеш не може призвести до подвійного продажу |

### 9.2. Розгортання Redis

- Окремий контейнер `ticket-booking-redis` (`redis:7-alpine`) у `docker-compose.yml`, спільний для всіх інстансів API (кеш, записаний одним вузлом, читає інший — перевірено: `MISS` на `api-1` → `HIT` на `api-2`).
- `--maxmemory 128mb --maxmemory-policy allkeys-lru` — при заповненні витісняються найдавніше використані ключі, кеш ніколи не відмовляє в записі.
- `--save "" --appendonly no` — кеш без persistence: після рестарту Redis порожній, тому не може повернути дані, що застаріли, поки він лежав.
- Клієнт: `IDistributedCache` (`Microsoft.Extensions.Caching.StackExchangeRedis`), рядок підключення `ConnectionStrings:RedisConnection`.

### 9.3. Патерн Cache-Aside

```mermaid
flowchart LR
    C[Клієнт] --> N[Nginx]
    N --> A1[API 1]
    N --> A2[API 2]
    subgraph Read ["Читання (GET)"]
      direction TB
      R1{"ключ у Redis?"} -- "так: HIT" --> R2["віддати значення<br/>(gzip як є)"]
      R1 -- "ні: MISS" --> R3["SELECT з PostgreSQL"] --> R4["SET ключ, TTL ± 10%"] --> R5[віддати]
      R1 -- "Redis недоступний: BYPASS" --> R6["SELECT з PostgreSQL,<br/>без запису в кеш"]
    end
    subgraph Write ["Зміна (POST)"]
      direction TB
      W1["транзакція в PostgreSQL<br/>(OCC)"] --> W2["COMMIT"] --> W3["DEL ключів події"] --> W4["через 1 с: DEL ще раз<br/>(double delete)"]
    end
    A1 & A2 --> Read & Write
    Read & Write <--> RD[("Redis 7<br/>спільний кеш")]
    Read & Write <--> PG[("PostgreSQL 16")]
```

Реалізація — `Infrastructure/Caching/ResilientCache.cs` (`GetOrCreateAsync` / `InvalidateAsync`), використання — `EventsController`. Кожна відповідь має заголовок **`X-Cache: HIT | MISS | BYPASS`**, а в лог пишеться `Cache HIT/MISS/BYPASS/INVALIDATE <ключ>`.

Значення зберігаються **стиснутими (gzip)**, якщо вони більші за 1 КБ (`CachedPayload.cs`): карта залу на 1000 місць займає ~5 КБ замість ~149 КБ. Клієнтам з `Accept-Encoding: gzip` (браузери, k6) стиснуте значення віддається без розпакування (`Content-Encoding: gzip`, `Vary: Accept-Encoding`), іншим — розпакований JSON. Навіщо це знадобилось, описано в [розділі 10.6](#106-знахідка-redis-не-витримував-великих-значень-і-як-це-виправлено).

### 9.4. Специфікація ключів

Шаблон: **`tb:v1:{domain}:{entity}:{id}:{params}`** (`Infrastructure/Caching/CacheKeys.cs`):

- `tb` — простір імен застосунку (Redis можна ділити з іншими сервісами); `v1` — версія формату значення: зміна DTO → `v2`, старі ключі просто протухнуть за TTL, без ручного очищення;
- `{id}` — ідентифікатор сутності (`Guid` у форматі `N`) або `-` для списків;
- `{params}` — канонічні параметри запиту: `all` без фільтрів; `k=v` у нижньому регістрі для коротких ASCII-значень; інакше `h-` + перші 16 hex SHA-256 від канонічного рядка (кирилиця, довгі значення, `:`). Однаковий запит завжди дає той самий ключ (`?city=Чернівці` і `?city= чернівці ` → один ключ), різні — різні.

| Ендпоінт | Приклад ключа | TTL | Інвалідація |
|---|---|---|---|
| `GET /events` | `tb:v1:catalog:events:-:all` | 300 с ± 10% | лише expiration (змін через API немає) |
| `GET /events?city=Чернівці` | `tb:v1:catalog:events:-:h-de9b7f8d0898eeca` | 300 с ± 10% | лише expiration |
| `GET /events/{id}/tickets` | `tb:v1:catalog:event-tickets:e5b871923c2b4d43982182d2cbb54d01:all` | 30 с ± 10% | явна + expiration |
| `GET /events/{id}/tickets?status=Available` | `tb:v1:catalog:event-tickets:e5b871923c2b4d43982182d2cbb54d01:status=available` | 30 с ± 10% | явна + expiration |

### 9.5. Політика TTL та інвалідації

- **Каталог (300 с).** Афіша змінюється рідко й не через цей API, тож допустима затримка — хвилини. TTL обмежує застарілість, якщо подію змінять напряму в БД.
- **Доступність квитків (30 с).** Основний механізм свіжості — **явна інвалідація**, TTL лише страховка (якщо `DEL` не дійшов через збій Redis, застарілі дані живуть не довше 30 с).
- **Jitter ±10%** до кожного TTL — ключі, записані одночасно (після рестарту або flush), протухають у різний час → немає Cache Avalanche.
- **Явна інвалідація після коміту** (ключі всіх варіантів фільтра `status` для зачеплених подій — `CacheKeys.AllEventTickets`):

  | Подія | Де | Причина в лозі |
  |---|---|---|
  | `POST /bookings` (квитки → `Reserved`) | `BookingsController.Create` | `booking created` |
  | `POST /bookings/{id}/confirm` (→ `Sold`) | `BookingsController.Confirm` | `booking confirmed` |
  | оплата простроченої броні (→ `Available`) | `BookingsController.Confirm` | `booking expired on confirm` |
  | `POST /bookings/{id}/cancel` (→ `Available`) | `BookingsController.Cancel` | `booking cancelled` |
  | фонове протермінування броней | `ExpiredBookingsCleaner` | `bookings expired` |

- **Чому видаляємо ключ, а не оновлюємо його.** Перерахунок карти залу при кожній бронi дорожчий, ніж наступний MISS, і при паралельних записах оновлення можуть прийти в іншому порядку (lost update у кеші).
- **Double delete.** Паралельний читач міг прочитати БД **до** коміту і записати старе значення **після** нашого `DEL`. Тому ключі видаляються ще раз через 1 с (`Cache:InvalidationRetryDelayMs`).
- Перевірка: `POST /bookings` → наступний `GET /tickets` дає `X-Cache: MISS` і квиток уже `Reserved` (інтеграційний тест `Tickets_BookingInvalidatesCache_NextReadIsFreshMiss`).

### 9.6. Відмовостійкість: Redis недоступний

`ResilientCache` ловить будь-яку помилку Redis і обслуговує запит з PostgreSQL (`X-Cache: BYPASS`), тобто кеш — оптимізація, а не точка відмови:

- **Обрив з'єднання** (`RedisConnectionException`) відкриває circuit breaker на 15 с: наступні запити інстансу одразу йдуть у БД, не чекаючи на таймаут. Через 15 с — нова спроба.
- **Таймаут команди** (`RedisTimeoutException`, 2 с) обслуговує з БД лише цей запит і **не** вимикає кеш: під навантаженням таймаут означав перевантажений інстанс API, а не мертвий Redis (див. [10.6](#106-знахідка-redis-не-витримував-великих-значень-і-як-це-виправлено)).
- Інвалідація при недоступному Redis пропускається з попередженням; оскільки Redis без persistence, після підйому він порожній і застарілих значень не має.

Перевірка (`docker stop ticket-booking-redis` під час роботи):

```
200 0.21s X-Cache: HIT       ← Redis працює
200 2.83s X-Cache: BYPASS    ← Redis зупинено: перший запит інстансу чекає таймаут підключення, далі circuit breaker
200 0.01s X-Cache: BYPASS
200 0.005s X-Cache: BYPASS
                             ← docker start ticket-booking-redis, через 15 с:
200 0.06s X-Cache: MISS      ← кеш порожній (без persistence), значення береться з БД
200 0.006s X-Cache: HIT
```

Бронювання при зупиненому Redis також проходить (`201`) — інтеграційний тест `RedisUnavailable_FallsBackToDatabase`.

### 9.7. Cold vs Hot: вплив кешу на латентність

`scripts/cache-latency.sh` — для кожної з 30 пар: `DEL` ключа → запит (**MISS**: БД + запис у кеш) → такий самий запит (**HIT**). Запити йдуть через Nginx, клієнт надсилає `Accept-Encoding: gzip`. Сирий вивід: `experiments/raw/lab4_cache_latency.txt`.

| Запит | Розмір відповіді | MISS p50 / p95, мс | HIT p50 / p95, мс | Прискорення p50 |
|---|---|---|---|---|
| Схема залу, 1000 квитків | ~149 КБ JSON (~5 КБ gzip) | 9.14 / 29.27 | **3.38 / 12.16** | **×2.7** |
| Квитки демо-події, 32 квитки | ~6 КБ | 5.01 / 8.01 | **2.75 / 4.00** | ×1.8 |
| Каталог подій | ~2 КБ | 3.94 / 5.52 | **2.32 / 3.10** | ×1.7 |

Що більший і дорожчий запит до БД, то більший виграш: для схеми залу MISS — це `JOIN` 1000 рядків, серіалізація ~149 КБ і gzip, а HIT — один `HMGET` у Redis і віддача готових ~5 КБ. Абсолютні значення невеликі, бо БД на тому ж хості й дані в `shared_buffers`; під навантаженням різниця значно більша (розділ 10.5: p99 17 мс з кешем проти 1.6 с без кешу при 100 VU).

Вплив кешу на навантаження БД і пропускну здатність під навантаженням виміряно в [розділі 10.5](#105-кеш-увімкнено-vs-вимкнено).

### 9.8. Інтеграційні тести кешу

`tests/TicketBooking.IntegrationTests/EventsTests.cs`, `CacheKeysTests.cs`, `CachedPayloadTests.cs` (Testcontainers піднімає ще й `redis:7-alpine`, між тестами — `FLUSHALL`): MISS → HIT; окремі ключі для фільтрів; 404 не кешується; інвалідація після бронювання, оплати, скасування; gzip-віддача з кешу; fallback при недоступному Redis; вимкнений кеш (`Cache:Enabled=false` → `BYPASS`). Усього **40/40** тестів.

### 9.9. Теорія до захисту

- **Cache Stampede / Thundering Herd.** Популярний ключ протухає, і сотні паралельних запитів одночасно отримують MISS та йдуть у БД з тим самим важким запитом. Захист: (1) блокування на перерахунок (лише один запит рахує значення, інші чекають або отримують старе; у Redis — `SET lock NX PX`); (2) ймовірнісне раннє оновлення (XFetch: значення перераховується трохи раніше TTL з імовірністю, що зростає ближче до кінця); (3) stale-while-revalidate. У нашому проєкті ризик обмежено: один MISS на ключ коштує ~10 мс, а в навантажувальному тесті кількість MISS — десятки на ~50 000 запитів (розділ 10). Для продакшну з flash crowd варто додати lock на перерахунок карти залу.
- **Cache Avalanche.** Масове одночасне протухання багатьох ключів (усі записані в один момент з однаковим TTL) або падіння всього кешу → вся хвиля запитів падає на БД. Захист: **jitter TTL** (у нас ±10%), прогрів, кластер/репліки Redis, circuit breaker і обмеження паралельності до БД. Під навантаженням ми бачили схожий ефект: коли кеш вимикався через таймаути, весь трафік інстансу йшов у БД і перевантаження ставало самопідтримним (розділ 10.6).
- **LRU vs LFU.** LRU (Least Recently Used) витісняє ключ, до якого найдовше не зверталися; LFU (Least Frequently Used) — ключ з найменшою частотою звернень (у Redis — ймовірнісний лічильник із затуханням). LRU краще реагує на зміну «модних» даних (нова подія відразу стає гарячою), LFU стійкіше до одноразових сканувань, які інакше вимили б гарячі ключі. Ми обрали **`allkeys-lru`**: інтерес до подій зміщується в часі (стартує продаж нової події), а ключів-«сканів» у нас немає. `allkeys-*`, а не `volatile-*`, бо всі ключі мають TTL і є кешем.
- **Expiration vs Invalidation.** Expiration (TTL) — пасивний механізм: дані можуть бути застарілими до TTL, зате нічого не треба відстежувати; підходить для даних, що змінюються поза нашим контролем або рідко (каталог). Invalidation — активне видалення при зміні: свіжість одразу після коміту, але кожен шлях запису має пам'ятати про кеш (у нас 5 точок) і є гонки (лікуємо double delete). Для доступності квитків використовуємо обидва: інвалідацію як основний механізм, TTL як страховку від пропущеного `DEL`.

---

## 10. Лабораторна робота №5: базовий рівень продуктивності (Load Performance Baseline)

Усі скрипти — у `load-tests/`, сирі результати кожного прогону — у `load-tests/results/` (`<мітка>.txt` — вивід k6, `.json` — метрики по ступенях, `.env.txt` — середовище, навантаження на БД, Redis і CPU throttling, `.stats.csv` — `docker stats` кожні 5 с). Зведення всіх прогонів: `load-tests/results/REPORT.md` (генерує `load-tests/report.py`).

### 10.1. Сценарії

| Сценарій | Скрипт | Що робить | Яку підсистему навантажує |
|---|---|---|---|
| **A — Read-Intensive** | `a-read.js` | 70% схема залу на 1000 місць (`GET /events/{id}/tickets?status=Available`), 20% афіша, 10% квитки демо-події; пауза 0.2 с | кеш Redis, серіалізація/мережа API; PostgreSQL лише при MISS |
| **B — Write-Intensive** | `b-write.js` | `POST /bookings` на унікальний квиток із пулу 400 000 (детермінований UUID за номером ітерації, тож 409 немає); пауза 0.2 с | транзакції PostgreSQL (SELECT → UPDATE з OCC → INSERT → COMMIT), WAL, пул з'єднань, інвалідація кешу |
| **C — Complex Workflow** | `c-complex.js` | наскрізний шлях покупця: логін (раз на VU) → афіша → схема залу → бронювання → оплата → перегляд броні; пауза 0.3 с між кроками | усе разом: кеш + 2 транзакції запису + читання за PK + JWT |

Тестові дані: `load-tests/seed.sql` (ідемпотентний) додає подію READ (1000 квитків, `c0ffee00-…-0000000000a1`) і подію WRITE (400 000 квитків, `…-0000000000b1`). Перед кожним прогоном `run.sh` скидає квитки цих подій (`reset.sql`), робить `FLUSHALL` у Redis і обнуляє його статистику — кожен прогін стартує з однакового стану з **холодним кешем**.

### 10.2. Середовище (Baseline Environment)

| Параметр | Значення |
|---|---|
| Хост | Apple M1 (8 ядер: 4 продуктивні + 4 енергоефективні), 8 ГБ RAM, macOS 26.6 |
| Віртуалізація | Docker Desktop (Engine 29.8.1), Linux VM: 8 vCPU, ~3.8 ГБ |
| API | ASP.NET Core .NET 9, **2 інстанси** (для масштабування — 1 і 3), **1 CPU / 512 МБ на інстанс**, логування `Warning`, `ThreadPool.MinThreads = 32` |
| PostgreSQL 16 | 2 CPU / 1 ГБ, Npgsql `Maximum Pool Size = 20` на інстанс |
| Redis 7 | 0.5 CPU / 256 МБ, `maxmemory 128mb`, `allkeys-lru`, без persistence; на старті прогону порожній, наповнюється на warm-up |
| Nginx | 1 CPU / 128 МБ, Round Robin, HTTP/1.1 до upstream |
| Генератор навантаження | k6 v2.4.0 **на хості, поза Docker VM**: ліміти CPU контейнерів (`load-tests/docker-compose.loadtest.yml`) гарантують, що SUT не забирає ядра в генератора і навпаки |

> **Обмеження ізоляції.** Генератор і SUT все ж на одній фізичній машині: k6 ділить з VM ядра й пам'ять (усього 8 ГБ), а планувальник macOS може переносити потоки VM на енергоефективні ядра. Через це між однаковими прогонами буває розкид (приклад нижче — два прогони A). Ліміти CPU роблять порівняння конфігурацій чесними, але абсолютні числа справедливі лише для цього стенду.

### 10.3. Профіль навантаження і методика

- **Warm-up** 20 с при 10 VU (JIT, пули з'єднань, наповнення кешу) — у метрики не входить.
- **Ступені** 10 → 25 → 50 → 100 → 200 VU: перехід 5 с + **утримання 30 с**. Кожен запит має тег `stage` (`warmup` / `ramp` / `vus_010` …), у таблиці потрапляють лише вікна утримання (`load-tests/lib/profile.js`).
- **Два режими:** «стандартний» — з паузою (think time) 0.2–0.3 с, як у живого користувача; **stress** — `THINK_SECONDS=0`, кожен VU шле запити без пауз, щоб знайти межу пропускної здатності.
- Модель навантаження k6 `ramping-vus` — **закрита** (VU чекає на відповідь перед наступним запитом); наслідки для percentiles — у 10.10.
- Навантаження на БД — дельта `pg_stat_database` (транзакції, прочитані рядки) за прогін; CPU — `docker stats` і `cpu.stat` cgroup (частка CFS-періодів, у яких контейнер вперся в ліміт).

### 10.4. Результати по сценаріях (2 інстанси, кеш увімкнено)

**Сценарій A — Read-Intensive** (стандартний режим). інстансів API: **2**; кеш: **true**; транзакцій БД: **386** (8 на 1000 запитів); рядків прочитано: 86 724; Redis hit ratio: 99.91% (53883 / 50); API CFS throttled: 16% періодів

| VUs | Запитів | RPS | avg, мс | p50, мс | p95, мс | p99, мс | max, мс | Помилки, % | cache_hit |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 10 | 1430 | 47.7 | 10.1 | 8.8 | 19.2 | 47.7 | 59.8 | 0.00 | 99.4% |
| 25 | 3579 | 119.3 | 9.4 | 8.0 | 20.2 | 27.0 | 100.8 | 0.00 | 99.6% |
| 50 | 7350 | 245.0 | 3.9 | 3.6 | 7.7 | 10.8 | 18.9 | 0.00 | 99.9% |
| 100 | 14688 | 489.6 | 4.0 | 3.2 | 8.8 | 17.3 | 98.6 | 0.00 | 100.0% |
| 200 | 19625 | 654.2 | 105.5 | 41.9 | 399.5 | 1 688.1 | 3 833.1 | 0.00 | 99.9% |

**Сценарій B — Write-Intensive** (стандартний режим). інстансів API: **2**; кеш: **true**; транзакцій БД: **312 997** (6808 на 1000 запитів); рядків прочитано: 773 670; Redis hit ratio: 0.00% (0 / 0); API CFS throttled: 33% періодів

| VUs | Запитів | RPS | avg, мс | p50, мс | p95, мс | p99, мс | max, мс | Помилки, % |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 10 | 1235 | 41.2 | 40.8 | 19.8 | 141.0 | 465.6 | 585.1 | 0.00 |
| 25 | 3419 | 114.0 | 16.5 | 14.1 | 32.4 | 59.6 | 244.6 | 0.00 |
| 50 | 6092 | 203.1 | 45.2 | 12.4 | 263.8 | 606.3 | 1 113.9 | 0.00 |
| 100 | 12487 | 416.2 | 39.7 | 15.5 | 127.1 | 518.4 | 1 327.9 | 0.00 |
| 200 | 22739 | 758.0 | 63.5 | 60.8 | 154.3 | 215.3 | 322.9 | 0.00 |

**Сценарій C — Complex Workflow** (стандартний режим; `flows_completed` — завершених покупок за секунду). інстансів API: **2**; кеш: **true**; транзакцій БД: **112 255** (2671 на 1000 запитів); рядків прочитано: 448 617; Redis hit ratio: 99.92% (18644 / 14); API CFS throttled: 21% періодів

| VUs | Запитів | RPS | avg, мс | p50, мс | p95, мс | p99, мс | max, мс | Помилки, % | flows_completed |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 10 | 1180 | 39.3 | 12.3 | 11.3 | 25.9 | 33.7 | 68.5 | 0.00 | 7.87 |
| 25 | 3001 | 100.0 | 10.2 | 7.7 | 26.3 | 35.9 | 79.1 | 0.00 | 20.00 |
| 50 | 6101 | 203.4 | 6.2 | 4.8 | 13.8 | 38.2 | 152.5 | 0.00 | 40.67 |
| 100 | 12181 | 406.0 | 8.2 | 4.2 | 11.9 | 50.6 | 1 277.1 | 0.00 | 81.20 |
| 200 | 19566 | 652.2 | 40.5 | 17.7 | 108.0 | 394.8 | 2 770.6 | 0.00 | 130.43 |

**Stress-режим (без пауз), A і B:**

| VUs | Запитів | RPS | avg, мс | p50, мс | p95, мс | p99, мс | max, мс | Помилки, % | cache_hit |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 10 | 22220 | 740.7 | 13.3 | 7.8 | 50.9 | 65.9 | 153.8 | 0.00 | 100.0% |
| 25 | 19221 | 640.7 | 38.9 | 21.9 | 87.1 | 313.0 | 981.4 | 0.00 | 99.9% |
| 50 | 19950 | 665.0 | 71.7 | 50.5 | 115.9 | 755.3 | 3 592.1 | 0.00 | 99.9% |
| 100 | 20388 | 679.6 | 146.3 | 88.3 | 381.2 | 1 534.6 | 3 824.8 | 0.00 | 99.9% |
| 200 | 16031 | 534.4 | 372.1 | 176.1 | 1 650.8 | 4 475.3 | 6 095.5 | 0.00 | 99.3% |

| VUs | Запитів | RPS | avg, мс | p50, мс | p95, мс | p99, мс | max, мс | Помилки, % |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 10 | 15665 | 522.2 | 18.9 | 10.8 | 58.8 | 68.7 | 173.4 | 0.00 |
| 25 | 19935 | 664.5 | 37.4 | 23.2 | 80.5 | 128.2 | 497.0 | 0.00 |
| 50 | 22806 | 760.2 | 65.6 | 68.8 | 127.8 | 195.4 | 395.0 | 0.00 |
| 100 | 24132 | 804.4 | 124.0 | 112.6 | 253.1 | 290.6 | 395.4 | 0.00 |
| 200 | 19734 | 657.8 | 303.6 | 309.2 | 721.5 | 992.3 | 1 291.7 | 0.00 |

```mermaid
xychart-beta
    title "A, 2 інстанси: RPS за рівнями VU"
    x-axis "VUs" [10, 25, 50, 100, 200]
    y-axis "RPS" 0 --> 815
    line [47.7, 119.3, 245.0, 489.6, 654.2]
    line [740.7, 640.7, 665.0, 679.6, 534.4]
```

<sub>лінія 1 — `стандартний` · лінія 2 — `stress`</sub>

```mermaid
xychart-beta
    title "A, 2 інстанси: p95 / p99, мс (стандартний режим)"
    x-axis "VUs" [10, 25, 50, 100, 200]
    y-axis "мс" 0 --> 1857
    line [19.2, 20.2, 7.7, 8.8, 399.5]
    line [47.7, 27.0, 10.8, 17.3, 1688.1]
```

<sub>лінія 1 — `p95` · лінія 2 — `p99`</sub>

**Повторюваність.** Той самий стандартний прогін A, виконаний двічі поспіль (`A_read_2inst_cache_run1` / `run2`), на 100 VU дав 305 / 490 RPS і p99 4350 / 17 мс: у першому прогоні один інстанс на ~30 с «застряг» на 100% CPU (інший у цей час простоював на ~20%), і через закриту модель навантаження VU накопичились на ньому. Повторний прогін C (`C_complex_2inst_stalled`) теж зловив зависання стенду. Тому в таблицях — прогони без таких епізодів, а ці файли залишено як свідчення шуму стенду.

#### Аналіз хвостової латентності (Tail Latency)

- **До насичення хвіст короткий.** A, 100 VU: p50 3.2 мс, p99 17.3 мс (×5). C, 50 VU: p50 4.8, p99 38 мс.
- **Після насичення p99 відривається від p50 на порядок.** A, 200 VU: p50 42 мс, p99 1688 мс (×40), max 3.8 с; stress A, 200 VU: p50 176 мс, p99 4475 мс.
- **Причини хвоста, виміряні на стенді:**
  1. **CFS throttling.** Ліміт `cpus: 1.0` = 100 мс CPU на кожні 100 мс. Коли інстанс витратив квоту, **усі** його потоки заморожуються до наступного періоду. У stress A інстанси впиралися в ліміт у **85%** періодів (B, 1 інстанс — 99%). Саме тому вже на 10 VU без пауз p50 7.8 мс, а p95 51 мс: частина запитів чекає «розморожування» на десятки мс.
  2. **Черги в закритій моделі.** Після насичення кожен новий VU лише подовжує чергу: за законом Літтла `W = N / X`, у stress A на 200 VU `200 / 534 RPS ≈ 375 мс` — збігається з виміряним avg 372 мс.
  3. **Нерівномірність Round Robin.** Якщо один інстанс тимчасово сповільнився (GC, throttling, E-ядро), він отримує стільки ж нових запитів, скільки й швидкий, і VU накопичуються на ньому (прогін `run1`). Least Connections з ЛР3 цей ефект зменшує.
  4. **Перший ступінь B (10 VU, p99 466 мс, max 585 мс)** — холодний старт записів: ріст пулу з'єднань Npgsql, перші транзакції після `VACUUM`, tiered JIT для шляху бронювання (warm-up у 20 с прогріває його лише частково). На 25 VU p99 уже 60 мс.

### 10.5. Кеш увімкнено vs вимкнено

Однаковий стенд (2 інстанси), сценарій A; кеш вимикається змінною `CACHE_ENABLED=false` (→ `Cache__Enabled`), відповіді тоді мають `X-Cache: BYPASS`. «Увімкнено» — кеш, прогрітий на warm-up (hit ratio ≥ 99.3% на всіх ступенях).

| Рівень (стандартний режим) | Кеш | RPS | p50, мс | p95, мс | p99, мс |
|---|---|---:|---:|---:|---:|
| 50 VU | увімкнено | 245.0 | 3.6 | 7.7 | 10.8 |
| 50 VU | вимкнено | 239.9 | 6.7 | 15.8 | 34.3 |
| 100 VU | увімкнено | 489.6 | 3.2 | 8.8 | 17.3 |
| 100 VU | вимкнено | 324.5 | 13.0 | 684.1 | 1624.8 |
| 200 VU | увімкнено | 654.2 | 41.9 | 399.5 | 1688.1 |
| 200 VU | вимкнено | 247.3 | 283.8 | 2314.3 | 3022.3 |

| Навантаження на БД за прогін | Кеш увімкнено | Кеш вимкнено | Зменшення |
|---|---:|---:|---:|
| Транзакцій PostgreSQL, стандартний режим | 386 | 187,212 | **×485** |
| Прочитано рядків, стандартний режим | 86,724 | 48,923,675 | **×564** |
| Транзакцій PostgreSQL, stress | 1,532 | 371,738 | **×243** |
| Прочитано рядків, stress | 447,721 | 97,336,218 | **×217** |
| CPU PostgreSQL (p75 за `docker stats`), stress | 1% | 76% | — |
| Максимальна пропускна здатність (stress) | **741 RPS** | 428 RPS | +73% |

```mermaid
xychart-beta
    title "A, 2 інстанси: RPS з кешем і без (стандартний режим)"
    x-axis "VUs" [10, 25, 50, 100, 200]
    y-axis "RPS" 0 --> 720
    line [47.7, 119.3, 245.0, 489.6, 654.2]
    line [46.8, 119.7, 239.9, 324.5, 247.3]
```

<sub>лінія 1 — `кеш увімкнено` · лінія 2 — `кеш вимкнено`</sub>

```mermaid
xychart-beta
    title "A, 2 інстанси: p99, мс, з кешем і без (стандартний режим)"
    x-axis "VUs" [10, 25, 50, 100, 200]
    y-axis "мс" 0 --> 3325
    line [47.7, 27.0, 10.8, 17.3, 1688.1]
    line [36.0, 32.8, 34.3, 1624.8, 3022.3]
```

<sub>лінія 1 — `кеш увімкнено` · лінія 2 — `кеш вимкнено`</sub>

**Висновки.** До 50 VU обидва варіанти встигають (БД швидка, дані в `shared_buffers`), різниця лише в p99 (11 проти 34 мс). На 100 VU без кешу система вже насичена: RPS 325 замість 490, p99 1.6 с замість 17 мс. Кеш прибирає з PostgreSQL ~99.5% роботи (×217 менше прочитаних рядків), звільняючи БД для транзакцій бронювання — саме тих, які кешувати не можна. Без кешу межею все одно лишається CPU API (серіалізація 1000 рядків на кожен запит), але БД уже завантажена на 76% свого ліміту і була б наступною.

### 10.6. Знахідка: Redis «не витримував» великих значень, і як це виправлено

Перші stress-прогони A показали, що кеш під навантаженням **сам вимикався**, а весь трафік ішов у БД. Розслідування по логах StackExchange.Redis (`RedisTimeoutException` з діагностикою пулу потоків і буферів) дало три послідовні виправлення:

| Крок | Симптом під stress A | Причина (з діагностики) | Виправлення | Результат |
|---|---|---|---|---|
| 1 | 15 756 відповідей `BYPASS`, ~76 000 транзакцій БД | `WORKER: Min=1` — при ліміті 1 CPU .NET стартує з 1 потоком у пулі, і продовження async-команд Redis чекали на потік довше за таймаут 500 мс (thread pool starvation) | `ThreadPool:MinWorkerThreads = 32` (`appsettings.json`, `Program.cs`) | starvation зник (`Min=32`), але таймаути лишились |
| 2 | 2 549 `BYPASS`; 908 → 544 RPS між 50 і 200 VU | `in-pipe: 9 681 524`, `inbound=12 МБ`: одне мультиплексоване з'єднання на інстанс тягнуло карти залу по **149 КБ** кожна, і черга відповідей не встигала розбиратись | значення в Redis **стискаються gzip** (~5 КБ) і віддаються клієнтам із `Accept-Encoding: gzip` без розпакування (`CachedPayload.cs`) | трафік Redis ×30 менше, 7 таймаутів на інстанс замість тисяч |
| 3 | на 200 VU (стандартний A) 268 RPS, p99 6.2 с, 976 `BYPASS` на одному інстансі | **metastable failure**: перевантажений по CPU інстанс не встигав прочитати відповідь Redis → таймаути → circuit breaker вимикав кеш → запити йшли в БД, а це дорожче (1000 рядків + серіалізація + gzip) → інстанс ще більше перевантажений → знову таймаути | таймаут команди **не** відкриває circuit breaker (лише обрив з'єднання); таймаут команд 2 с (`Cache:OperationTimeoutMs`) | 0 таймаутів, DB load у stress A — 1,532 транзакцій замість ~76 000 |

Урок: fallback «якщо кеш повільний — іди в БД» безпечний, лише коли Redis справді недоступний. Якщо ж повільний сам інстанс, такий fallback множить навантаження (це той самий механізм, що й у Cache Avalanche).

### 10.7. Масштабування: 1 vs 2 vs 3 інстанси

Stress-режим (без пауз), той самий стенд, змінюється лише `--scale api=N`. Пропускна здатність — максимальний RPS серед ступенів. **Scalability Efficiency Factor** `SEF(N) = RPS(N) / (N × RPS(1))` (1.0 — ідеальне лінійне масштабування).

| Сценарій | 1 інстанс, RPS | 2 інстанси, RPS | 3 інстанси, RPS | SEF(2) | SEF(3) |
|---|---:|---:|---:|---:|---:|
| A — Read (кеш) | 461 | 741 | 922 | 0.80 | 0.67 |
| B — Write | 412 | 804 | 1010 | 0.98 | 0.82 |

```mermaid
xychart-beta
    title "A: максимальний RPS від кількості інстансів"
    x-axis ["1", "2", "3"]
    y-axis "RPS" 0 --> 1060
    bar [461, 741, 922]
```

```mermaid
xychart-beta
    title "B: максимальний RPS від кількості інстансів"
    x-axis ["1", "2", "3"]
    y-axis "RPS" 0 --> 1161
    bar [412, 804, 1010]
```

| Прогін | CPU API (сумарно, p75) | CPU PostgreSQL (ліміт 200%) | CPU Nginx | CPU Redis |
|---|---:|---:|---:|---:|
| A, 1 інстанс | 100% | 0% | 34% | 3% |
| A, 2 інстанси | 201% | 1% | 46% | 5% |
| A, 3 інстанси | 306% | 4% | 59% | 8% |
| B, 1 інстанс | 100% | 76% | 27% | 3% |
| B, 2 інстанси | 203% | 146% | 18% | 5% |
| B, 3 інстанси | 308% | **201%** | 26% | 8% |

**Висновки.**

- **B (запис) масштабується майже лінійно до 2 інстансів (SEF 0.98)**, а на 3 ефективність падає до 0.82: PostgreSQL упирається у свій ліміт 2 CPU (201%) — **вузьке місце переходить з API на БД**. Подальше додавання інстансів API для запису не допоможе; потрібні оптимізація транзакцій, більше CPU для БД або шардування подій.
- **A (читання з кешу) масштабується гірше (SEF 0.80 / 0.67)**, хоча БД і Redis майже не навантажені. Причина: API на кешованих читаннях дуже швидкий, тож усе більша частка часу припадає на спільні ресурси одного хоста — Nginx (34 → 59% CPU), мережевий стек Docker VM і сам k6 на хості (при ~900 RPS з gzip-розпакуванням). Тобто для читань наступне вузьке місце — інфраструктура навколо API, а не БД.
- **Порівняння з ЛР3.** Там додавання інстансів **зменшувало** RPS, бо контейнери не мали лімітів і 1 інстанс уже використовував усі ядра VM. Тут кожен інстанс має фіксований 1 CPU, тож масштабування додає реальну обчислювальну потужність — і ефект видно.

### 10.8. Точка насичення і основне вузьке місце

- **Точка насичення (A, 2 інстанси, кеш): ~650–740 RPS.** У стандартному режимі пропускна здатність росте лінійно з VU до 100 VU (490 RPS = попит), а на 200 VU замість очікуваних ~950 RPS система дає 654 RPS при p99 1.7 с. У stress-режимі ~740 RPS досягається вже на 10 VU, далі RPS не росте (а на 200 VU падає до 534 — ретроградна ділянка через throttling і конкуренцію потоків), латентність росте пропорційно VU (закон Літтла).
- **B, 2 інстанси: ~760–800 RPS** (50–100 VU без пауз), на 200 VU — 658 RPS і p99 ~1 с.
- **C, 2 інстанси:** до 200 VU (652 RPS, 130 покупок/с) помилок немає, p99 395 мс — близько до насичення.
- **Основне вузьке місце — CPU інстансів API** (ліміт 1 CPU кожен): у stress-режимі API використовує 100% ліміту і throttled у 85–99% CFS-періодів, тоді як PostgreSQL (A: 1%) і Redis (≤ 8%) майже вільні. Для запису при 3 інстансах вузьке місце переходить на **PostgreSQL** (2 CPU, 201%).

### 10.9. SYSTEM PERFORMANCE BASELINE

```
┌────────────────────────── SYSTEM PERFORMANCE BASELINE ───────────────────────────┐
│ Стенд        : Apple M1 / Docker Desktop VM 8 vCPU; API 2 × (1 CPU, 512 МБ);     │
│                PostgreSQL 16 (2 CPU, 1 ГБ, pool 20/інстанс); Redis 7 (0.5 CPU,   │
│                128 МБ allkeys-lru, gzip-значення); Nginx Round Robin; k6 2.4.0   │
│ Read  (A)    : 490 RPS при p95 8.8 мс / p99 17 мс (100 VU, think 0.2 с)          │
│                насичення ~650–740 RPS; cache hit ≥ 99.3%                         │
│ Write (B)    : 416 RPS при p95 127 мс / p99 518 мс (100 VU, think 0.2 с)         │
│                насичення ~760–800 RPS; 0% помилок, 0 конфліктів                  │
│ Flow  (C)    : 130 покупок/с (652 RPS) при p99 395 мс (200 VU)                   │
│ Помилки      : 0.00% на всіх ступенях усіх сценаріїв                             │
│ Bottleneck   : CPU API (throttled 85–99% CFS-періодів у stress);                 │
│                для запису при 3 інстансах — CPU PostgreSQL (201% з 200%)         │
│ Масштабування: SEF(2) = 0.80 (read) / 0.98 (write); SEF(3) = 0.67 / 0.82         │
│ Кеш          : ×217 менше рядків з БД; +73% пропускної здатності;                │
│                p99 при 100 VU 17 мс замість 1625 мс                              │
└──────────────────────────────────────────────────────────────────────────────────┘
```

### 10.10. Теорія до захисту

- **Saturation Point (точка насичення)** — навантаження, після якого збільшення кількості клієнтів не підвищує пропускну здатність, а лише збільшує латентність (черги). На графіку RPS(VU) крива виходить на плато (або падає — ретроградна ділянка, як у stress A на 200 VU), а latency(VU) перетворюється з плоскої на лінійно зростаючу: `W = N / X` (закон Літтла). Насичення визначається найповільнішим ресурсом (тут — CPU API); робоча точка системи має бути помітно нижче (зазвичай 60–70% від точки насичення), щоб мати запас на піки.
- **Coordinated Omission** — систематичне заниження percentiles у **закритій** моделі навантаження (наш `ramping-vus`): коли система «зависає», кожен VU чекає на свою відповідь і **не надсилає** запити, які за планом мали б іти в цей час. Затримка, яку відчули б ці невідправлені запити, не потрапляє в статистику, тож p99 виглядає кращим, ніж для реальних користувачів, які приходять незалежно від стану сервера. У нас це видно на прогоні `run1`: під час зависання інстансу RPS падав (305 замість 490), тобто частина «запланованих» запитів просто не відбулася. Як уникнути: відкрита модель (`constant-arrival-rate` / `ramping-arrival-rate` у k6 — запити йдуть за розкладом, незалежно від відповідей), або корекція в HdrHistogram. Ми свідомо лишили закриту модель для пошуку точки насичення (вона не дає генератору «закидати» систему нескінченною чергою), але для SLO-вимірювань варто використовувати відкриту.
- **Вплив GC/JIT на percentiles.**
  - *JIT*: .NET компілює методи при першому виклику (tiered compilation: спершу швидкий неоптимізований код, потім рекомпіляція гарячих методів). Перші запити повільні — це впливає на max/p99 перших секунд, тому warm-up виключено з метрик. Ефект видно на першому ступені B (p99 466 мс на 10 VU проти 60 мс на 25 VU): шлях запису до warm-up майже не виконувався.
  - *GC*: збирання сміття зупиняє (повністю або частково) потоки застосунку. Gen0-збирання короткі й часті — майже не впливають на p50; Gen2 і особливо Large Object Heap (об'єкти > 85 КБ) — дорогі й рідкісні, тому б'ють саме по p99/max. До gzip кожна HIT-відповідь карти залу створювала рядок у 149 КБ (два об'єкти в LOH на запит), після — масив ~5 КБ. В обмеженому контейнері (1 CPU) GC ще й конкурує з обробкою запитів за ту саму квоту CFS.
  - Обидва ефекти дають **бімодальний розподіл**: p50 стабільний, а p99 «стрибає» між прогонами, тому порівнювати конфігурації треба за кількома прогонами і за p95/p99, а не лише за середнім.

### 10.11. Як відтворити

```bash
# 1. Стенд з лімітами ресурсів (2 інстанси)
docker compose -f docker-compose.yml -f load-tests/docker-compose.loadtest.yml up -d --build --scale api=2
docker compose restart nginx

# 2. Тестові дані для навантаження (один раз, ~15 с)
docker exec -i ticket-booking-db psql -U postgres -d ticket_booking_db < load-tests/seed.sql

# 3. Один прогін: сценарій, мітка, додаткові параметри k6
load-tests/run.sh a-read A_read_2inst_cache                       # стандартний режим
load-tests/run.sh b-write B_write_2inst_stress -e THINK_SECONDS=0  # stress

# 4. Уся серія (baseline, stress, масштабування 1/3, кеш вимкнено; ~50 хв)
load-tests/run-all.sh all

# 5. Таблиці
python3 load-tests/report.py                    # → load-tests/results/REPORT.md

# 6. Повернути звичайний стенд без лімітів
docker compose up -d --build --scale api=2 && docker compose restart nginx
```

Потрібен k6 на хості (`brew install k6`). Параметри профілю: `LEVELS`, `HOLD_SECONDS`, `RAMP_SECONDS`, `WARMUP_SECONDS`, `THINK_SECONDS`, `BASE_URL`.

---

## 11. Лабораторна робота №6: реплікація БД та Read/Write Separation

Основна БД тепер складається з двох вузлів PostgreSQL 16: **Primary** (`ticket-booking-db`) приймає всі записи, **Read Replica** (`ticket-booking-db-replica`) — Hot Standby, що асинхронно програє журнал WAL Primary і обслуговує читання. Який вузол обслуговує конкретне читання, вирішує `DbRouter` у шарі доступу до даних — за вимогою бізнес-сценарію до узгодженості.

### 11.1. Топологія реплікації (Primary / Replica)

| Що | Де | Значення |
|---|---|---|
| WAL для реплікації | `docker-compose.yml`, `db.command` | `wal_level=replica`, `max_wal_senders=10`, `wal_keep_size=256MB` (запас WAL для репліки, що відстала або перезапускалась) |
| Користувач реплікації | `db/primary/initdb/10-replication-user.sh` (новий volume), `db/replica/bootstrap.sh` (існуючий volume, ідемпотентно) | `replicator` з атрибутом `REPLICATION`, пароль SCRAM |
| Доступ | `db/primary/pg_hba.conf` (монтується, `-c hba_file=...`) | `host replication replicator all scram-sha-256` — реплікаційне з'єднання дозволене лише цьому користувачу |
| Ініціалізація репліки | `db/replica/bootstrap.sh` | Порожній data-каталог → `pg_basebackup --wal-method=stream` (фізична копія Primary), `standby.signal` + `primary_conninfo` |
| Режим репліки | там само | `hot_standby=on` (читання під час програвання WAL), `hot_standby_feedback=on` (Primary не прибирає рядки, які ще читає репліка → менше скасованих запитів) |
| Тип | — | Асинхронна потокова (`sync_state = async`): Primary не чекає репліку при коміті |
| Порти на хості | — | Primary 5432, Replica 5433 |

Перевірка (`scripts/replication-demo.sh replication`): бронювання, створене через API, одразу видно на репліці; спроба запису на репліку відхиляється самою СУБД; `pg_stat_replication` на Primary показує стрімінг і нульове відставання.

```text
=== 2.1 Реплікація: запис на Primary → дані на Replica ===
POST /bookings → бронювання 2a11602e-8a4a-4ed5-8072-ea97cd54dedf створено на Primary
Primary: Pending
Replica: Pending
Спроба запису на репліку:
  ERROR:  cannot execute DELETE in a read-only transaction
== Primary: pg_stat_replication
 application_name | client_addr |   state   | sync_state | primary_lsn | replay_lsn | lag_bytes |   replay_lag    
------------------+-------------+-----------+------------+-------------+------------+-----------+-----------------
 db_replica       | 172.18.0.4  | streaming | async      | 0/75B00D90  | 0/75B00D90 |         0 | 00:00:00.000937
(1 row)

== Replica
 in_recovery | received_lsn | replayed_lsn | since_last_replayed_xact | apply_delay 
-------------+--------------+--------------+--------------------------+-------------
 t           | 0/75B00D90   | 0/75B00D90   | 00:00:00.124558          | 0
(1 row)
```

### 11.2. Read/Write Separation у коді (`Infrastructure/Replication/DbRouter.cs`)

Маршрутизація програмна (Multi-Pool Manager): у застосунку два пули з'єднань Npgsql — Primary (`ConnectionStrings:DefaultConnection`) і Replica (`ConnectionStrings:ReplicaConnection`, `Timeout=2`).

- **Запис** — завжди Primary. Контролери, що змінюють дані, працюють із `router.Primary` — тим самим `AppDbContext`, що й раніше; транзакції, блокування й OCC не змінилися.
- **Читання** — `router.ReadAsync(consistency, scope, query)`. Контролер описує лише вимогу до узгодженості, а вузол обирає роутер:

```csharp
// Публічний каталог: Eventual → репліка
router.ReadAsync(ReadConsistency.Eventual, null, (db, ct) => db.Events...ToListAsync(ct), ct);
// Статус оплати: Strong → завжди Primary
router.ReadAsync(ReadConsistency.Strong, null, (db, ct) => LoadBookingWithTickets(db, id, ...), ct);
// Історія бронювань: Read-Your-Writes → репліка, якщо вона вже має останній запис цього користувача
router.ReadAsync(ReadConsistency.ReadYourWrites, DbRouter.UserScope(userId), (db, ct) => ..., ct);
```

Обраний вузол і причина повертаються в заголовках `X-Db-Node: primary|replica` і `X-Db-Route` (`eventual`, `strong`, `session-no-writes`, `session-caught-up`, `replica-behind`, `replica-down`, `no-replica`, `single-primary`). `Replication:Enabled=false` (env `REPLICA_ENABLED=false`) вмикає топологію Single Primary без зміни коду — так виконано порівняння в 11.6.

Чому в коді, а не в проксі (Pgpool-II / MaxScale): проксі бачить лише SQL і не відрізнить «каталог» від «мої бронювання одразу після оплати». Рівень узгодженості — властивість бізнес-сценарію, тому рішення ухвалює код, який цей сценарій знає.

### 11.3. Класифікація читань і Replication Lag

| Операція | Ендпоінт | Узгодженість | Вузол | Чому |
|---|---|---|---|---|
| Каталог подій | `GET /api/v1/events` | Eventual | Replica | Публічний список; нова подія може з'явитися на мілісекунди пізніше |
| Схема залу | `GET /api/v1/events/{id}/tickets` | Eventual + межа запису події | Replica (Primary, поки репліка не наздогнала останнє бронювання події) | Показ місць допускає застарілість: сам продаж перевіряється на Primary (OCC). Але після бронювання кеш (ЛР4) інвалідується, і повторне заповнення з репліки, що відстає, поклало б у Redis на 30 с стан до продажу. Тому подія має свою «межу запису» (11.4) |
| Історія бронювань | `GET /api/v1/users/{id}/bookings` | Read-Your-Writes | Replica або Primary | Користувач щойно оплатив і має побачити своє бронювання |
| Профіль | `GET /api/v1/auth/me` | Read-Your-Writes | Replica або Primary | Одразу після реєстрації |
| Статус бронювання / оплати | `GET /api/v1/bookings/{id}` | Strong | Primary | Підтвердження транзакції: застаріле «Pending» після оплати — це помилка, а не затримка |
| Логін | `POST /api/v1/auth/login` | Strong | Primary | Автентифікація: щойно змінений пароль має діяти одразу |
| Бронювання, оплата, скасування, реєстрація | `POST`/`DELETE` | — (запис) | Primary | Мутуючі операції |

**Відставання під навантаженням.** `scripts/replication-lag-under-load.sh`: сценарій B (бронювання без пауз, 10→100 VU) і кожні 0.5 с — `pg_stat_replication` (сирі дані: `experiments/raw/lab6_replication_lag.csv`):

| Метрика | Медіана | p95 | Максимум |
|---|---:|---:|---:|
| Відставання, байт WAL (`pg_current_wal_lsn() − replay_lsn`) | 640 | 26 256 | 49 256 |
| `replay_lag` (запис закомічено → видно на репліці), мс | 0.85 | 8.17 | 36.43 |

На одній машині репліка відстає на одиниці мілісекунд — Stale Read на живому стенді зловити складно, але він реальний: між комітом на Primary і появою рядка на репліці завжди є вікно. У продакшені (окремий сервер, мережа між AZ, важкий запит на репліці, `VACUUM`) це вже сотні мілісекунд — секунди. Для демонстрації лаг відтворено синтетично: `recovery_min_apply_delay` — репліка отримує WAL одразу, але програє його із затримкою (`scripts/replica-lag.sh 3000`).

**Stale Read** (`scripts/replication-demo.sh stale-read`, лаг 3 с): користувач створив бронювання й одразу відкрив історію. Заголовок `X-Read-Consistency: eventual` примусово читає з репліки (лише для демонстрації) — бронювання у відповіді немає.

```text
=== 2.3–2.4 Stale Read і Read-Your-Writes (lag 3 с) ===
recovery_min_apply_delay = 3s
POST /bookings → 85b4ad00-8755-4027-9323-486fe4cdfc21

--- одразу ж читаємо історію з репліки БЕЗ захисту (X-Read-Consistency: eventual):
HTTP/1.1 200 OK
X-Db-Node: replica
X-Db-Route: eventual
  STALE READ: нового бронювання у відповіді НЕМАЄ

--- те саме читання із захистом Read-Your-Writes (поведінка за замовчуванням):
HTTP/1.1 200 OK
X-Db-Node: primary
X-Db-Route: replica-behind
  нове бронювання є у відповіді (репліка відстає → прочитано з Primary)

--- через 4 с репліка наздогнала, той самий запит знову йде на репліку:
HTTP/1.1 200 OK
X-Db-Node: replica
X-Db-Route: session-caught-up
  бронювання є у відповіді
recovery_min_apply_delay = 0
```

### 11.4. Read-After-Write Consistency: перевірка LSN

Реалізовано **Read-Your-Own-Writes Verification** (звірка LSN), а не просте Sticky Routing з TTL:

1. Після коміту запису `DbRouter.RecordWriteAsync` читає з Primary `pg_current_wal_lsn()` — позицію журналу, що вже містить цей коміт, — і зберігає її в Redis як **межу запису** сесії: `tb:v1:replication:write-fence:user:{id}:lsn` (і `event:{id}` для схеми залу) з TTL 30 с.
2. Читання Read-Your-Writes бере межу сесії. Якщо її немає (сесія нічого не писала 30 с) — одразу репліка (`session-no-writes`).
3. Якщо межа є — запит до репліки `pg_last_wal_replay_lsn()`. Репліка вже програла журнал до межі → читаємо з неї (`session-caught-up`), інакше → Primary (`replica-behind`).
4. Redis недоступний, і межу прочитати не вдалося → Primary (`session-unknown`): безпечніше переплатити читанням з Primary, ніж показати застарілі дані.

```mermaid
sequenceDiagram
    participant U as Користувач
    participant API as API (DbRouter)
    participant P as Primary
    participant R as Redis
    participant S as Replica
    U->>API: POST /bookings
    API->>P: INSERT ... COMMIT
    API->>P: SELECT pg_current_wal_lsn()  → 0/75B00D90
    API->>R: SET write-fence:user:{id} = 0/75B00D90 (TTL 30 с)
    U->>API: GET /users/{id}/bookings
    API->>R: GET write-fence:user:{id}
    API->>S: SELECT pg_last_wal_replay_lsn()
    alt репліка ≥ межі
        API->>S: SELECT ... (X-Db-Route: session-caught-up)
    else репліка відстає
        API->>P: SELECT ... (X-Db-Route: replica-behind)
    end
```

Порівняння зі Sticky Routing (усі читання користувача на Primary протягом N секунд після запису): sticky простіший, але гарантує консистентність лише якщо лаг < N, і при цьому відправляє на Primary всі читання навіть тоді, коли репліка давно наздогнала (на нашому стенді — через ~1 мс). LSN-перевірка точна в обидва боки: на Primary йдуть лише ті читання, для яких репліка справді ще не має запису. Ціна — додатковий `GET` у Redis і короткий запит до репліки на кожне Read-Your-Writes читання (у сценарії D це видно як Redis misses у `*.env.txt`).

Межа ставиться **до** інвалідації кешу (`BookingsController`, `ExpiredBookingsCleaner`): інакше паралельний запит міг би між інвалідацією і записом межі заповнити кеш з репліки, що відстає.

### 11.5. Відмовостійкість: втрата Replica і Primary

**Втрата Replica** (`docker stop ticket-booking-db-replica`): перше читання отримує помилку з'єднання і прозоро повторюється на Primary (`X-Db-Route: replica-down`). Репліка виключається з маршрутизації на 10 с (`ReplicaHealth`, circuit breaker) — наступні читання одразу йдуть на Primary без спроби підключення. Через 10 с роутер знову пробує репліку; після `docker start` читання повертаються на неї без рестарту API.

```text
=== 2.5 Втрата репліки: docker stop ticket-booking-db-replica ===
--- каталог (eventual), кеш порожній:
HTTP/1.1 200 OK
X-Db-Node: primary
X-Db-Route: replica-down
X-Cache: MISS
--- повторно (репліка вже виключена з маршрутизації на 10 с):
HTTP/1.1 200 OK
X-Db-Node: primary
X-Db-Route: replica-down
X-Cache: MISS
--- запис працює як завжди:
  бронювання 5ecbd2f6-de42-43a5-8446-a13da0e6d01d створено
Репліку запущено знову; через ~10 с читання повертаються на неї.
HTTP/1.1 200 OK
X-Db-Node: replica
X-Db-Route: eventual
X-Cache: MISS
```

**Втрата Primary** (`docker stop ticket-booking-db`): каталог і схема залу продовжують читатися з репліки. Мутації отримують контрольовану відповідь **503 «Система в режимі лише читання»** з `Retry-After: 10` (`GlobalExceptionHandler`: помилка з'єднання з БД на не-GET запиті). Strong-читання (статус оплати) — 503, бо їх свідомо не обслуговують з репліки. API-контейнери не падають, після `docker start` запис відновлюється сам (пул з'єднань перевіряє «мертві» з'єднання, ЛР2).

```text
=== 2.5 Втрата Primary: docker stop ticket-booking-db ===
--- каталог читається з репліки:
HTTP/1.1 200 OK
X-Db-Node: replica
X-Db-Route: eventual
X-Cache: MISS
--- запис блокується (Read-Only Mode):
HTTP/1.1 503 Service Unavailable
Retry-After: 10
  title: Система в режимі лише читання
  detail: Основний вузол бази даних (Primary) недоступний: операції запису тимчасово заблоковано. Перегляд каталогу працює. Спробуйте пізніше.
--- статус оплати (strong, лише Primary) — контрольована помилка, без падіння API:
  HTTP 503
Primary запущено знову.
Запис знову працює: бронювання 3d61f075-8c98-4c0e-bb12-e806e73df397
```

**Знахідка: Nginx «вбивав» і читання.** Перша спроба сценарію з Primary дала 502 на каталозі: у конфігах ЛР3 `proxy_next_upstream ... http_503`, тож кожен 503 від API рахувався відмовою інстансу (`max_fails=2`). Після кількох записів у режимі лише читання Nginx виключав з ротації **обидва** здорові інстанси (`no live upstreams`), і падали навіть читання з репліки. 503 від нашого API означає недоступність спільної залежності (Primary), а не самого інстансу: повтор на іншому інстансі не допоможе. Тому `http_503` прибрано з `proxy_next_upstream` у всіх конфігах `nginx/` (`error`, `timeout`, `http_500`, `http_502` лишились — для падіння самого інстансу, як у ЛР3).

| Сценарій збою | Читання eventual | Читання strong | Запис | API |
|---|---|---|---|---|
| Replica down | Primary (fallback), 200 | Primary, 200 | 200/201 | Працює |
| Primary down | Replica, 200 | 503 | 503 Read-Only + `Retry-After` | Працює |
| Primary down (до виправлення Nginx) | 502 після кількох 503 | 503 → 502 | 503 → 502 | Обидва інстанси виключено Nginx |

Автоматичного промоуту репліки немає свідомо (див. split-brain у 11.8): у цій топології втрата Primary — це деградація до Read-Only, а не відмова.

### 11.6. Read Scaling: Single Primary vs Primary + Replica

**Методика.** Сценарій **D — Mixed** (`load-tests/d-mixed.js`): 80% читань (схема залу на 1000 квитків і каталог), 20% бронювань. Кеш вимкнено, бо з кешем читання майже не доходять до БД (ЛР5: 7 транзакцій БД на 1000 запитів) і репліці нічого розвантажувати. Кожен вузол БД обмежено **0.5 CPU** (однакові вузли в обох топологіях), API — 4 інстанси по 1 CPU, щоб у Single Primary вузьким місцем була саме БД. Профіль той самий, що в ЛР5 (рівні 10→200 VU по 30 с); `REPLICA_ENABLED=false/true` перемикає лише маршрутизацію. Запуск: `load-tests/run-lab6.sh` (~30 хв). Додатково сценарій A (100% читань).

**Стандартний профіль (пауза 0.2 с), сценарій D:**

| VUs | RPS: Single | RPS: + Replica | p95, мс: Single | p95, мс: + Replica | p99, мс: Single | p99, мс: + Replica |
|---:|---:|---:|---:|---:|---:|---:|
| 10 | 47.1 | **47.3** | 26.3 | **23.1** | 38.4 | **37.0** |
| 25 | 120.2 | **119.2** | 15.1 | **20.1** | 24.6 | **29.0** |
| 50 | 230.9 | **240.4** | 26.0 | **15.1** | 308.3 | **31.5** |
| 100 | 226.4 | **469.9** | 725.0 | **18.0** | 1 209.0 | **201.2** |
| 200 | 270.1 | **536.7** | 1 311.6 | **439.4** | 1 897.7 | **607.3** |

**Stress (без пауз), сценарій D, два прогони:**

| VUs | RPS: Single | RPS: + Replica | p95, мс: Single | p95, мс: + Replica | p99, мс: Single | p99, мс: + Replica |
|---:|---:|---:|---:|---:|---:|---:|
| 10 | 296.6 | **523.8** | 90.7 | **61.9** | 105.2 | **77.5** |
| 25 | 256.4 | **513.7** | 207.7 | **108.9** | 305.7 | **160.1** |
| 50 | 257.7 | **518.4** | 493.6 | **223.7** | 691.6 | **312.6** |
| 100 | 259.8 | **531.8** | 964.9 | **468.9** | 1 301.4 | **603.8** |
| 200 | 291.9 | **528.4** | 1 518.8 | **960.0** | 1 890.0 | **1 196.6** |

<sub>Прогін 2 (`*_stress_run2`). Прогін 1: Single — 308.1 / 255.2 / 256.0 / 282.0 / 279.3 RPS, + Replica — 516.9 / 425.1 / 471.0 / 465.2 / 511.0 RPS.</sub>

```mermaid
xychart-beta
    title "Сценарій D, stress: RPS"
    x-axis "VUs" [10, 25, 50, 100, 200]
    y-axis "RPS" 0 --> 586
    line [296.6, 256.4, 257.7, 259.8, 291.9]
    line [523.8, 513.7, 518.4, 531.8, 528.4]
```

<sub>лінія 1 — `Single Primary` · лінія 2 — `Primary + Replica`</sub>

```mermaid
xychart-beta
    title "Сценарій D, стандартний профіль: p99, мс"
    x-axis "VUs" [10, 25, 50, 100, 200]
    y-axis "p99, мс" 0 --> 2088
    line [38.4, 24.6, 308.3, 1209.0, 1897.7]
    line [37.0, 29.0, 31.5, 201.2, 607.3]
```

<sub>лінія 1 — `Single Primary` · лінія 2 — `Primary + Replica`</sub>

**Ресурси (середнє / максимум CPU контейнера за stress-прогін, ліміт вузла БД — 50%):**

| Топологія | Primary | Replica | API (кожен) | Транзакцій на Primary на 1000 запитів | Рядків прочитано на Primary |
|---|---:|---:|---:|---:|---:|
| Single Primary | 47% / 52% — **упирається в ліміт** | 2% (простоює) | ~36% | 6 268 | 43 680 378 |
| Primary + Replica | 28% / 41% | 47% / 54% — **упирається в ліміт** | ~58% | 2 370 | 5 407 189 |

**Результат.**

| Метрика | Single Primary | Primary + Replica | Зміна |
|---|---:|---:|---:|
| Пропускна здатність, stress (середнє по рівнях, прогін 1 / 2) | 276.1 / 272.5 | 477.8 / 523.2 | ×1.73 / ×1.92 |
| Точка насичення (стандартний профіль) | ~50 VU, ~230 RPS: далі RPS не росте, p99 стрибає до 0.3–1.2 с | ~100–200 VU, ~470–540 RPS: до 100 VU p95 = 18 мс | Удвічі пізніше |
| p95 / p99 на 100 VU (стандартний) | 725.0 / 1 209.0 мс | 18.0 / 201.2 мс | ×40 / ×6 нижче |
| p99 на 200 VU, stress (прогін 2) | 1 890.0 мс | 1 196.6 мс | −37% |
| Помилки | 0% | 0% | — |

Винесення читань на репліку подвоїло ресурс БД для змішаного навантаження: запис і strong-читання лишились на Primary, а 80% читань пішли на окремий вузол. Вузьке місце перемістилося з Primary на репліку (47% із 50%), а Primary має запас для записів — наступний крок масштабування — друга репліка з розподілом eventual-читань між ними.

**Чесне застереження: чисте читання майже не виграє.** Сценарій A (100% читань, stress): Single — 313.4 / 276.7 / 279.2 / 273.8 / 261.4 RPS; + Replica — 332.7 / 315.8 / 310.4 / 308.9 / 302.1 RPS (+12%). Усі читання просто переїхали з Primary (CPU 1%) на репліку з тим самим лімітом — сумарний ресурс для читань не змінився. Read Scaling працює, коли (1) є записи, які репліка звільняє від конкуренції з читаннями, або (2) реплік кілька і читання розподіляються між ними. І третє: з увімкненим кешем (ЛР4) більшість читань не доходить ні до Primary, ні до репліки — репліка допомагає на cache miss, на холодному старті після рестарту Redis і для персональних читань, які не кешуються (історія бронювань).

### 11.7. Тести

`tests/TicketBooking.IntegrationTests/ReplicationRoutingTests.cs` (у тестах немає справжньої потокової реплікації, тому «репліка» — той самий PostgreSQL, а «мертва репліка» — неіснуючий хост): eventual-читання йде на репліку, strong — на Primary; після власного запису історія бронювань перевіряє межу запису; репліка недоступна → Primary fallback, і другий запит уже не пробує репліку (circuit breaker); `Replication:Enabled=false` → Single Primary. `LsnTests.cs` — розбір і порівняння LSN. Усього 53 тести, усі проходять; Newman (папки 01, 02, 03, 05): 37 запитів, 123 перевірки, 0 помилок.

### 11.8. Теорія до захисту

**Синхронна, асинхронна і напівсинхронна реплікація.**

| Режим | Коли Primary відповідає клієнту «закомічено» | Втрата даних при падінні Primary | Затримка запису | У PostgreSQL |
|---|---|---|---|---|
| Асинхронна (наш стенд) | Одразу після запису WAL на свій диск | Можлива: останні транзакції, які репліка ще не отримала | Мінімальна | `synchronous_standby_names = ''` (за замовчуванням) |
| Напівсинхронна | Після того, як **хоча б одна** репліка підтвердила отримання (не обов'язково застосування) WAL | Лише якщо одночасно впадуть Primary і та репліка | + 1 мережевий RTT | `synchronous_commit = remote_write` / `on` + `synchronous_standby_names = 'ANY 1 (...)'` |
| Синхронна | Після того, як репліка **застосувала** WAL (запис видно в читаннях з неї) | Немає (RPO = 0) | + RTT + час застосування; якщо репліка зависла — зависає і запис | `synchronous_commit = remote_apply` |

Ми обрали асинхронну: репліка не впливає на латентність і доступність запису, а її відставання ми явно обробляємо (Read-Your-Writes). Плата — RPO > 0: при втраті Primary можуть зникнути останні мілісекунди транзакцій (у тесті під навантаженням записом — до ~50 КБ WAL, див. 11.3).

**Split-Brain.** Два вузли одночасно вважають себе Primary і приймають записи → дві розбіжні історії даних, які неможливо автоматично злити (наприклад, той самий квиток продано двічі). Виникає, якщо при автоматичному failover старий Primary не помер, а лише втратив мережу з частиною кластера. Захист: кворум (новий лідер обирається більшістю вузлів, меншість не може прийняти записи), fencing / STONITH (старий Primary примусово вимикається або відрізається від сховища), лідерська оренда з TTL (Patroni + etcd). У нашому стенді автоматичного failover немає, тому немає і split-brain: при втраті Primary система свідомо переходить у Read-Only (11.5), а промоут репліки (`pg_ctl promote` / `SELECT pg_promote()`) — ручне рішення оператора.

**CAP-теорема.** При мережевому розділенні (Partition) розподілена система мусить обрати: Consistency (відмовити в операції, якщо не може гарантувати актуальні дані) або Availability (відповісти, ризикуючи застарілими даними). Наш вибір різний для різних операцій:
- запис і strong-читання (оплата, логін) — **CP**: без Primary повертаємо 503, але не приймаємо запис «кудись»;
- каталог і схема залу — **AP**: при втраті Primary читаємо з репліки, навіть якщо вона трохи відстає.

**PACELC.** Розширення CAP: якщо Partition — обираємо A чи C; **Else** (у нормальному режимі) — обираємо Latency чи Consistency. Асинхронна репліка — класичний PA/EL: у нормальному режимі ми віддаємо перевагу низькій латентності (не чекаємо репліку при записі), погоджуючись на eventual consistency читань із неї. Read-Your-Writes — точкове повернення до C там, де це помітно користувачу: ціною одного додаткового запиту до репліки (перевірка LSN) або читання з Primary.

**Вибір нового лідера, Raft.** Автоматичний failover потребує консенсусу щодо того, хто новий Primary. Raft: вузли мають терміни (term); якщо фоловер не отримує heartbeat від лідера протягом випадкового election timeout, він стає кандидатом, збільшує term і просить голоси; лідером стає той, хто отримав **більшість** голосів, причому вузол голосує лише за кандидата, чий журнал не старіший за його власний (тому новий лідер має всі закомічені записи). Більшість з N = 2f + 1 вузлів переживає відмову f вузлів, і два лідери в одному term неможливі (дві більшості перетинаються) — це і є захист від split-brain. Для PostgreSQL це роблять зовнішні менеджери: Patroni (лідерство через etcd/Consul/ZooKeeper, які самі працюють на Raft/ZAB), pg_auto_failover, Stolon. Для такого кластера потрібно щонайменше 3 вузли консенсусу.

**Чому не PgBouncer / Pgpool для розділення R/W.** PgBouncer — лише пул з'єднань, маршрутизувати запити за типом він не вміє. Pgpool-II уміє, але аналізує SQL і не знає бізнес-сенсу: він не відрізнить «каталог» від «мої бронювання одразу після оплати». Тому маршрутизація зроблена в коді (`DbRouter`): рівень узгодженості — властивість бізнес-сценарію, а не SQL-запиту.

### 11.9. Як відтворити

```bash
# Стенд: Primary + Replica (репліка ініціалізується сама через pg_basebackup)
docker compose up -d --build && docker compose restart nginx

# Стан реплікації: pg_stat_replication на Primary, LSN на репліці
scripts/replication-status.sh

# Демонстрації: replication | stale-read | replica-down | primary-down | all
scripts/replication-demo.sh all

# Синтетичний лаг (мс) і вимкнення
scripts/replica-lag.sh 3000
scripts/replica-lag.sh 0

# Лаг під навантаженням записом (потрібен k6)
scripts/replication-lag-under-load.sh

# Порівняння Single Primary vs Primary + Replica (~30 хв)
load-tests/run-lab6.sh
python3 load-tests/report.py lab6_D_mixed_single lab6_D_mixed_replica
```

Якщо після `docker volume rm ticket-booking_pgdata` (новий Primary) репліка не стартує з помилкою `database system identifier differs`, її volume треба теж перестворити: `docker compose rm -sf db-replica && docker volume rm ticket-booking_pgdata-replica && docker compose up -d db-replica`. `docker compose down -v` прибирає обидва volume одразу.

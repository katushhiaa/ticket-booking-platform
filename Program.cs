using System.Reflection;
using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using TicketBooking.Api.Auth;
using TicketBooking.Api.Data;
using TicketBooking.Api.Infrastructure;
using TicketBooking.Api.Models;
using TicketBooking.Api.Services;
using TicketBooking.Api.Validation;

var builder = WebApplication.CreateBuilder(args);

// ---------- Persistence ----------
// Без EnableRetryOnFailure: retrying-стратегія несумісна з ручними транзакціями (BeginTransactionAsync) у BookingsController.
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"))
        .AddInterceptors(new PooledConnectionValidator()));

// ---------- Distributed Cache (Redis for Stateless Architecture - Lab 2) ----------
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = builder.Configuration.GetConnectionString("RedisConnection") ?? "redis:6379";
    options.InstanceName = "TicketBooking_";
});

// ---------- MVC + Input Validation ----------
builder.Services
    .AddControllers(options =>
    {
        // null у полях запиту перевіряє FluentValidation (з українськими повідомленнями), а не implicit [Required]
        options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
        options.Filters.Add<FluentValidationFilter>();
    })
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddValidatorsFromAssemblyContaining<CreateBookingRequestValidator>();

// ---------- Error Handling (RFC 7807) ----------
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Instance ??=
            $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}";
        context.ProblemDetails.Extensions.TryAdd("traceId", context.HttpContext.TraceIdentifier);
    };
});
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// ---------- Authentication (JWT Bearer) ----------
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
if (jwtOptions.SigningKey.Length < 32)
{
    throw new InvalidOperationException("Jwt:SigningKey must be at least 32 characters long (HMAC-SHA256).");
}

builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false; // залишаємо оригінальні імена claims: sub, email, name
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = jwtOptions.GetSecurityKey(),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name"
        };
    });
builder.Services.AddAuthorization();

// ---------- Background jobs ----------
builder.Services.AddHostedService<ExpiredBookingsCleaner>();

// ---------- Health checks ----------
builder.Services.AddHealthChecks();

// ---------- Swagger / OpenAPI ----------
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Ticket Booking API",
        Version = "v1",
        Description = "Платформа бронювання квитків на концерти (захист від Double-Booking, TTL резервів, JWT-авторизація)."
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Вставте accessToken з /api/v1/auth/login (без префікса 'Bearer')."
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });

    var xmlFile = Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
    if (File.Exists(xmlFile))
    {
        options.IncludeXmlComments(xmlFile);
    }
});

var app = builder.Build();

// ---------- Node Identification Middleware (Lab 2 — X-Instance-ID) ----------
// Стоїть на самому початку, щоб додавати заголовок у всі відповіді (навіть 401/409/500)
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Instance-ID"] = Environment.MachineName;
    await next();
});

// Має стояти далі: перехоплює винятки з усього конвеєра і віддає ProblemDetails замість стек-трейсу
app.UseExceptionHandler();
// Порожні 404/405/401 теж віддаються як ProblemDetails
app.UseStatusCodePages();

// Кілька інстансів стартують одночасно (docker compose --scale api=2), тому схему і сідинг
// серіалізуємо advisory lock-ом PostgreSQL: інакше два процеси одночасно створять таблиці/дані.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await using var lockConnection = new NpgsqlConnection(db.Database.GetConnectionString());
    await lockConnection.OpenAsync();
    await new NpgsqlCommand("SELECT pg_advisory_lock(20260001)", lockConnection).ExecuteNonQueryAsync();
    try
    {
        db.Database.EnsureCreated();
        DbSeeder.SeedInitialData(db);
    }
    finally
    {
        await new NpgsqlCommand("SELECT pg_advisory_unlock(20260001)", lockConnection).ExecuteNonQueryAsync();
    }
}

// Swagger доступний завжди (навчальний проєкт, потрібен і в Docker)
app.UseSwagger();
app.UseSwaggerUI();

// У контейнері працюємо лише по HTTP — редирект на HTTPS там не потрібен
if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") != "true")
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

// Потрібно для WebApplicationFactory<Program> в інтеграційних тестах
public partial class Program;
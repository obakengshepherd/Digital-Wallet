using DigitalWallet.Api.Middleware;
using DigitalWallet.Application.Interfaces;
using DigitalWallet.Application.Services;
using DigitalWallet.Infrastructure.Cache;
using DigitalWallet.Infrastructure.Messaging;
using DigitalWallet.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shared.Api.Controllers;
using Shared.Infrastructure.RateLimit;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// ── Controllers & API Docs ────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Digital Wallet API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new()
    {
        Type   = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });
});

// ── Authentication ─────────────────────────────────────────────────────────────
builder.Services.AddAuthentication("Bearer")
    .AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["Auth:Authority"];
        options.Audience  = builder.Configuration["Auth:Audience"];
    });
builder.Services.AddAuthorization();

// ── Redis (singleton — one multiplexer per process) ───────────────────────────
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(
        builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379"));

builder.Services.AddSingleton<WalletCacheService>();
builder.Services.AddSingleton<WalletCacheServiceV2>();

// ── Kafka ─────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<WalletEventPublisher>();
builder.Services.AddSingleton<FraudDecisionConsumer>();
builder.Services.AddHostedService<FraudDecisionConsumerWorker>();

// ── Repositories ──────────────────────────────────────────────────────────────
builder.Services.AddScoped<WalletRepository>();
builder.Services.AddScoped<TransactionRepository>();

// ── Application Services ───────────────────────────────────────────────────────
builder.Services.AddScoped<IWalletService, WalletService>();
builder.Services.AddScoped<ITransferService, TransferService>();
builder.Services.AddScoped<ITransactionService, TransactionService>();
builder.Services.AddSingleton<TrueSlidingWindowChecker>();

// ── Distributed Redis Rate Limiter ─────────────────────────────────────────────
// Register the rate limit rules for this system
builder.Services.AddSingleton<IEnumerable<RateLimitRule>>(
    _ => RateLimitPolicies.WalletPolicies());

// ── Health Checks ──────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddCheck<RedisHealthCheck>("redis",
        failureStatus: HealthStatus.Degraded,    // Redis down = degraded, not unhealthy
        tags: ["cache", "redis"])
    .AddCheck<PostgreSqlHealthCheck>("postgresql",
        failureStatus: HealthStatus.Unhealthy,   // PostgreSQL down = unhealthy
        tags: ["database", "postgresql"])
    .AddCheck<KafkaHealthCheck>("kafka",
        failureStatus: HealthStatus.Degraded,    // Kafka down = degraded
        tags: ["messaging", "kafka"])
    .AddTypeActivatedCheck<PostgreSqlHealthCheck>("postgresql",
        builder.Configuration.GetConnectionString("PostgreSQL")!);

// Register named health check implementations
builder.Services.AddTransient<RedisHealthCheck>();
builder.Services.AddTransient(sp =>
    new PostgreSqlHealthCheck(
        builder.Configuration.GetConnectionString("PostgreSQL")!));
builder.Services.AddTransient(sp =>
    new KafkaHealthCheck(
        builder.Configuration.GetConnectionString("Kafka") ?? "localhost:9092"));

// ── Exception Handling ─────────────────────────────────────────────────────────
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();

// ── Middleware Pipeline ────────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler();

// Request logging — before auth so we log all requests including 401s
app.UseMiddleware<RequestLoggingMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

// Distributed Redis rate limiter — AFTER auth so we can identify users
// This replaces the built-in AddRateLimiter for distributed enforcement
app.UseMiddleware<RedisRateLimitMiddleware>();

// Idempotency middleware — for write endpoints only
app.UseMiddleware<IdempotencyMiddleware>();

app.MapControllers();

// Health check endpoints — no auth required (load balancer needs these)
app.MapHealthEndpoints();

app.Run();

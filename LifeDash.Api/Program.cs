using System.Text;
using LifeDash.Api.Data;
using LifeDash.Api.Endpoints;
using LifeDash.Api.Models;
using LifeDash.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// ---------- database ----------
var conn = builder.Configuration.GetConnectionString("Default")
           ?? throw new InvalidOperationException("ConnectionStrings:Default is missing.");
builder.Services.AddDbContext<LifeDashContext>(o => o.UseSqlServer(conn));

// ---------- auth ----------
var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwt.Key) || jwt.Key.Length < 32)
    throw new InvalidOperationException("Jwt:Key must be at least 32 characters. Set it in appsettings or as an environment variable.");

builder.Services.AddSingleton(jwt);
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<DeadlineEngine>();
builder.Services.AddSingleton<IAuditLogWriter, AuditLogWriter>();
builder.Services.AddHttpContextAccessor();
builder.Services.Configure<ReminderEmailOptions>(builder.Configuration.GetSection("ReminderEmail"));
builder.Services.AddHostedService<ReminderEmailWorker>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            NameClaimType = "name",
            ClockSkew = TimeSpan.FromMinutes(2)
        };
    });
builder.Services.AddAuthorization();

// ---------- cors ----------
var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
              ?? new[] { "http://localhost:5173" };
builder.Services.AddCors(o => o.AddPolicy("spa", p => p
    .WithOrigins(origins)
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.Use(async (ctx, next) =>
{
    var sw = Stopwatch.StartNew();
    await next();
    sw.Stop();

    if (!ctx.Request.Path.StartsWithSegments("/api")) return;

    var audit = ctx.RequestServices.GetRequiredService<IAuditLogWriter>();
    var raw = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ctx.User.FindFirstValue("sub");
    var userId = int.TryParse(raw, out var uid) ? uid : (int?)null;
    audit.Write(new
    {
        ts = DateTimeOffset.UtcNow,
        eventType = "request",
        userId,
        method = ctx.Request.Method,
        path = ctx.Request.Path.Value,
        status = ctx.Response.StatusCode,
        ms = sw.ElapsedMilliseconds,
        ip = ctx.Connection.RemoteIpAddress?.ToString()
    });
});

// ---------- fix the seeded demo password hash once ----------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LifeDashContext>();
    try
    {
        await db.Database.ExecuteSqlRawAsync("""
            IF COL_LENGTH('dbo.Trips', 'StartPlace') IS NULL
                ALTER TABLE dbo.Trips ADD StartPlace NVARCHAR(200) NULL;
            """);

        var seeded = db.Users.FirstOrDefault(u => u.PasswordHash == "SEED");
        if (seeded is not null)
        {
            seeded.PasswordHash = PasswordHasher.Hash("demo1234");
            db.SaveChanges();
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Could not reach the database at startup. Run the SQL scripts first.");
    }
}

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Configuration.GetValue("Swagger:Enabled", true))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("spa");
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Ok(new { name = "Life Dashboard API", version = "1.0.0" }));
app.MapGet("/api/health", async (LifeDashContext db) =>
{
    var ok = await db.Database.CanConnectAsync();
    return ok ? Results.Ok(new { status = "healthy" })
              : Results.Json(new { status = "database unreachable" }, statusCode: 503);
});

app.MapGet("/api/logs", (HttpContext ctx, IAuditLogWriter audit, int? take) =>
{
    var path = audit.GetPath();
    if (!File.Exists(path)) return Results.Ok(Array.Empty<object>());

    var raw = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ctx.User.FindFirstValue("sub");
    if (!int.TryParse(raw, out var userId)) return Results.Unauthorized();

    var max = Math.Clamp(take ?? 200, 1, 1000);
    var lines = File.ReadLines(path)
        .Where(l => !string.IsNullOrWhiteSpace(l))
        .Reverse()
        .Take(5000)
        .Select(line =>
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("userId", out var uid)) return null;
                if (uid.ValueKind != JsonValueKind.Number || uid.GetInt32() != userId) return null;
                return line;
            }
            catch
            {
                return null;
            }
        })
        .Where(x => x is not null)
        .Take(max)
        .Select(x => JsonDocument.Parse(x!).RootElement.Clone())
        .ToArray();

    return Results.Ok(lines);
}).RequireAuthorization().WithTags("logs");

app.MapAuth();
app.MapLifeDash();

app.Run();

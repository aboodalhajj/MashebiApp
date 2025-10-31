using System.Data;
using Dapper;
using Npgsql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();

// دابر: دعم تطابق أسماء snake_case مع الخصائص
Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

// ===== اتصال Neon (DATABASE_URL بصيغة libpq URI) =====
var databaseUrl = builder.Configuration["DATABASE_URL"];
if (string.IsNullOrWhiteSpace(databaseUrl))
    throw new InvalidOperationException("DATABASE_URL is missing");

static string BuildConnStringFromUri(string url, bool strictVerify = true)
{
    var u = new Uri(url);
    var parts = Uri.UnescapeDataString(u.UserInfo).Split(':', 2);
    var csb = new NpgsqlConnectionStringBuilder
    {
        Host = u.Host,
        Port = u.IsDefaultPort ? 5432 : u.Port,
        Database = u.AbsolutePath.TrimStart('/'),
        Username = parts[0],
        Password = parts.Length > 1 ? parts[1] : "",
        SslMode = strictVerify ? SslMode.VerifyFull : SslMode.Require,
        TrustServerCertificate = strictVerify ? false : true,
        ChannelBinding = ChannelBinding.Require,
        Timeout = 15,
        CommandTimeout = 30,
        KeepAlive = 30,
        MaxPoolSize = 50
    };
    return csb.ConnectionString;
}

var connString = BuildConnStringFromUri(databaseUrl, strictVerify: true);
var dataSource = new NpgsqlDataSourceBuilder(connString).Build();
builder.Services.AddSingleton(dataSource);

// ===== CORS (وسّعها مؤقتًا أثناء التطوير) =====
builder.Services.AddCors(opt =>
{
    opt.AddPolicy("AllowDev", p => p
        .AllowAnyOrigin()
        .AllowAnyHeader()
        .AllowAnyMethod());
});

// ===== JWT =====
// ضَع القيم في Railway → Variables: JWT__Key, JWT__Issuer, JWT__Audience
var jwtKey      = builder.Configuration["JWT__Key"]      ?? "change-this-key";
var jwtIssuer   = builder.Configuration["JWT__Issuer"]   ?? "MashebiApi";
var jwtAudience = builder.Configuration["JWT__Audience"] ?? "MashebiApp";
var signingKey  = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme    = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,   ValidIssuer   = jwtIssuer,
            ValidateAudience = true, ValidAudience = jwtAudience,
            ValidateIssuerSigningKey = true, IssuerSigningKey = signingKey,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2)
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseCors("AllowDev");
app.UseAuthentication();
app.UseAuthorization();

// ===== Health =====
app.MapGet("/health", () => Results.Ok("OK"));

// ===== LOGIN (الحساب العام في جدول accounts) =====
app.MapPost("/api/auth/login", async (LoginDto dto, NpgsqlDataSource ds) =>
{
    dto.Username = dto.Username?.Trim() ?? "";
    if (string.IsNullOrWhiteSpace(dto.Username) || string.IsNullOrWhiteSpace(dto.Password))
        return Results.BadRequest(new { message = "أدخل اسم المستخدم وكلمة المرور." });

    await using var conn = await ds.OpenConnectionAsync();

    // التحقق عبر pgcrypto/bcrypt: password_hash = crypt(@p, password_hash)
    var sql = @"
        select id, account_name, username, email, is_active
        from public.accounts
        where lower(username) = lower(@u)
          and is_active = true
          and password_hash = crypt(@p, password_hash)
        limit 1;";

    var acc = await conn.QueryFirstOrDefaultAsync(sql, new { u = dto.Username, p = dto.Password });
    if (acc is null)
        return Results.Unauthorized();

    // إنشاء JWT
    string token = CreateJwtToken(
        issuer: jwtIssuer,
        audience: jwtAudience,
        signingKey: signingKey,
        claims: new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, $"{acc.id}"),
            new Claim(JwtRegisteredClaimNames.UniqueName, (string)acc.username),
            new Claim("account_id", $"{acc.id}"),
            new Claim("account_name", (string)acc.account_name)
        },
        expires: DateTime.UtcNow.AddHours(12));

    return Results.Ok(new
    {
        token,
        accountId   = (int)acc.id,
        username    = (string)acc.username,
        accountName = (string)acc.account_name,
        email       = (string)acc.email
    });
});

// Root check
app.MapGet("/", () => new { ok = true, db = "neon", ts = DateTimeOffset.UtcNow });

app.Run();

// ===== Helpers / DTO =====
static string CreateJwtToken(string issuer, string audience, SymmetricSecurityKey signingKey,
    IEnumerable<Claim> claims, DateTime expires)
{
    var creds = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);
    var jwt = new JwtSecurityToken(issuer, audience, claims, expires: expires, signingCredentials: creds);
    return new JwtSecurityTokenHandler().WriteToken(jwt);
}

public class LoginDto
{
    public string? Username { get; set; }
    public string? Password { get; set; }
}

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
// ===== LOGIN (الحساب العام في جدول accounts) =====
app.MapPost("/api/auth/login", async (LoginDto dto, NpgsqlDataSource ds, ILoggerFactory lf) =>
{
    var log = lf.CreateLogger("Auth");
    dto.Username = dto.Username?.Trim() ?? "";
    var pwd = dto.Password ?? "";

    if (string.IsNullOrWhiteSpace(dto.Username) || string.IsNullOrWhiteSpace(pwd))
        return Results.BadRequest(new { message = "أدخل اسم المستخدم وكلمة المرور." });

    try
    {
        await using var conn = await ds.OpenConnectionAsync();

        // ✅ استخدم أسماء أعمدة مُسمّاة (aliases) للربط القوي
        const string sql = @"
            select 
                id              as ""Id"",
                account_name    as ""AccountName"",
                username        as ""Username"",
                email           as ""Email"",
                is_active       as ""IsActive""
            from public.accounts
            where lower(username) = lower(@u)
              and is_active = true
              and password_hash = crypt(@p, password_hash)
            limit 1;";

        var acc = await conn.QueryFirstOrDefaultAsync<AccountRow>(sql, new { u = dto.Username, p = pwd });

        if (acc is null)
            return Results.Unauthorized(); // 401

        // إنشاء JWT
        string token = CreateJwtToken(
            issuer: jwtIssuer,
            audience: jwtAudience,
            signingKey: signingKey,
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, $"{acc.Id}"),
                new Claim(JwtRegisteredClaimNames.UniqueName, acc.Username),
                new Claim("account_id", $"{acc.Id}"),
                new Claim("account_name", acc.AccountName)
            },
            expires: DateTime.UtcNow.AddHours(12));

        return Results.Ok(new
        {
            token,
            accountId   = acc.Id,
            username    = acc.Username,
            accountName = acc.AccountName,
            email       = acc.Email
        });
    }
    catch (PostgresException pgex)
    {
        // لو Crypt() غير معروف -> غالبًا pgcrypto غير مُفعل: SQLSTATE 42883
        log.LogError(pgex, "Postgres error during login. SqlState={SqlState}", pgex.SqlState);
        var hint = pgex.SqlState == "42883" ? "الوظيفة crypt غير موجودة. فعّل امتداد pgcrypto في نفس قاعدة البيانات." : "خطأ قاعدة بيانات.";
        return Results.Problem(hint, statusCode: 500);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Unhandled error during login");
        return Results.Problem("حدث خطأ غير متوقع أثناء تسجيل الدخول.", statusCode: 500);
    }
});

// ===== DTO قوي النوع لصف accounts =====
public class AccountRow
{
    public int    Id          { get; set; }
    public string AccountName { get; set; } = "";
    public string Username    { get; set; } = "";
    public string Email       { get; set; } = "";
    public bool   IsActive    { get; set; }
}

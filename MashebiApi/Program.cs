using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();

// خذ DATABASE_URL إن وُجد (صيغة URI libpq)
var databaseUrl = builder.Configuration["DATABASE_URL"];

// دالة آمنة تفكك URI لصيغة Npgsql *بدون* إدخال tcp:// في Host
static string BuildConnStringFromUri(string url, bool strictVerify = false)
{
    var u = new Uri(url);
    var userInfo = Uri.UnescapeDataString(u.UserInfo).Split(':', 2);
    var user = userInfo[0];
    var pass = userInfo.Length > 1 ? userInfo[1] : "";

    // ملاحظة: لا نضع tcp:// في Host — فقط الاسم الصِرف
    var csb = new NpgsqlConnectionStringBuilder
    {
        Host = u.Host,
        Port = u.IsDefaultPort ? 5432 : u.Port,
        Database = u.AbsolutePath.TrimStart('/'),
        Username = user,
        Password = pass,

        // ---- التصليح السريع:
        // تشفير إلزامي لكن لا نتحقق من اسم المضيف (لرفع الحظر سريعًا)
        SslMode = strictVerify ? SslMode.VerifyFull : SslMode.Require,
        TrustServerCertificate = strictVerify ? false : true,

        // channel_binding=require من رابطك الأصلي:
        ChannelBinding = ChannelBinding.Require,

        Timeout = 15,
        CommandTimeout = 30,
        KeepAlive = 30,
        MaxPoolSize = 50
    };

    return csb.ConnectionString;
}

// Fallback لو ما فيه DATABASE_URL (اختبار محلي)
var fallbackUri = "postgresql://neondb_owner:npg_eT96qAVPUYlb@ep-super-lab-agh3uq8x-pooler.c-2.eu-central-1.aws.neon.tech/DataMashebiApi?sslmode=require&channel_binding=require";

// استخدم التصليح السريع strictVerify:false
var connString = BuildConnStringFromUri(!string.IsNullOrWhiteSpace(databaseUrl) ? databaseUrl : fallbackUri, strictVerify: false);

builder.Services.AddDbContext<AppDbContext>(opt => opt.UseNpgsql(connString));

var app = builder.Build();

// (اختياري) شغّل الهجرات عند الإقلاع
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

app.MapGet("/", () => new { ok = true, db = "neon", ts = DateTimeOffset.UtcNow });


app.Run();

// ====== النماذج والسياق ======
public class Todo
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public bool IsDone { get; set; }
    public DateTime CreatedAt { get; set; }
}

public record TodoCreate(string? Title);
public record TodoUpdate(string? Title, bool? IsDone);

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Todo> Todos => Set<Todo>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Todo>(e =>
        {
            e.ToTable("todos");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityByDefaultColumn();
            e.Property(x => x.Title).IsRequired().HasMaxLength(200);
            e.HasIndex(x => x.IsDone);
            e.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
        });
    }
}

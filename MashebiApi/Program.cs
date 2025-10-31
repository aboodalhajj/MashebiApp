using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();

// 1) المفضل: خذ الرابط من متغير بيئة DATABASE_URL (صيغة URI) عند النشر
var databaseUrl = builder.Configuration["DATABASE_URL"];

// 2) إن لم يوجد، استخدم الصيغة المباشرة (Npgsql) — جرّب محليًا فقط
var fallbackNpgsql = "Host=ep-super-lab-agh3uq8x-pooler.c-2.eu-central-1.aws.neon.tech;Port=5432;Database=DataMashebiApi;Username=neondb_owner;Password=npg_eT96qAVPUYlb;SSL Mode=VerifyFull;Trust Server Certificate=false;Channel Binding=Require;Timeout=15;Command Timeout=30;Maximum Pool Size=50;Keepalive=30";

// دالة تحويل URI إلى Npgsql Connection String (لو استخدمت DATABASE_URL)
static string ToNpgsqlFromUri(string uri)
{
    var u = new Uri(uri);
    var creds = Uri.UnescapeDataString(u.UserInfo).Split(':', 2);
    var csb = new NpgsqlConnectionStringBuilder
    {
        Host = u.Host,
        Port = u.IsDefaultPort ? 5432 : u.Port,
        Database = u.AbsolutePath.TrimStart('/'),
        Username = creds[0],
        Password = creds.Length > 1 ? creds[1] : "",
        SslMode = SslMode.VerifyFull,
        TrustServerCertificate = false,
        ChannelBinding = ChannelBinding.Require,
        Timeout = 15,
        CommandTimeout = 30,
        MaxPoolSize = 50,
        KeepAlive = 30
    };
    return csb.ConnectionString;
}

var connString = !string.IsNullOrWhiteSpace(databaseUrl)
    ? ToNpgsqlFromUri(databaseUrl)
    : fallbackNpgsql;

builder.Services.AddDbContext<AppDbContext>(opt =>
{
    opt.UseNpgsql(connString, b =>
    {
        // اختياري: تسمية جدول سجل الترقيات ومخطط مخصص
        // b.MigrationsHistoryTable("__efmigrations_history", "app");
    });
});

var app = builder.Build();

// اختياري: نقطة فحص
app.MapGet("/", () => new { ok = true, db = "neon", ts = DateTimeOffset.UtcNow });

// مثال CRUD بسيط (إن رغبت): TODOs
app.MapGet("/todos", async (AppDbContext db) => await db.Todos.OrderByDescending(t => t.Id).Take(200).ToListAsync());
app.MapPost("/todos", async (AppDbContext db, TodoCreate dto) =>
{
    var t = new Todo { Title = dto.Title?.Trim() ?? "", CreatedAt = DateTime.UtcNow };
    db.Add(t);
    await db.SaveChangesAsync();
    return Results.Created($"/todos/{t.Id}", t);
});
app.MapPut("/todos/{id:long}", async (AppDbContext db, long id, TodoUpdate dto) =>
{
    var t = await db.Todos.FindAsync(id);
    if (t is null) return Results.NotFound();
    if (dto.Title is not null) t.Title = dto.Title.Trim();
    if (dto.IsDone.HasValue) t.IsDone = dto.IsDone.Value;
    await db.SaveChangesAsync();
    return Results.NoContent();
});
app.MapDelete("/todos/{id:long}", async (AppDbContext db, long id) =>
{
    var t = await db.Todos.FindAsync(id);
    if (t is null) return Results.NotFound();
    db.Remove(t);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

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

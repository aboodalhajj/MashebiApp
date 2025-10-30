using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();

// يحوّل DATABASE_URL لصيغة Npgsql عند توفره
static string? FromDatabaseUrl(string? url)
{
    if (string.IsNullOrWhiteSpace(url)) return null;
    var u = new Uri(url);
    var up = u.UserInfo.Split(':', 2);
    return new NpgsqlConnectionStringBuilder
    {
        Host = u.Host,
        Port = u.Port > 0 ? u.Port : 5432,
        Database = u.AbsolutePath.Trim('/'),
        Username = up.Length > 0 ? up[0] : "",
        Password = up.Length > 1 ? up[1] : "",
        SslMode = SslMode.Require
    }.ConnectionString;
}

// اجلب الاتصال من البيئة (Neon عبر DATABASE_URL أو CONNECTION_STRING)
// اجلب الاتصال من البيئة (Neon عبر DATABASE_URL أو CONNECTION_STRING)
var cs = builder.Configuration["CONNECTION_STRING"]
         ?? FromDatabaseUrl(builder.Configuration["DATABASE_URL"]);

if (string.IsNullOrWhiteSpace(cs))
{
    Console.WriteLine("WARNING: No DB connection. Using InMemory.");
    builder.Services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase("dev"));
}
else
{
    builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(cs));
}

var app = builder.Build();

// فحوصات
app.MapGet("/ping", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }));
app.MapGet("/health", () => Results.Ok("healthy"));

// CRUD بسيطة (مثالك الحالي)

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    try
    {
        await db.Database.MigrateAsync(); // بدل EnsureCreatedAsync
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("DB migrate failed: " + ex.Message);
    }
}
app.Run();


#region EF داخل نفس الملف
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Todo> Todos => Set<Todo>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Todo>(e =>
        {
            e.ToTable("todos");                  // اسم جدول صغير واضح
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Title).IsRequired().HasMaxLength(200);
            e.HasIndex(x => x.IsDone);
        });
    }
}

public class Todo
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public bool IsDone { get; set; }
    public DateTime CreatedAt { get; set; }
}

public record TodoCreate(string? Title);
#endregion

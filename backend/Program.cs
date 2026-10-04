using Microsoft.EntityFrameworkCore;
using FactoryQuoteApi.Data;
using FactoryQuoteApi.Models;
using FactoryQuoteApi.Services;
// Use pure managed networking for SQL Client to avoid native SNI.dll dependencies
AppContext.SetSwitch("Switch.Microsoft.Data.SqlClient.UseManagedNetworkingOnWindows", true);

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });

// Configure SQL Server DbContext
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(connectionString));

// Register HTTP Client Factory
builder.Services.AddHttpClient();

// Register Application Services
builder.Services.AddScoped<IWordQuoteGeneratorService, WordQuoteGeneratorService>();
builder.Services.AddScoped<IPdfConverterService, PdfConverterService>();
builder.Services.AddScoped<INotificationService, NotificationService>();

// Register Background Queue
builder.Services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>(sp => new BackgroundTaskQueue(100));
builder.Services.AddHostedService<QueuedHostedService>();

// Configure CORS for Vercel Frontend
builder.Services.AddCors(options =>
{
    options.AddPolicy("VercelCorsPolicy", policy =>
    {
        policy.SetIsOriginAllowed(origin =>
        {
            if (string.IsNullOrWhiteSpace(origin)) return false;
            var uri = new Uri(origin);
            return uri.Host.EndsWith("vercel.app", StringComparison.OrdinalIgnoreCase) ||
                   uri.Host.EndsWith("runasp.net", StringComparison.OrdinalIgnoreCase) ||
                   uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                   uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase);
        })
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials();
    });
});

builder.Services.AddOpenApi();

var app = builder.Build();

// Ensure Database is created and Seed initial data
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var logger = services.GetRequiredService<ILogger<Program>>();
    try
    {
        var db = services.GetRequiredService<ApplicationDbContext>();
        logger.LogInformation("Ensuring SQL Server database is created...");
        db.Database.EnsureCreated();

        // Seed Users
        if (!db.Users.Any())
        {
            logger.LogInformation("Seeding initial Users...");
            var adminUser = new User
            {
                FullName = "مدير النظام",
                Email = "admin@factory-eg.com",
                PasswordHash = "admin_hash_123",
                Role = "Admin",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };
            var salesUser = new User
            {
                FullName = "مسؤول المبيعات",
                Email = "sales@factory-eg.com",
                PasswordHash = "sales_hash_123",
                Role = "Sales",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };
            db.Users.AddRange(adminUser, salesUser);
            db.SaveChanges();
        }

        // Seed Products
        if (!db.Products.Any())
        {
            logger.LogInformation("Seeding initial Products...");
            var products = new List<Product>
            {
                new() { ProductName = "طوب الي مصمت", Size = "25*12*6", Capacity = "1000", DefaultPrice = 1450.00m, IsActive = true },
                new() { ProductName = "طوب مصمت عادي", Size = "25*12*6", Capacity = "1000", DefaultPrice = 1300.00m, IsActive = true },
                new() { ProductName = "بلوك خرساني مفرغ", Size = "40*20*20", Capacity = "500", DefaultPrice = 3200.00m, IsActive = true }
            };
            db.Products.AddRange(products);
            db.SaveChanges();
        }

        // Seed Clients
        if (!db.Clients.Any())
        {
            logger.LogInformation("Seeding initial Clients...");
            var salesUser = db.Users.FirstOrDefault(u => u.Role == "Sales");
            var clients = new List<Client>
            {
                new()
                {
                    ClientName = "شركة اتريم للمقاولات والاعمال المتخصصة",
                    ContactPerson = "م / سارة شريف",
                    Phone = "+201012345678",
                    Email = "sara@atreem.com",
                    Address = "القاهرة - التجمع الخامس",
                    CreatedById = salesUser?.Id
                },
                new()
                {
                    ClientName = "شركة النصر للتجارة والمقاولات",
                    ContactPerson = "م / أحمد فؤاد",
                    Phone = "+201198765432",
                    Email = "ahmed@elnasr.com",
                    Address = "الجيزة - مدينة 6 أكتوبر",
                    CreatedById = salesUser?.Id
                }
            };
            db.Clients.AddRange(clients);
            db.SaveChanges();
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "An error occurred while initializing or seeding the database.");
    }
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors("VercelCorsPolicy");
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseCors("VercelCorsPolicy");
app.MapControllers();

app.MapGet("/api/health", () => Results.Ok(new { status = "Factory Quote API Running", timestamp = DateTime.UtcNow }));

app.Run();

using System.Text;
using Fraud.Api.Auth;
using Fraud.Api.Data;
using Fraud.Api.Hubs;
using Fraud.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("FraudDb")
    ?? throw new InvalidOperationException("Set ConnectionStrings__FraudDb to a SQL Server connection string.");
var jwtKey = builder.Configuration["Jwt:Key"];
if (string.IsNullOrWhiteSpace(jwtKey) || Encoding.UTF8.GetByteCount(jwtKey) < 32)
    throw new InvalidOperationException("Set Jwt__Key to a random secret with at least 32 bytes.");

builder.Services.AddDbContext<FraudDbContext>(options => options.UseSqlServer(connectionString,
    sql => sql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null)));
builder.Services.AddIdentityCore<IdentityUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<FraudDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

var issuer = builder.Configuration["Jwt:Issuer"] ?? "FraudRiskPlatform";
var audience = builder.Configuration["Jwt:Audience"] ?? "FraudRiskDashboard";
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey!)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments("/hubs/transactions"))
                    context.Token = token;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:5173", "http://localhost:3000", "http://localhost:3001"];
builder.Services.AddCors(options => options.AddPolicy("Dashboard", policy =>
    policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<ModelRegistry>();
builder.Services.AddSingleton<ModelTrainer>();
builder.Services.AddScoped<ModelEvaluationService>();
builder.Services.AddScoped<JwtTokenService>();
builder.Services.AddScoped<IModelTrainingService, ModelTrainingService>();
builder.Services.AddHostedService<RetrainingWorker>();

var app = builder.Build();
app.UseExceptionHandler();
app.UseCors("Dashboard");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<TransactionHub>("/hubs/transactions");
app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
app.MapGet("/health/ready", async (FraudDbContext db, ModelRegistry registry, CancellationToken ct) =>
{
    var canConnect = await db.Database.CanConnectAsync(ct);
    return canConnect
        ? Results.Ok(new { status = "ready", database = "connected", modelLoaded = registry.IsLoaded })
        : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Database unavailable");
}).AllowAnonymous();

await InitializeDatabaseAsync(app);
var modelsDirectory = app.Configuration["ML:ModelsDirectory"] ?? "artifacts/models";
modelsDirectory = ProjectPaths.Resolve(modelsDirectory);
var registry = app.Services.GetRequiredService<ModelRegistry>();
if (!registry.TryLoadLatest(modelsDirectory))
    app.Logger.LogInformation("No trained model artifact found in {ModelsDirectory}. Scoring remains disabled until training.", modelsDirectory);

app.Run();

static async Task InitializeDatabaseAsync(WebApplication app)
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<FraudDbContext>();
    await db.Database.EnsureCreatedAsync();

    var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    foreach (var roleName in new[] { "Admin", "Analyst" })
    {
        if (!await roles.RoleExistsAsync(roleName))
            await roles.CreateAsync(new IdentityRole(roleName));
    }

    var email = app.Configuration["Admin:Email"]?.Trim();
    var password = app.Configuration["Admin:Password"];
    var resetPasswordOnStartup = app.Configuration.GetValue<bool>("Admin:ResetPasswordOnStartup");
    if (!string.IsNullOrWhiteSpace(email) && !string.IsNullOrWhiteSpace(password))
    {
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var admin = await users.FindByEmailAsync(email);
        if (admin is null)
        {
            admin = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true };
            var creation = await users.CreateAsync(admin, password);
            if (!creation.Succeeded)
                throw new InvalidOperationException("Could not create configured admin account: " + string.Join("; ", creation.Errors.Select(e => e.Description)));
            app.Logger.LogInformation("Created configured local admin account {AdminEmail}", email);
        }
        else if (resetPasswordOnStartup)
        {
            // Explicitly opt-in local-development recovery switch. Never enable this in a shared deployment,
            // because the configured environment password will be applied on every API startup.
            var resetToken = await users.GeneratePasswordResetTokenAsync(admin);
            var resetResult = await users.ResetPasswordAsync(admin, resetToken, password);
            if (!resetResult.Succeeded)
                throw new InvalidOperationException("Could not reset configured admin password: " + string.Join("; ", resetResult.Errors.Select(e => e.Description)));
            var unlockResult = await users.SetLockoutEndDateAsync(admin, null);
            var failedCountResult = await users.ResetAccessFailedCountAsync(admin);
            if (!unlockResult.Succeeded || !failedCountResult.Succeeded)
                throw new InvalidOperationException("Could not clear the configured admin lockout after password reset.");
            app.Logger.LogWarning("Reset password and cleared login lockout for configured local admin {AdminEmail} because Admin:ResetPasswordOnStartup is enabled", email);
        }
        if (!await users.IsInRoleAsync(admin, "Admin"))
        {
            var roleResult = await users.AddToRoleAsync(admin, "Admin");
            if (!roleResult.Succeeded)
                throw new InvalidOperationException("Could not assign Admin role: " + string.Join("; ", roleResult.Errors.Select(e => e.Description)));
        }
    }
}

public partial class Program { }

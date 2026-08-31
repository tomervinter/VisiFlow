using System.Globalization;
using ClosedXML.Excel;
using VisiFlow.Data;
using VisiFlow.Data.Entities;
using VisiFlow.Data.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;
using Npgsql;
using Microsoft.EntityFrameworkCore.Infrastructure;

// Local `dotnet build`/`run` (Debug) doesn't copy wwwroot into bin/Debug/net8.0 in this project, so
// the content root needs to be pinned to the project's source directory (3 levels up from
// AppContext.BaseDirectory) instead of the default - otherwise static files 404 regardless of which
// directory the process was launched from. `dotnet publish` (Release - what the Docker image runs)
// DOES copy wwwroot next to the DLL, and has none of that nested bin/Debug/net8.0 structure to walk
// up out of - walking up 3 levels there lands outside the container's /app entirely. So: only apply
// the walk-up when wwwroot isn't already sitting right next to the assembly.
var contentRoot = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot"))
    ? AppContext.BaseDirectory
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = contentRoot });

// DATABASE_URL present (Render/Supabase in production) -> Postgres. Otherwise -> the local SQLite
// file, unchanged from how this has always run - local `dotnet run` during development never sets
// this, so that workflow keeps working exactly as before.
var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
var usingPostgres = !string.IsNullOrWhiteSpace(databaseUrl);

builder.Services.AddDbContext<VisiFlowDbContext>(options =>
{
    if (usingPostgres)
    {
        options.UseNpgsql(NpgsqlConnectionStringFromUrl(databaseUrl!));
    }
    else
    {
        var solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var dataDir = Path.Combine(solutionRoot, "data");
        Directory.CreateDirectory(dataDir);
        var dbPath = Path.Combine(dataDir, "visiflow.db");
        options.UseSqlite($"Data Source={dbPath}");
    }
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    // Keep C# PascalCase property names as-is so home.html can address fields directly (CustomerNumber, not customerNumber).
    options.SerializerOptions.PropertyNamingPolicy = null;
});

// Admin-interface login only (home.html) - the agent interface (agent.html) keeps its separate,
// unauthenticated identify-by-ID-number flow untouched, per explicit product decision. httpOnly
// cookie, non-Secure in dev (plain http://localhost) so local testing still works, Secure once
// actually deployed behind HTTPS (matches the pattern used in the Marketing Budget & Gantt project).
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "visiflow_auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.None : CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        // Minimal-API JSON endpoints, not a page-based app - a 401/403 status code, not the
        // framework's default redirect-to-a-login-page behavior.
        options.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        options.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<VisiFlowDbContext>();
    if (usingPostgres)
    {
        // No EF migration history on Postgres yet (deliberate simplification for the first cloud
        // deploy - see the deployment plan) - creates the current schema directly from the model.
        // A future schema change here needs a manual approach (or graduating this to real per-provider
        // migrations) since this doesn't apply incremental changes to an existing database.
        //
        // NOT EnsureCreated() - that only creates tables when the DATABASE itself doesn't exist yet.
        // Supabase's "postgres" database is always pre-provisioned, so EnsureCreated would silently
        // create nothing and every request would then fail with "relation does not exist"
        // (confirmed against a real Supabase instance while building this). CreateTablesAsync() is
        // the lower-level call that creates the tables regardless of whether the database pre-existed.
        try
        {
            await db.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>().CreateTablesAsync();
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P07") // relation already exists - fine, a previous boot already created it
        {
        }
    }
    else
    {
        db.Database.Migrate();
    }
}

app.UseDefaultFiles(new DefaultFilesOptions { DefaultFileNames = new List<string> { "home.html" } });
app.UseStaticFiles(new StaticFileOptions
{
    // home.html has no <meta charset> of its own - without this, the default "text/html" content
    // type carries no encoding hint, so the browser falls back to a locale guess and every Hebrew
    // string renders as mojibake (same fix as CARS2026.Api).
    //
    // Also disables browser caching on .html/.js - this app is a no-build single-file frontend that
    // gets edited and redeployed constantly (including live over a public tunnel link during demos),
    // so a stale cached copy silently hiding new features/fixes behind a normal refresh is a real
    // recurring problem, not a hypothetical one.
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.ContentType = "text/html; charset=utf-8";
        }
        if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || ctx.File.Name.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            ctx.Context.Response.Headers.Pragma = "no-cache";
            ctx.Context.Response.Headers.Expires = "0";
        }
    }
});

app.UseAuthentication();
app.UseAuthorization();

const long MaxImportUploadBytes = 20 * 1024 * 1024;
static string[] AllowedImportExtensions() => new[] { ".xlsx", ".xls" };

// Supabase (and most managed Postgres hosts) hand out DATABASE_URL as a postgres:// URI
// (postgres://user:pass@host:port/db) - Npgsql's own connection-string format
// (Host=...;Username=...;Password=...;Database=...) doesn't parse that directly, so this converts
// one to the other. Require+TrustServerCertificate matches how Supabase's pooler is reached (TLS,
// but not against a certificate chain Npgsql's default trust store necessarily has).
static string NpgsqlConnectionStringFromUrl(string url)
{
    var uri = new Uri(url);
    var userInfo = uri.UserInfo.Split(':', 2);
    var csb = new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.Port > 0 ? uri.Port : 5432,
        Username = Uri.UnescapeDataString(userInfo[0]),
        Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "",
        Database = uri.AbsolutePath.TrimStart('/'),
        SslMode = SslMode.Require
    };
    return csb.ConnectionString;
}

// Starting set of editable non-visit reasons, seeded once per new company (see POST /api/companies
// below) - the admin can then rename/add/remove freely from the "סיבות אי ביקור" screen.
var DEFAULT_NON_VISIT_REASONS = new[]
{
    "הלקוח לא נמצא בעסק בעת הביקור",
    "הלקוח ביקש לדחות את הפגישה למועד אחר",
    "תקלה ברכב הסוכן / בעיית הגעה לשטח",
    "העסק היה סגור באופן חריג",
    "חוסר זמן בלוח היום של הסוכן"
};

// ==================== AUTH (admin interface only - home.html) ====================
// Real username/password login, gating every existing admin endpoint below (.RequireAuthorization()
// on each MapGroup/route). The agent interface (agent.html) and its /api/agent/* endpoints are
// deliberately NOT part of this - that stays the existing identify-by-ID-number flow, unauthenticated,
// per explicit product decision (a separate, lower-stakes flow for field agents).

app.MapPost("/api/auth/login", async (LoginRequest req, VisiFlowDbContext db, IPasswordHasher<User> hasher, HttpContext ctx) =>
{
    var user = await db.Users.FirstOrDefaultAsync(u => u.Username == req.Username);
    if (user == null) return Results.Unauthorized();
    var result = hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password ?? "");
    if (result == PasswordVerificationResult.Failed) return Results.Unauthorized();

    var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
    identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
    identity.AddClaim(new Claim(ClaimTypes.Name, user.Username));
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.Ok(UserDto.From(user));
});

app.MapPost("/api/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
}).RequireAuthorization();

app.MapGet("/api/auth/me", async (HttpContext ctx, VisiFlowDbContext db) =>
{
    var userId = int.Parse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    var user = await db.Users.FindAsync(userId);
    if (user == null) return Results.Unauthorized();
    return Results.Ok(UserDto.From(user));
}).RequireAuthorization();

// User management (create/reset-password/delete other admin-interface logins) - flat permission
// model, no roles yet: any logged-in user can manage any other user, including themselves.
app.MapGet("/api/users", async (VisiFlowDbContext db) =>
    (await db.Users.OrderBy(u => u.Username).ToListAsync()).Select(UserDto.From)
).RequireAuthorization();

app.MapPost("/api/users", async (CreateUserRequest req, VisiFlowDbContext db, IPasswordHasher<User> hasher) =>
{
    if (string.IsNullOrWhiteSpace(req.Username)) return Results.BadRequest("יש להזין שם משתמש");
    if (string.IsNullOrWhiteSpace(req.DisplayName)) return Results.BadRequest("יש להזין שם תצוגה");
    if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 6) return Results.BadRequest("הסיסמה חייבת להכיל לפחות 6 תווים");
    if (await db.Companies.FindAsync(req.CompanyId) == null) return Results.BadRequest("החברה שנבחרה אינה קיימת");
    if (await db.Users.AnyAsync(u => u.Username == req.Username)) return Results.BadRequest("שם המשתמש כבר תפוס");

    var user = new User { CompanyId = req.CompanyId, Username = req.Username.Trim(), DisplayName = req.DisplayName.Trim(), CreatedAt = DateTime.UtcNow };
    user.PasswordHash = hasher.HashPassword(user, req.Password);
    db.Users.Add(user);
    await db.SaveChangesAsync();
    return Results.Created($"/api/users/{user.Id}", UserDto.From(user));
}).RequireAuthorization();

app.MapPost("/api/users/{id:int}/reset-password", async (int id, ResetPasswordRequest req, VisiFlowDbContext db, IPasswordHasher<User> hasher) =>
{
    if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 6) return Results.BadRequest("הסיסמה חייבת להכיל לפחות 6 תווים");
    var user = await db.Users.FindAsync(id);
    if (user == null) return Results.NotFound("המשתמש לא נמצא");
    user.PasswordHash = hasher.HashPassword(user, req.Password);
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization();

app.MapDelete("/api/users/{id:int}", async (int id, HttpContext ctx, VisiFlowDbContext db) =>
{
    var currentUserId = int.Parse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    if (id == currentUserId) return Results.BadRequest("לא ניתן למחוק את המשתמש שאיתו אתם מחוברים כרגע");
    var user = await db.Users.FindAsync(id);
    if (user == null) return Results.NotFound("המשתמש לא נמצא");
    db.Users.Remove(user);
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization();

// ==================== COMPANIES (tenants) ====================
// VisiFlow serves multiple companies from one deployment; every other table (customers, and later
// visit plans/users) is scoped under a CompanyId. The client keeps the currently-selected company
// in a global header selector, mirroring the pattern in CARS2026.Api.

app.MapGet("/api/companies", async (VisiFlowDbContext db) =>
    (await db.Companies.OrderBy(c => c.Name).ToListAsync()).Select(CompanyDto.From)
).RequireAuthorization();

app.MapPost("/api/companies", async (HttpRequest request, VisiFlowDbContext db) =>
{
    var form = await request.ReadFormAsync();
    var name = form["Name"].FirstOrDefault()?.Trim();
    if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest("שם חברה הוא שדה חובה");

    var company = new Company { Name = name };
    db.Companies.Add(company);
    await db.SaveChangesAsync();

    // Seed a starting set of editable non-visit reasons so the admin has something to tweak
    // instead of an empty list - see DEFAULT_NON_VISIT_REASONS below.
    for (var i = 0; i < DEFAULT_NON_VISIT_REASONS.Length; i++)
        db.NonVisitReasons.Add(new NonVisitReason { CompanyId = company.Id, Text = DEFAULT_NON_VISIT_REASONS[i], SortOrder = i });
    await db.SaveChangesAsync();

    return Results.Created($"/api/companies/{company.Id}", CompanyDto.From(company));
}).RequireAuthorization();

// ==================== CUSTOMERS (master data, loaded monthly from Excel) ====================

// Each customer file upload is its own (year, month) snapshot (see Customer.cs) - callers must say
// which month's snapshot they want, same as distribution days already require.
app.MapGet("/api/customers", async (int companyId, int year, int month, VisiFlowDbContext db) =>
{
    var customers = await db.Customers.Where(c => c.CompanyId == companyId && c.Year == year && c.Month == month)
        .OrderBy(c => c.CustomerNumber).ToListAsync();
    var standards = await db.CustomerVisitStandards.Where(s => s.CompanyId == companyId).ToDictionaryAsync(s => s.CustomerNumber);
    return customers.Select(c => CustomerDto.From(c, standards.TryGetValue(c.CustomerNumber, out var s) ? s.RequiredVisitsPerWeek : null));
}).RequireAuthorization();

// Which (year, month) snapshots actually have data - lets the client pick a sensible default month
// to display instead of guessing, and lets the visit-plan screen tell the admin up front that a
// month has no customer file loaded yet rather than only failing once they hit "generate".
app.MapGet("/api/customers/months", async (int companyId, VisiFlowDbContext db) =>
    (await db.Customers.Where(c => c.CompanyId == companyId).Select(c => new { c.Year, c.Month }).Distinct().ToListAsync())
        .OrderByDescending(x => x.Year).ThenByDescending(x => x.Month)
        .Select(x => new { x.Year, x.Month })
).RequireAuthorization();

// Bulk wipe for the "מחיקת החודש הנבחר" danger button - lets an admin clear one month's customer
// snapshot and reload it from scratch (e.g. after a bad import) rather than deleting rows one by
// one. year/month are optional only for backward compatibility with any other caller; the UI button
// always passes the year/month currently selected in the customers screen's own picker, so in
// practice this only ever wipes the one snapshot the admin is looking at, not the whole company's
// history. Distribution days reference CustomerNumber as a plain string, not a foreign key (see
// CustomerDistributionDay), so this can't violate any FK constraint - those rows are just left
// pointing at a customer number that no longer exists in the customers table.
app.MapDelete("/api/customers", async (int companyId, int? year, int? month, VisiFlowDbContext db) =>
{
    if (await db.Companies.FindAsync(companyId) == null) return Results.BadRequest("החברה שנבחרה אינה קיימת");
    var query = db.Customers.Where(c => c.CompanyId == companyId);
    if (year.HasValue && month.HasValue) query = query.Where(c => c.Year == year.Value && c.Month == month.Value);
    var deleted = await query.ExecuteDeleteAsync();
    return Results.Ok(new DeleteCustomersResultDto(deleted));
}).RequireAuthorization();

// "תקן ביקורים ללקוח" - lets an admin select a customer or a group of customers (from the
// customers table itself, via checkboxes) and set/clear their required weekly visit count in one
// bulk action. Plain JSON body (not multipart) since there's no file involved here. Writes to the
// separate CustomerVisitStandard table (not the monthly Customer snapshot) so the standard survives
// automatically the next time a new month's customer file is uploaded.
app.MapPost("/api/customers/visitstandard", async (BulkVisitStandardRequest req, VisiFlowDbContext db) =>
{
    if (await db.Companies.FindAsync(req.CompanyId) == null) return Results.BadRequest("החברה שנבחרה אינה קיימת");
    if (req.CustomerNumbers == null || req.CustomerNumbers.Count == 0) return Results.BadRequest("יש לבחור לפחות לקוח אחד");
    if (req.RequiredVisitsPerWeek is < 0) return Results.BadRequest("תקן הביקורים לא יכול להיות שלילי");

    var existing = await db.CustomerVisitStandards
        .Where(s => s.CompanyId == req.CompanyId && req.CustomerNumbers.Contains(s.CustomerNumber))
        .ToDictionaryAsync(s => s.CustomerNumber);
    var now = DateTime.UtcNow;
    foreach (var customerNumber in req.CustomerNumbers)
    {
        if (!existing.TryGetValue(customerNumber, out var standard))
        {
            standard = new CustomerVisitStandard { CompanyId = req.CompanyId, CustomerNumber = customerNumber };
            db.CustomerVisitStandards.Add(standard);
        }
        standard.RequiredVisitsPerWeek = req.RequiredVisitsPerWeek;
        standard.UpdatedAt = now;
    }
    await db.SaveChangesAsync();
    return Results.Ok(new { Updated = req.CustomerNumbers.Count });
}).RequireAuthorization();

// (Label, Key, Required) - order/labels match exactly what the user specified for the monthly
// customer upload. Matched by header text (not column position) so re-ordered columns still work.
var CUSTOMER_IMPORT_COLUMNS = new (string Label, string Key, bool Required)[]
{
    ("מספר לקוח", "CustomerNumber", true),
    ("שם לקוח", "CustomerName", true),
    ("סוכן משוייך", "AgentName", false),
    ("תעודת זהות סוכן", "AgentIdNumber", false),
    ("ערוץ מכר", "Channel", false),
    ("מכירות מצטברות השנה", "SalesYtdCurrentYear", false),
    ("מכירות מצטברות אשתקד", "SalesYtdPreviousYear", false),
    ("גודל לקוח", "CustomerSize", false),
    ("טלפון", "Phone", false),
    ("כתובת", "Address", false),
    ("עיר", "City", false),
    ("כמות הזמנות ממוצעת בחודש", "AvgMonthlyOrders", false),
    ("לקוח פעיל בכל התקופה (כן/לא)", "WasActiveAllPeriod", false),
    ("סטטוס לקוח (פעיל/לא פעיל)", "Status", false),
};

static decimal? ParseDecimalCell(IXLCell cell)
{
    if (cell.IsEmpty()) return null;
    if (cell.TryGetValue(out double d)) return (decimal)d;
    var s = cell.GetString().Trim();
    if (string.IsNullOrEmpty(s)) return null;
    return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var dec) ? dec : (decimal?)null;
}

static bool ParseActiveFlag(IXLCell cell)
{
    var s = cell.GetString().Trim();
    return s is "כן" or "Yes" or "yes" or "1" or "true" or "TRUE" or "True";
}

static bool ParseCheckFlag(IXLCell cell)
{
    var s = cell.GetString().Trim();
    return s is "כן" or "Yes" or "yes" or "1" or "true" or "TRUE" or "True" or "V" or "v" or "X" or "x";
}

static CustomerStatus ParseCustomerStatus(IXLCell cell)
{
    var s = cell.GetString().Trim();
    return s is "לא פעיל" or "לא" or "Inactive" or "inactive" or "0" ? CustomerStatus.Inactive : CustomerStatus.Active;
}

app.MapPost("/api/customers/import", async (HttpRequest request, VisiFlowDbContext db) =>
{
    if (!request.HasFormContentType) return Results.BadRequest("יש להעלות כקובץ multipart/form-data");
    var form = await request.ReadFormAsync();

    if (!int.TryParse(form["CompanyId"].FirstOrDefault(), out var companyId))
        return Results.BadRequest("יש לבחור חברה לפני הטעינה");
    var company = await db.Companies.FindAsync(companyId);
    if (company == null) return Results.BadRequest("החברה שנבחרה אינה קיימת");
    if (!int.TryParse(form["Year"].FirstOrDefault(), out var year) || year < 2000 || year > 2100) return Results.BadRequest("יש לבחור שנה תקינה");
    if (!int.TryParse(form["Month"].FirstOrDefault(), out var month) || month < 1 || month > 12) return Results.BadRequest("יש לבחור חודש תקין");

    var file = form.Files.GetFile("file");
    if (file == null || file.Length == 0) return Results.BadRequest("לא נבחר קובץ");
    if (file.Length > MaxImportUploadBytes) return Results.BadRequest("הקובץ גדול מדי (מקסימום 20MB)");
    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (!AllowedImportExtensions().Contains(ext)) return Results.BadRequest($"סוג קובץ לא נתמך '{ext}' - יש להעלות קובץ אקסל (.xlsx)");

    IXLWorksheet ws;
    try
    {
        using var stream = file.OpenReadStream();
        using var workbook = new XLWorkbook(stream);
        ws = workbook.Worksheets.First();

        var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        if (lastCol == 0 || lastRow < 2) return Results.BadRequest("הקובץ ריק או לא מכיל שורות נתונים");

        // Map each expected column to its position in this file's header row (row 1).
        var colByKey = new Dictionary<string, int>();
        for (var col = 1; col <= lastCol; col++)
        {
            var header = ws.Cell(1, col).GetString().Trim();
            var match = CUSTOMER_IMPORT_COLUMNS.FirstOrDefault(h => h.Label == header);
            if (match.Key != null) colByKey[match.Key] = col;
        }
        var missing = CUSTOMER_IMPORT_COLUMNS.Where(h => h.Required && !colByKey.ContainsKey(h.Key)).Select(h => h.Label).ToList();
        if (missing.Count > 0) return Results.BadRequest($"חסרות עמודות חובה בקובץ: {string.Join(", ", missing)}");

        // Optional columns whose header text didn't match any of CUSTOMER_IMPORT_COLUMNS' exact
        // labels are silently skipped (left null on every row) rather than rejecting the whole file -
        // that's the right behavior for genuinely-omitted columns, but indistinguishable from "the
        // column is there under slightly different wording" without surfacing it, so it's reported
        // back for the client to show alongside the created/updated counts.
        var unmatchedOptional = CUSTOMER_IMPORT_COLUMNS.Where(h => !h.Required && !colByKey.ContainsKey(h.Key)).Select(h => h.Label).ToList();

        // Matched by (CustomerNumber, Year, Month) - re-uploading the SAME month's file again updates
        // that month's rows in place (fixing a bad upload), but a different month always gets its own
        // fresh rows rather than overwriting an earlier month's snapshot.
        var existingByNumber = await db.Customers
            .Where(c => c.CompanyId == companyId && c.Year == year && c.Month == month)
            .ToDictionaryAsync(c => c.CustomerNumber);
        var errors = new List<string>();
        int created = 0, updated = 0;
        var now = DateTime.UtcNow;

        for (var row = 2; row <= lastRow; row++)
        {
            var xlRow = ws.Row(row);
            if (xlRow.IsEmpty()) continue;

            string? Text(string key) => colByKey.TryGetValue(key, out var col) ? xlRow.Cell(col).GetString().Trim() : null;

            var customerNumber = Text("CustomerNumber");
            if (string.IsNullOrWhiteSpace(customerNumber)) { errors.Add($"שורה {row}: חסר מספר לקוח"); continue; }
            var customerName = Text("CustomerName");
            if (string.IsNullOrWhiteSpace(customerName)) { errors.Add($"שורה {row}: חסר שם לקוח (לקוח {customerNumber})"); continue; }

            var salesCurrent = colByKey.TryGetValue("SalesYtdCurrentYear", out var scCol) ? ParseDecimalCell(xlRow.Cell(scCol)) : null;
            var salesPrevious = colByKey.TryGetValue("SalesYtdPreviousYear", out var spCol) ? ParseDecimalCell(xlRow.Cell(spCol)) : null;
            var avgOrders = colByKey.TryGetValue("AvgMonthlyOrders", out var aoCol) ? ParseDecimalCell(xlRow.Cell(aoCol)) : null;
            var wasActive = colByKey.TryGetValue("WasActiveAllPeriod", out var waCol) && ParseActiveFlag(xlRow.Cell(waCol));
            var status = colByKey.TryGetValue("Status", out var stCol) ? ParseCustomerStatus(xlRow.Cell(stCol)) : CustomerStatus.Active;

            if (!existingByNumber.TryGetValue(customerNumber, out var customer))
            {
                customer = new Customer { CompanyId = companyId, CustomerNumber = customerNumber, Year = year, Month = month };
                db.Customers.Add(customer);
                existingByNumber[customerNumber] = customer;
                created++;
            }
            else
            {
                updated++;
            }

            customer.CustomerName = customerName;
            customer.AgentName = Text("AgentName");
            customer.AgentIdNumber = Text("AgentIdNumber");
            customer.Channel = Text("Channel");
            customer.SalesYtdCurrentYear = salesCurrent;
            customer.SalesYtdPreviousYear = salesPrevious;
            customer.CustomerSize = Text("CustomerSize");
            customer.Phone = Text("Phone");
            customer.Address = Text("Address");
            customer.City = Text("City");
            customer.AvgMonthlyOrders = avgOrders;
            customer.WasActiveAllPeriod = wasActive;
            customer.Status = status;
            customer.UpdatedAt = now;
        }

        await db.SaveChangesAsync();
        return Results.Ok(new CustomerImportResultDto(created, updated, errors, unmatchedOptional));
    }
    catch (Exception ex) when (ex is not BadHttpRequestException)
    {
        return Results.BadRequest($"שגיאה בקריאת קובץ האקסל: {ex.Message}");
    }
}).RequireAuthorization();

app.MapGet("/api/customers/import/template", () =>
{
    using var wb = new XLWorkbook();
    var ws = wb.Worksheets.Add("לקוחות");
    ws.RightToLeft = true;
    for (var i = 0; i < CUSTOMER_IMPORT_COLUMNS.Length; i++)
    {
        var cell = ws.Cell(1, i + 1);
        cell.Value = CUSTOMER_IMPORT_COLUMNS[i].Label;
        cell.Style.Font.Bold = true;
    }
    var sample = new object[] { "1001", "לקוח לדוגמה בע\"מ", "ישראל ישראלי", "123456789", "רשתות", 120000, 98000, "A", "050-1234567", "רחוב הדוגמה 1, תל אביב", "תל אביב", 8, "כן", "פעיל" };
    for (var i = 0; i < sample.Length; i++) ws.Cell(2, i + 1).Value = sample[i].ToString();
    ws.Columns().AdjustToContents();

    using var ms = new MemoryStream();
    wb.SaveAs(ms);
    return Results.File(ms.ToArray(),
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "תבנית-טעינת-לקוחות.xlsx");
}).RequireAuthorization();

// ==================== NON-VISIT REASONS (editable catalog) ====================
// Later surfaced to the agent's tablet/phone app when they log that they didn't visit a customer
// they were scheduled to see - not built yet, this is just the admin-editable catalog.

// Deliberately NOT behind auth - agent.html (unauthenticated by design) fetches this too, to
// populate its own "reason didn't visit" dropdown.
app.MapGet("/api/nonvisitreasons", async (int companyId, VisiFlowDbContext db) =>
    (await db.NonVisitReasons.Where(r => r.CompanyId == companyId).OrderBy(r => r.SortOrder).ToListAsync())
        .Select(NonVisitReasonDto.From));

app.MapPost("/api/nonvisitreasons", async (HttpRequest request, VisiFlowDbContext db) =>
{
    var form = await request.ReadFormAsync();
    if (!int.TryParse(form["CompanyId"].FirstOrDefault(), out var companyId)) return Results.BadRequest("יש לבחור חברה");
    var company = await db.Companies.FindAsync(companyId);
    if (company == null) return Results.BadRequest("החברה שנבחרה אינה קיימת");
    var text = form["Text"].FirstOrDefault()?.Trim();
    if (string.IsNullOrWhiteSpace(text)) return Results.BadRequest("יש להזין את נוסח הסיבה");

    var maxOrder = await db.NonVisitReasons.Where(r => r.CompanyId == companyId).Select(r => (int?)r.SortOrder).MaxAsync() ?? -1;
    var reason = new NonVisitReason { CompanyId = companyId, Text = text, SortOrder = maxOrder + 1 };
    db.NonVisitReasons.Add(reason);
    await db.SaveChangesAsync();
    return Results.Created($"/api/nonvisitreasons/{reason.Id}", NonVisitReasonDto.From(reason));
}).RequireAuthorization();

app.MapPost("/api/nonvisitreasons/{id:int}", async (int id, HttpRequest request, VisiFlowDbContext db) =>
{
    var reason = await db.NonVisitReasons.FindAsync(id);
    if (reason == null) return Results.NotFound();
    var form = await request.ReadFormAsync();
    var text = form["Text"].FirstOrDefault()?.Trim();
    if (string.IsNullOrWhiteSpace(text)) return Results.BadRequest("יש להזין את נוסח הסיבה");
    reason.Text = text;
    await db.SaveChangesAsync();
    return Results.Ok(NonVisitReasonDto.From(reason));
}).RequireAuthorization();

app.MapDelete("/api/nonvisitreasons/{id:int}", async (int id, VisiFlowDbContext db) =>
{
    var reason = await db.NonVisitReasons.FindAsync(id);
    if (reason == null) return Results.NotFound();
    db.NonVisitReasons.Remove(reason);
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization();

// ==================== CUSTOMER DISTRIBUTION DAYS (monthly, from Excel) ====================
// Which weekdays each customer normally gets a delivery. Treated as fixed month to month, but each
// upload targets one specific (year, month) so an admin can correct just the month something changed
// in, without disturbing the rest.

var DISTRIBUTION_IMPORT_COLUMNS = new (string Label, string Key, bool Required)[]
{
    ("מספר לקוח", "CustomerNumber", true),
    ("ראשון", "Sunday", false),
    ("שני", "Monday", false),
    ("שלישי", "Tuesday", false),
    ("רביעי", "Wednesday", false),
    ("חמישי", "Thursday", false),
    ("שישי", "Friday", false),
    ("שבת", "Saturday", false),
};

app.MapGet("/api/distributiondays", async (int companyId, int year, int month, VisiFlowDbContext db) =>
    (await db.CustomerDistributionDays.Where(d => d.CompanyId == companyId && d.Year == year && d.Month == month)
        .OrderBy(d => d.CustomerNumber).ToListAsync())
        .Select(CustomerDistributionDayDto.From)
).RequireAuthorization();

// Bulk wipe for the "מחיקת כל הנתונים" danger button - clears every month/year for the company, same
// pattern as DELETE /api/customers.
app.MapDelete("/api/distributiondays", async (int companyId, VisiFlowDbContext db) =>
{
    if (await db.Companies.FindAsync(companyId) == null) return Results.BadRequest("החברה שנבחרה אינה קיימת");
    var deleted = await db.CustomerDistributionDays.Where(d => d.CompanyId == companyId).ExecuteDeleteAsync();
    return Results.Ok(new DeleteCustomersResultDto(deleted));
}).RequireAuthorization();

app.MapPost("/api/distributiondays/import", async (HttpRequest request, VisiFlowDbContext db) =>
{
    if (!request.HasFormContentType) return Results.BadRequest("יש להעלות כקובץ multipart/form-data");
    var form = await request.ReadFormAsync();

    if (!int.TryParse(form["CompanyId"].FirstOrDefault(), out var companyId)) return Results.BadRequest("יש לבחור חברה לפני הטעינה");
    if (await db.Companies.FindAsync(companyId) == null) return Results.BadRequest("החברה שנבחרה אינה קיימת");
    if (!int.TryParse(form["Year"].FirstOrDefault(), out var year) || year < 2000 || year > 2100) return Results.BadRequest("יש לבחור שנה תקינה");
    if (!int.TryParse(form["Month"].FirstOrDefault(), out var month) || month < 1 || month > 12) return Results.BadRequest("יש לבחור חודש תקין");
    // When set, the same file's data is applied identically to all 12 months of the chosen year -
    // lets an admin load a full year's (usually-fixed) distribution schedule in one upload instead
    // of re-uploading the same file 12 times. Month above is then irrelevant for this request but
    // still required/validated, since a single-month upload remains the default path.
    var applyToWholeYear = form["ApplyToWholeYear"].FirstOrDefault() == "true";

    var file = form.Files.GetFile("file");
    if (file == null || file.Length == 0) return Results.BadRequest("לא נבחר קובץ");
    if (file.Length > MaxImportUploadBytes) return Results.BadRequest("הקובץ גדול מדי (מקסימום 20MB)");
    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (!AllowedImportExtensions().Contains(ext)) return Results.BadRequest($"סוג קובץ לא נתמך '{ext}' - יש להעלות קובץ אקסל (.xlsx)");

    try
    {
        using var stream = file.OpenReadStream();
        using var workbook = new XLWorkbook(stream);
        var ws = workbook.Worksheets.First();

        var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        if (lastCol == 0 || lastRow < 2) return Results.BadRequest("הקובץ ריק או לא מכיל שורות נתונים");

        var colByKey = new Dictionary<string, int>();
        for (var col = 1; col <= lastCol; col++)
        {
            var header = ws.Cell(1, col).GetString().Trim();
            var match = DISTRIBUTION_IMPORT_COLUMNS.FirstOrDefault(h => h.Label == header);
            if (match.Key != null) colByKey[match.Key] = col;
        }
        var missing = DISTRIBUTION_IMPORT_COLUMNS.Where(h => h.Required && !colByKey.ContainsKey(h.Key)).Select(h => h.Label).ToList();
        if (missing.Count > 0) return Results.BadRequest($"חסרות עמודות חובה בקובץ: {string.Join(", ", missing)}");

        // Parse the file once regardless of how many months it'll be applied to - row-level errors
        // are about the file itself, not any particular target month, so they'd otherwise be
        // reported (and counted) once per month when applying to a whole year.
        var parsedRows = new List<(string CustomerNumber, bool Sunday, bool Monday, bool Tuesday, bool Wednesday, bool Thursday, bool Friday, bool Saturday)>();
        var errors = new List<string>();
        for (var row = 2; row <= lastRow; row++)
        {
            var xlRow = ws.Row(row);
            if (xlRow.IsEmpty()) continue;

            var customerNumber = xlRow.Cell(colByKey["CustomerNumber"]).GetString().Trim();
            if (string.IsNullOrWhiteSpace(customerNumber)) { errors.Add($"שורה {row}: חסר מספר לקוח"); continue; }

            bool Flag(string key) => colByKey.TryGetValue(key, out var col) && ParseCheckFlag(xlRow.Cell(col));
            parsedRows.Add((customerNumber, Flag("Sunday"), Flag("Monday"), Flag("Tuesday"), Flag("Wednesday"), Flag("Thursday"), Flag("Friday"), Flag("Saturday")));
        }

        var targetMonths = applyToWholeYear ? Enumerable.Range(1, 12) : new[] { month };
        int created = 0, updated = 0;

        foreach (var targetMonth in targetMonths)
        {
            var existingByNumber = await db.CustomerDistributionDays
                .Where(d => d.CompanyId == companyId && d.Year == year && d.Month == targetMonth)
                .ToDictionaryAsync(d => d.CustomerNumber);

            foreach (var r in parsedRows)
            {
                if (!existingByNumber.TryGetValue(r.CustomerNumber, out var entry))
                {
                    entry = new CustomerDistributionDay { CompanyId = companyId, CustomerNumber = r.CustomerNumber, Year = year, Month = targetMonth };
                    db.CustomerDistributionDays.Add(entry);
                    existingByNumber[r.CustomerNumber] = entry;
                    created++;
                }
                else
                {
                    updated++;
                }

                entry.Sunday = r.Sunday;
                entry.Monday = r.Monday;
                entry.Tuesday = r.Tuesday;
                entry.Wednesday = r.Wednesday;
                entry.Thursday = r.Thursday;
                entry.Friday = r.Friday;
                entry.Saturday = r.Saturday;
            }
        }

        await db.SaveChangesAsync();
        return Results.Ok(new DistributionImportResultDto(created, updated, errors));
    }
    catch (Exception ex) when (ex is not BadHttpRequestException)
    {
        return Results.BadRequest($"שגיאה בקריאת קובץ האקסל: {ex.Message}");
    }
}).RequireAuthorization();

app.MapGet("/api/distributiondays/import/template", () =>
{
    using var wb = new XLWorkbook();
    var ws = wb.Worksheets.Add("ימי הפצה");
    ws.RightToLeft = true;
    for (var i = 0; i < DISTRIBUTION_IMPORT_COLUMNS.Length; i++)
    {
        var cell = ws.Cell(1, i + 1);
        cell.Value = DISTRIBUTION_IMPORT_COLUMNS[i].Label;
        cell.Style.Font.Bold = true;
    }
    var sample = new object[] { "1001", "V", "", "V", "", "V", "", "" };
    for (var i = 0; i < sample.Length; i++) ws.Cell(2, i + 1).Value = sample[i].ToString();
    ws.Columns().AdjustToContents();

    using var ms = new MemoryStream();
    wb.SaveAs(ms);
    return Results.File(ms.ToArray(),
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "תבנית-ימי-הפצה.xlsx");
}).RequireAuthorization();

// ==================== WORK CALENDAR ====================
// Sparse: only days an admin explicitly set are stored here. Any date with no row falls back to the
// natural default (Sunday-Thursday = Full workday, Friday/Saturday = Off) - computed client-side,
// same rule GET below documents for callers that want it server-side too.

app.MapGet("/api/workcalendar", async (int companyId, int year, int month, VisiFlowDbContext db) =>
{
    var from = new DateTime(year, month, 1);
    var to = from.AddMonths(1);
    return (await db.WorkCalendarDays
        .Where(d => d.CompanyId == companyId && d.Date >= from && d.Date < to)
        .OrderBy(d => d.Date).ToListAsync())
        .Select(WorkCalendarDayDto.From);
}).RequireAuthorization();

app.MapPost("/api/workcalendar", async (HttpRequest request, VisiFlowDbContext db) =>
{
    var form = await request.ReadFormAsync();
    if (!int.TryParse(form["CompanyId"].FirstOrDefault(), out var companyId)) return Results.BadRequest("יש לבחור חברה");
    if (await db.Companies.FindAsync(companyId) == null) return Results.BadRequest("החברה שנבחרה אינה קיימת");
    if (!DateTime.TryParse(form["Date"].FirstOrDefault(), out var date)) return Results.BadRequest("תאריך לא תקין");
    if (!Enum.TryParse<WorkDayType>(form["DayType"].FirstOrDefault(), out var dayType)) return Results.BadRequest("סוג יום לא תקין");

    date = date.Date;
    var entry = await db.WorkCalendarDays.FirstOrDefaultAsync(d => d.CompanyId == companyId && d.Date == date);
    if (entry == null)
    {
        entry = new WorkCalendarDay { CompanyId = companyId, Date = date, DayType = dayType };
        db.WorkCalendarDays.Add(entry);
    }
    else
    {
        entry.DayType = dayType;
    }
    await db.SaveChangesAsync();
    return Results.Ok(WorkCalendarDayDto.From(entry));
}).RequireAuthorization();

// ==================== CUSTOMER VISITS (visit log) ====================
// Records actual visits (or logged misses, with a reason from NonVisitReasons) - the data source
// the visit-planning algorithm will use for "days since last visit". Entered by an admin today;
// this is what an agent's tablet/phone app would eventually write directly.

app.MapGet("/api/customervisits", async (int companyId, VisiFlowDbContext db) =>
{
    var visits = await db.CustomerVisits.Include(v => v.NonVisitReason).Where(v => v.CompanyId == companyId)
        .OrderByDescending(v => v.VisitDate).ThenByDescending(v => v.Id).ToListAsync();
    // Channel/City are enriched from the customer snapshot matching the VISIT's own month (see
    // Customer.cs) - the closest approximation of "what the customer looked like around that visit".
    // Falls back to null gracefully if no snapshot exists for that exact month.
    var customerByKey = (await db.Customers.Where(c => c.CompanyId == companyId).ToListAsync())
        .ToDictionary(c => (c.CustomerNumber, c.Year, c.Month));
    return Results.Ok(visits.Select(v =>
    {
        customerByKey.TryGetValue((v.CustomerNumber, v.VisitDate.Year, v.VisitDate.Month), out var customer);
        return CustomerVisitDto.From(v, customer);
    }));
}).RequireAuthorization();

// Deliberately NOT behind auth - this is the agent's own "mark visited/not visited" write path
// (submitVisit() in agent.html), which is unauthenticated by design just like the rest of that app.
app.MapPost("/api/customervisits", async (CreateCustomerVisitRequest req, VisiFlowDbContext db) =>
{
    if (await db.Companies.FindAsync(req.CompanyId) == null) return Results.BadRequest("החברה שנבחרה אינה קיימת");
    if (string.IsNullOrWhiteSpace(req.CustomerNumber)) return Results.BadRequest("יש לבחור לקוח");
    if (!await db.Customers.AnyAsync(c => c.CompanyId == req.CompanyId && c.CustomerNumber == req.CustomerNumber))
        return Results.BadRequest("הלקוח שנבחר אינו קיים בבסיס הלקוחות של החברה");
    if (!Enum.TryParse<VisitOutcome>(req.Outcome, out var outcome)) return Results.BadRequest("סטטוס ביקור לא תקין");
    if (outcome == VisitOutcome.NotVisited && req.NonVisitReasonId == null) return Results.BadRequest("יש לבחור סיבה כאשר הביקור לא בוצע");

    NonVisitReason? reason = null;
    if (req.NonVisitReasonId != null)
    {
        reason = await db.NonVisitReasons.FindAsync(req.NonVisitReasonId.Value);
        if (reason == null || reason.CompanyId != req.CompanyId) return Results.BadRequest("הסיבה שנבחרה אינה קיימת");
    }

    var visitDate = req.VisitDate.Date;
    // One entry per (customer, day): the agent-facing page lets someone tap visited/not-visited and
    // change their mind for the same day, so re-submitting for a day that already has an entry
    // updates it in place instead of piling up duplicates.
    var visit = await db.CustomerVisits.FirstOrDefaultAsync(v =>
        v.CompanyId == req.CompanyId && v.CustomerNumber == req.CustomerNumber && v.VisitDate == visitDate);
    var isNew = visit == null;
    if (visit == null)
    {
        visit = new CustomerVisit { CompanyId = req.CompanyId, CustomerNumber = req.CustomerNumber, VisitDate = visitDate, CreatedAt = DateTime.UtcNow };
        db.CustomerVisits.Add(visit);
    }
    visit.AgentName = string.IsNullOrWhiteSpace(req.AgentName) ? null : req.AgentName;
    visit.Outcome = outcome;
    visit.NonVisitReasonId = outcome == VisitOutcome.NotVisited ? req.NonVisitReasonId : null;
    visit.Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes;

    await db.SaveChangesAsync();
    visit.NonVisitReason = reason;
    return isNew ? Results.Created($"/api/customervisits/{visit.Id}", CustomerVisitDto.From(visit)) : Results.Ok(CustomerVisitDto.From(visit));
});

app.MapDelete("/api/customervisits/{id:int}", async (int id, VisiFlowDbContext db) =>
{
    var visit = await db.CustomerVisits.FindAsync(id);
    if (visit == null) return Results.NotFound();
    db.CustomerVisits.Remove(visit);
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization();

// Same pattern as /api/visitplan/entries/bulkdelete - backs the "יומן ביקורים" screen's checkbox
// multi-select + "מחיקת הנתונים" button, for deleting a chosen set of visit-log rows at once instead
// of one at a time.
app.MapPost("/api/customervisits/bulkdelete", async (BulkDeleteCustomerVisitsRequest req, VisiFlowDbContext db) =>
{
    if (req.Ids == null || req.Ids.Count == 0) return Results.BadRequest("יש לבחור לפחות שורה אחת למחיקה");
    var deleted = await db.CustomerVisits.Where(v => req.Ids.Contains(v.Id)).ExecuteDeleteAsync();
    return Results.Ok(new DeleteCustomersResultDto(deleted));
}).RequireAuthorization();

// ==================== VISIT PLAN (next-month planning algorithm) ====================
// See VisitPlanGenerator.cs for the actual scoring/scheduling logic.

app.MapGet("/api/visitplan/weights", async (int companyId, VisiFlowDbContext db) =>
{
    var weights = await db.VisitPlanWeights.FirstOrDefaultAsync(w => w.CompanyId == companyId);
    if (weights == null)
    {
        weights = new VisitPlanWeights { CompanyId = companyId };
        db.VisitPlanWeights.Add(weights);
        await db.SaveChangesAsync();
    }
    return Results.Ok(VisitPlanWeightsDto.From(weights));
}).RequireAuthorization();

app.MapPost("/api/visitplan/weights", async (VisitPlanWeightsDto req, VisiFlowDbContext db) =>
{
    if (await db.Companies.FindAsync(req.CompanyId) == null) return Results.BadRequest("החברה שנבחרה אינה קיימת");
    var sum = req.SalesDropWeight + req.DistributionWeight + req.FrequencyWeight + req.VisitStandardWeight + req.DaysSinceVisitWeight;
    if (Math.Abs(sum - 100) > 0.1m) return Results.BadRequest($"סכום המשקלים חייב להיות 100% (כרגע {sum}%)");
    if (req.FullDayCapacity < 1) return Results.BadRequest("קיבולת יום מלא חייבת להיות לפחות 1");
    if (req.HalfDayCapacity < 1) return Results.BadRequest("קיבולת חצי יום חייבת להיות לפחות 1");

    var weights = await db.VisitPlanWeights.FirstOrDefaultAsync(w => w.CompanyId == req.CompanyId);
    if (weights == null) { weights = new VisitPlanWeights { CompanyId = req.CompanyId }; db.VisitPlanWeights.Add(weights); }
    weights.SalesDropWeight = req.SalesDropWeight;
    weights.DistributionWeight = req.DistributionWeight;
    weights.FrequencyWeight = req.FrequencyWeight;
    weights.VisitStandardWeight = req.VisitStandardWeight;
    weights.DaysSinceVisitWeight = req.DaysSinceVisitWeight;
    weights.FullDayCapacity = req.FullDayCapacity;
    weights.HalfDayCapacity = req.HalfDayCapacity;
    await db.SaveChangesAsync();
    return Results.Ok(VisitPlanWeightsDto.From(weights));
}).RequireAuthorization();

app.MapPost("/api/visitplan/generate", async (GenerateVisitPlanRequest req, VisiFlowDbContext db) =>
{
    if (await db.Companies.FindAsync(req.CompanyId) == null) return Results.BadRequest("החברה שנבחרה אינה קיימת");
    if (req.Month < 1 || req.Month > 12) return Results.BadRequest("חודש לא תקין");
    // Customer data is a monthly snapshot now (see Customer.cs) - a plan can only be built from that
    // exact month's own upload, never silently borrowed from a different month's data.
    if (!await db.Customers.AnyAsync(c => c.CompanyId == req.CompanyId && c.Year == req.Year && c.Month == req.Month))
        return Results.BadRequest($"לא נטען קובץ לקוחות עבור {req.Month:00}/{req.Year} - יש לטעון קודם קובץ לקוחות לחודש הזה במסך \"לקוחות\"");
    var result = await VisitPlanGenerator.GenerateAsync(db, req.CompanyId, req.Year, req.Month);
    return Results.Ok(result);
}).RequireAuthorization();

app.MapGet("/api/visitplan", async (int companyId, int year, int month, VisiFlowDbContext db) =>
{
    // SQLite/EF Core can't translate ORDER BY on a decimal column into SQL - order in memory instead.
    var entries = (await db.VisitPlanEntries
        .Where(e => e.CompanyId == companyId && e.PlanYear == year && e.PlanMonth == month)
        .ToListAsync())
        .OrderByDescending(e => e.PriorityScore).ToList();
    // The plan's own (year, month) is exactly the customer snapshot it was built from - entries never
    // span months (see VisitPlanGenerator), so a single Customer.Year/Month filter here is correct.
    var customers = await db.Customers.Where(c => c.CompanyId == companyId && c.Year == year && c.Month == month)
        .ToDictionaryAsync(c => c.CustomerNumber);
    var standards = await db.CustomerVisitStandards.Where(s => s.CompanyId == companyId).ToDictionaryAsync(s => s.CustomerNumber);
    return Results.Ok(entries.Select(e => VisitPlanEntryDto.From(e, customers.GetValueOrDefault(e.CustomerNumber),
        standards.TryGetValue(e.CustomerNumber, out var s) ? s.RequiredVisitsPerWeek : null)));
}).RequireAuthorization();

// ==================== AGENT-FACING API (agent.html) ====================
// No real authentication yet - explicit user decision to defer that. "Identification" here is just
// matching the entered ID number against Customer.AgentIdNumber (loaded from the customer Excel
// import); that field is meant to become the real login username once proper auth exists.

// The agent's daily list is now driven by the generated visit plan (VisitPlanEntry.PlannedDate),
// not "all of my customers" - lets an agent pick any date and see exactly who they're scheduled to
// visit that day, per the priority-ranked plan an admin generated.
app.MapGet("/api/agent/visitplan", async (string agentIdNumber, DateTime date, VisiFlowDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(agentIdNumber)) return Results.BadRequest("יש להזין תעודת זהות");
    var day = date.Date;

    var entries = await db.VisitPlanEntries.Where(e => e.PlannedDate == day).ToListAsync();
    if (entries.Count == 0) return Results.Ok(new List<AgentVisitPlanEntryDto>());

    // CustomerNumber is only unique within its own company - match/join on (CompanyId,
    // CustomerNumber) pairs throughout, not CustomerNumber alone. Every matched entry's PlannedDate is
    // this same single `day`, so its PlanYear/PlanMonth is always day.Year/day.Month too (a plan
    // entry's month always matches its own PlannedDate - see the reschedule endpoint) - one Year/Month
    // filter on the customer snapshot is therefore correct for the whole batch.
    var companyIds = entries.Select(e => e.CompanyId).Distinct().ToList();
    var customerNumbers = entries.Select(e => e.CustomerNumber).Distinct().ToList();
    var customerByKey = await db.Customers
        .Where(c => companyIds.Contains(c.CompanyId) && customerNumbers.Contains(c.CustomerNumber)
            && c.Year == day.Year && c.Month == day.Month && c.AgentIdNumber == agentIdNumber)
        .ToDictionaryAsync(c => (c.CompanyId, c.CustomerNumber));

    var matched = entries.Where(e => customerByKey.ContainsKey((e.CompanyId, e.CustomerNumber))).ToList();
    if (matched.Count == 0) return Results.Ok(new List<AgentVisitPlanEntryDto>());

    var visits = await db.CustomerVisits.Include(v => v.NonVisitReason)
        .Where(v => companyIds.Contains(v.CompanyId) && v.VisitDate == day && customerNumbers.Contains(v.CustomerNumber))
        .ToListAsync();
    var visitByKey = visits.GroupBy(v => (v.CompanyId, v.CustomerNumber)).ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.Id).First());

    // All-time last actually-completed visit per customer (for "ימים מאז ביקור אחרון" on the card) -
    // separate from visitByKey above, which is scoped to just today's date.
    var lastVisitByKey = await db.CustomerVisits
        .Where(v => companyIds.Contains(v.CompanyId) && customerNumbers.Contains(v.CustomerNumber) && v.Outcome == VisitOutcome.Visited)
        .GroupBy(v => new { v.CompanyId, v.CustomerNumber })
        .Select(g => new { g.Key.CompanyId, g.Key.CustomerNumber, LastVisit = g.Max(v => v.VisitDate) })
        .ToDictionaryAsync(x => (x.CompanyId, x.CustomerNumber), x => x.LastVisit);

    // Distribution days for the specific (company, customer, plan year/month) each entry belongs to -
    // matches what the algorithm itself used when it placed that entry.
    var distByKey = (await db.CustomerDistributionDays
        .Where(d => companyIds.Contains(d.CompanyId) && customerNumbers.Contains(d.CustomerNumber))
        .ToListAsync())
        .ToDictionary(d => (d.CompanyId, d.CustomerNumber, d.Year, d.Month));

    var standardByKey = await db.CustomerVisitStandards
        .Where(s => companyIds.Contains(s.CompanyId) && customerNumbers.Contains(s.CustomerNumber))
        .ToDictionaryAsync(s => (s.CompanyId, s.CustomerNumber));

    var today = DateTime.Today;
    var result = matched.Select(e =>
    {
        visitByKey.TryGetValue((e.CompanyId, e.CustomerNumber), out var visit);
        lastVisitByKey.TryGetValue((e.CompanyId, e.CustomerNumber), out var lastVisit);
        int? daysSince = lastVisit == default ? null : (today - lastVisit).Days;
        distByKey.TryGetValue((e.CompanyId, e.CustomerNumber, e.PlanYear, e.PlanMonth), out var dist);
        standardByKey.TryGetValue((e.CompanyId, e.CustomerNumber), out var standard);
        return AgentVisitPlanEntryDto.From(e, customerByKey[(e.CompanyId, e.CustomerNumber)], visit,
            lastVisit == default ? null : lastVisit, daysSince, dist, standard?.RequiredVisitsPerWeek);
    }).OrderByDescending(x => x.PriorityScore).ToList(); // in-memory sort - SQLite can't ORDER BY decimal
    return Results.Ok(result);
});

// Same shape/enrichment as the single-day endpoint above, generalized to a date range (for the
// agent app's "השבוע"/"החודש" views) - the one real difference is that entries in a multi-day range
// can span MORE THAN ONE customer snapshot month, so each entry is matched against its own
// (PlanYear, PlanMonth), not one shared pair like the single-day version can assume.
app.MapGet("/api/agent/visitplan/range", async (string agentIdNumber, DateTime startDate, DateTime endDate, VisiFlowDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(agentIdNumber)) return Results.BadRequest("יש להזין תעודת זהות");
    var start = startDate.Date;
    var end = endDate.Date;

    var entries = await db.VisitPlanEntries.Where(e => e.PlannedDate != null && e.PlannedDate >= start && e.PlannedDate <= end).ToListAsync();
    if (entries.Count == 0) return Results.Ok(new List<AgentVisitPlanEntryDto>());

    var companyIds = entries.Select(e => e.CompanyId).Distinct().ToList();
    var customerNumbers = entries.Select(e => e.CustomerNumber).Distinct().ToList();

    var allCustomers = await db.Customers
        .Where(c => companyIds.Contains(c.CompanyId) && customerNumbers.Contains(c.CustomerNumber) && c.AgentIdNumber == agentIdNumber)
        .ToListAsync();
    var customerByKey = allCustomers.ToDictionary(c => (c.CompanyId, c.CustomerNumber, c.Year, c.Month));

    var matched = entries.Where(e => customerByKey.ContainsKey((e.CompanyId, e.CustomerNumber, e.PlanYear, e.PlanMonth))).ToList();
    if (matched.Count == 0) return Results.Ok(new List<AgentVisitPlanEntryDto>());

    var visits = await db.CustomerVisits.Include(v => v.NonVisitReason)
        .Where(v => companyIds.Contains(v.CompanyId) && v.VisitDate >= start && v.VisitDate <= end && customerNumbers.Contains(v.CustomerNumber))
        .ToListAsync();
    var visitByKey = visits.GroupBy(v => (v.CompanyId, v.CustomerNumber, v.VisitDate.Date))
        .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.Id).First());

    var lastVisitByKey = await db.CustomerVisits
        .Where(v => companyIds.Contains(v.CompanyId) && customerNumbers.Contains(v.CustomerNumber) && v.Outcome == VisitOutcome.Visited)
        .GroupBy(v => new { v.CompanyId, v.CustomerNumber })
        .Select(g => new { g.Key.CompanyId, g.Key.CustomerNumber, LastVisit = g.Max(v => v.VisitDate) })
        .ToDictionaryAsync(x => (x.CompanyId, x.CustomerNumber), x => x.LastVisit);

    var distByKey = (await db.CustomerDistributionDays
        .Where(d => companyIds.Contains(d.CompanyId) && customerNumbers.Contains(d.CustomerNumber))
        .ToListAsync())
        .ToDictionary(d => (d.CompanyId, d.CustomerNumber, d.Year, d.Month));

    var standardByKey = await db.CustomerVisitStandards
        .Where(s => companyIds.Contains(s.CompanyId) && customerNumbers.Contains(s.CustomerNumber))
        .ToDictionaryAsync(s => (s.CompanyId, s.CustomerNumber));

    var today = DateTime.Today;
    var result = matched.Select(e =>
    {
        visitByKey.TryGetValue((e.CompanyId, e.CustomerNumber, e.PlannedDate!.Value.Date), out var visit);
        lastVisitByKey.TryGetValue((e.CompanyId, e.CustomerNumber), out var lastVisit);
        int? daysSince = lastVisit == default ? null : (today - lastVisit).Days;
        distByKey.TryGetValue((e.CompanyId, e.CustomerNumber, e.PlanYear, e.PlanMonth), out var dist);
        standardByKey.TryGetValue((e.CompanyId, e.CustomerNumber), out var standard);
        return AgentVisitPlanEntryDto.From(e, customerByKey[(e.CompanyId, e.CustomerNumber, e.PlanYear, e.PlanMonth)], visit,
            lastVisit == default ? null : lastVisit, daysSince, dist, standard?.RequiredVisitsPerWeek);
    }).OrderBy(x => x.PlannedDate).ThenByDescending(x => x.PriorityScore).ToList();
    return Results.Ok(result);
});

// Lightweight name/number lookup across ALL of an agent's scheduled visits (any date, any month
// snapshot) - powers the agent app's "which days does this customer have an appointment" search,
// which is deliberately separate from the day/range endpoints above since it needs to search across
// dates rather than list entries within one.
app.MapGet("/api/agent/visitplan/search", async (string agentIdNumber, string query, VisiFlowDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(agentIdNumber) || string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
        return Results.Ok(new List<AgentSearchResultDto>());
    var q = query.Trim();

    // Every (company, customer number) this agent has ever been assigned whose name/number matches -
    // deduplicated across months, since the same real customer can appear in several monthly snapshots.
    var matches = await db.Customers
        .Where(c => c.AgentIdNumber == agentIdNumber && (c.CustomerName.Contains(q) || c.CustomerNumber.Contains(q)))
        .Select(c => new { c.CompanyId, c.CustomerNumber, c.CustomerName })
        .Distinct()
        .ToListAsync();
    if (matches.Count == 0) return Results.Ok(new List<AgentSearchResultDto>());

    var companyIds = matches.Select(m => m.CompanyId).Distinct().ToList();
    var customerNumbers = matches.Select(m => m.CustomerNumber).Distinct().ToList();
    var nameByKey = matches.ToDictionary(m => (m.CompanyId, m.CustomerNumber), m => m.CustomerName);

    var entries = await db.VisitPlanEntries
        .Where(e => companyIds.Contains(e.CompanyId) && customerNumbers.Contains(e.CustomerNumber) && e.PlannedDate != null)
        .ToListAsync();
    var results = entries
        .Where(e => nameByKey.ContainsKey((e.CompanyId, e.CustomerNumber)))
        .OrderBy(e => e.PlannedDate)
        .Select(e => new AgentSearchResultDto(e.CustomerNumber, nameByKey[(e.CompanyId, e.CustomerNumber)], e.PlannedDate!.Value))
        .ToList();
    return Results.Ok(results);
});

// Manually moves one plan entry to a new date - used both by an admin dragging/editing a row on the
// "תוכנית ביקורים" screen, and by an agent choosing to reschedule a visit they didn't make from the
// agent screen. Deliberately does NOT re-check agent daily capacity for the new date - this is an
// explicit human override of the algorithm's placement, not another automated scheduling pass.
// Records who moved it and why in ManuallyModifiedNote so both screens can show a "moved manually"
// indicator without the two callers duplicating that formatting logic themselves.
app.MapPost("/api/visitplan/entries/{id:int}/reschedule", async (int id, RescheduleVisitPlanEntryRequest req, VisiFlowDbContext db) =>
{
    var entry = await db.VisitPlanEntries.FindAsync(id);
    if (entry == null) return Results.NotFound("שורת תוכנית הביקורים לא נמצאה");

    var oldDate = entry.PlannedDate;
    var oldDateStr = oldDate?.ToString("dd/MM") ?? "ללא שיבוץ";
    var newDateStr = req.NewDate.ToString("dd/MM");
    var isAgent = req.Source == "Agent";
    var note = isAgent
        ? $"הוזז על ידי הסוכן{(string.IsNullOrWhiteSpace(req.ReasonText) ? "" : $" (לא ביקר - סיבה: {req.ReasonText})")} מ-{oldDateStr} ל-{newDateStr}"
        : $"הוזז ידנית על ידי מנהל מ-{oldDateStr} ל-{newDateStr}";

    VisitPlanEntry resultEntry;
    if (isAgent)
    {
        // The agent's reschedule keeps the ORIGINAL entry exactly where it is - it's the record of
        // what was planned (and not completed) on the original date, in both the agent and admin
        // screens. The new visit is a SEPARATE new entry added to the new date's plan, alongside
        // whatever else is already scheduled that day - not a replacement for the old one. An admin
        // dragging/editing a date directly (Source="Admin") still just moves the one row in place;
        // this branch only applies to the agent's "didn't visit, move it" flow.
        entry.ManuallyModifiedAt = DateTime.UtcNow;
        entry.ManuallyModifiedNote = note;

        resultEntry = new VisitPlanEntry
        {
            CompanyId = entry.CompanyId,
            PlanYear = req.NewDate.Year,
            PlanMonth = req.NewDate.Month,
            CustomerNumber = entry.CustomerNumber,
            PlannedDate = req.NewDate.Date,
            AgentName = entry.AgentName,
            PriorityScore = entry.PriorityScore,
            SalesDropScore = entry.SalesDropScore,
            DistributionScore = entry.DistributionScore,
            FrequencyScore = entry.FrequencyScore,
            VisitStandardScore = entry.VisitStandardScore,
            DaysSinceVisitScore = entry.DaysSinceVisitScore,
            DaysSinceLastVisit = entry.DaysSinceLastVisit,
            GeneratedAt = DateTime.UtcNow,
            ManuallyModifiedAt = DateTime.UtcNow,
            ManuallyModifiedNote = note
        };
        db.VisitPlanEntries.Add(resultEntry);

        // Tag the CustomerVisit that was just logged as "not visited" on the old date, so the
        // dashboard's non-visit-reasons breakdown can show it was a postponement, not a dead end.
        if (oldDate.HasValue)
        {
            var relatedVisit = await db.CustomerVisits
                .Where(v => v.CompanyId == entry.CompanyId && v.CustomerNumber == entry.CustomerNumber
                    && v.VisitDate == oldDate.Value.Date && v.Outcome == VisitOutcome.NotVisited)
                .OrderByDescending(v => v.Id)
                .FirstOrDefaultAsync();
            if (relatedVisit != null && (relatedVisit.Notes == null || !relatedVisit.Notes.Contains("נדחתה למועד חדש")))
            {
                var marker = $"הפגישה נדחתה למועד חדש: {req.NewDate:dd/MM/yyyy}";
                relatedVisit.Notes = string.IsNullOrWhiteSpace(relatedVisit.Notes) ? marker : $"{relatedVisit.Notes} | {marker}";
            }
        }
    }
    else
    {
        entry.ManuallyModifiedNote = note;
        entry.PlannedDate = req.NewDate.Date;
        entry.PlanYear = req.NewDate.Year;
        entry.PlanMonth = req.NewDate.Month;
        entry.ManuallyModifiedAt = DateTime.UtcNow;
        resultEntry = entry;
    }

    await db.SaveChangesAsync();

    // Matches resultEntry's OWN (year, month) - not necessarily the original entry's, since the agent
    // branch above returns a brand-new entry that may land in a different month's snapshot.
    var customer = await db.Customers.FirstOrDefaultAsync(c =>
        c.CompanyId == resultEntry.CompanyId && c.CustomerNumber == resultEntry.CustomerNumber && c.Year == resultEntry.PlanYear && c.Month == resultEntry.PlanMonth);
    var standard = await db.CustomerVisitStandards.FirstOrDefaultAsync(s => s.CompanyId == resultEntry.CompanyId && s.CustomerNumber == resultEntry.CustomerNumber);
    return Results.Ok(VisitPlanEntryDto.From(resultEntry, customer, standard?.RequiredVisitsPerWeek));
});

// Lets an admin attach a free-text note to one specific planned visit from the "תוכנית ביקורים"
// screen - shown on the agent's own copy of that visit (AgentVisitPlanEntryDto.AdminNote) so the
// admin can flag something the agent should know before that particular visit (e.g. "תבדוק מלאי
// לפני שמגיעים"). Deliberately NOT treated as a "manual edit" (ManuallyModifiedAt) - it doesn't move
// or otherwise change the plan, so it shouldn't exclude the row from re-optimization passes.
app.MapPost("/api/visitplan/entries/{id:int}/note", async (int id, SetVisitPlanEntryNoteRequest req, VisiFlowDbContext db) =>
{
    var entry = await db.VisitPlanEntries.FindAsync(id);
    if (entry == null) return Results.NotFound("שורת תוכנית הביקורים לא נמצאה");
    entry.AdminNote = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
    await db.SaveChangesAsync();

    var customer = await db.Customers.FirstOrDefaultAsync(c =>
        c.CompanyId == entry.CompanyId && c.CustomerNumber == entry.CustomerNumber && c.Year == entry.PlanYear && c.Month == entry.PlanMonth);
    var standard = await db.CustomerVisitStandards.FirstOrDefaultAsync(s => s.CompanyId == entry.CompanyId && s.CustomerNumber == entry.CustomerNumber);
    return Results.Ok(VisitPlanEntryDto.From(entry, customer, standard?.RequiredVisitsPerWeek));
}).RequireAuthorization();

// Row-level deletion from the "תוכנית ביקורים" screen - a single row, a bulk-selected set (via
// checkboxes, same pattern as "תקן ביקורים ללקוח"), or the whole month at once.
app.MapDelete("/api/visitplan/entries/{id:int}", async (int id, VisiFlowDbContext db) =>
{
    var entry = await db.VisitPlanEntries.FindAsync(id);
    if (entry == null) return Results.NotFound("שורת תוכנית הביקורים לא נמצאה");
    db.VisitPlanEntries.Remove(entry);
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization();

app.MapPost("/api/visitplan/entries/bulkdelete", async (BulkDeleteVisitPlanEntriesRequest req, VisiFlowDbContext db) =>
{
    if (req.Ids == null || req.Ids.Count == 0) return Results.BadRequest("יש לבחור לפחות שורה אחת למחיקה");
    var deleted = await db.VisitPlanEntries.Where(e => req.Ids.Contains(e.Id)).ExecuteDeleteAsync();
    return Results.Ok(new DeleteCustomersResultDto(deleted));
}).RequireAuthorization();

app.MapDelete("/api/visitplan", async (int companyId, int year, int month, VisiFlowDbContext db) =>
{
    if (await db.Companies.FindAsync(companyId) == null) return Results.BadRequest("החברה שנבחרה אינה קיימת");
    var deleted = await db.VisitPlanEntries.Where(e => e.CompanyId == companyId && e.PlanYear == year && e.PlanMonth == month).ExecuteDeleteAsync();
    return Results.Ok(new DeleteCustomersResultDto(deleted));
}).RequireAuthorization();

// Post-process pass over an already-generated month's plan - reorders visit dates (within each
// customer's own already-valid options) so an agent's same-city customers land on the same day.
// See VisitPlanCityOptimizer for the full algorithm and its constraints.
app.MapPost("/api/visitplan/optimizebycity", async (OptimizeByCityRequest req, VisiFlowDbContext db) =>
{
    if (await db.Companies.FindAsync(req.CompanyId) == null) return Results.BadRequest("החברה שנבחרה אינה קיימת");
    var hasEntries = await db.VisitPlanEntries.AnyAsync(e => e.CompanyId == req.CompanyId && e.PlanYear == req.Year && e.PlanMonth == req.Month);
    if (!hasEntries) return Results.BadRequest("לא קיימת תוכנית ביקורים לחודש זה - יש ליצור תוכנית לפני הרצת האופטימיזציה");
    var result = await VisitPlanCityOptimizer.OptimizeAsync(db, req.CompanyId, req.Year, req.Month);
    return Results.Ok(result);
}).RequireAuthorization();

app.Run();

record CompanyDto(int Id, string Name)
{
    public static CompanyDto From(Company c) => new(c.Id, c.Name);
}

record LoginRequest(string Username, string? Password);
record CreateUserRequest(int CompanyId, string Username, string DisplayName, string Password);
record ResetPasswordRequest(string Password);
record UserDto(int Id, int CompanyId, string Username, string DisplayName, DateTime CreatedAt)
{
    public static UserDto From(User u) => new(u.Id, u.CompanyId, u.Username, u.DisplayName, u.CreatedAt);
}

record CustomerDto(
    int Id, int CompanyId, int Year, int Month, string CustomerNumber, string CustomerName, string? AgentName, string? AgentIdNumber, string? Channel,
    decimal? SalesYtdCurrentYear, decimal? SalesYtdPreviousYear, string? CustomerSize, string? Phone,
    string? Address, string? City, decimal? AvgMonthlyOrders, bool WasActiveAllPeriod, string Status,
    decimal? RequiredVisitsPerWeek, DateTime UpdatedAt)
{
    public static CustomerDto From(Customer c, decimal? requiredVisitsPerWeek) => new(
        c.Id, c.CompanyId, c.Year, c.Month, c.CustomerNumber, c.CustomerName, c.AgentName, c.AgentIdNumber, c.Channel,
        c.SalesYtdCurrentYear, c.SalesYtdPreviousYear, c.CustomerSize, c.Phone,
        c.Address, c.City, c.AvgMonthlyOrders, c.WasActiveAllPeriod, c.Status.ToString(),
        requiredVisitsPerWeek, c.UpdatedAt);
}

record BulkVisitStandardRequest(int CompanyId, List<string> CustomerNumbers, decimal? RequiredVisitsPerWeek);

record CustomerImportResultDto(int Created, int Updated, List<string> Errors, List<string> UnmatchedOptionalColumns);

record DeleteCustomersResultDto(int Deleted);

record NonVisitReasonDto(int Id, int CompanyId, string Text, int SortOrder)
{
    public static NonVisitReasonDto From(NonVisitReason r) => new(r.Id, r.CompanyId, r.Text, r.SortOrder);
}

record CustomerDistributionDayDto(
    int Id, int CompanyId, string CustomerNumber, int Year, int Month,
    bool Sunday, bool Monday, bool Tuesday, bool Wednesday, bool Thursday, bool Friday, bool Saturday)
{
    public static CustomerDistributionDayDto From(CustomerDistributionDay d) => new(
        d.Id, d.CompanyId, d.CustomerNumber, d.Year, d.Month,
        d.Sunday, d.Monday, d.Tuesday, d.Wednesday, d.Thursday, d.Friday, d.Saturday);
}

record DistributionImportResultDto(int Created, int Updated, List<string> Errors);

record WorkCalendarDayDto(int Id, int CompanyId, DateTime Date, string DayType)
{
    public static WorkCalendarDayDto From(WorkCalendarDay d) => new(d.Id, d.CompanyId, d.Date, d.DayType.ToString());
}

record CustomerVisitDto(
    int Id, int CompanyId, string CustomerNumber, DateTime VisitDate, string? AgentName,
    string Outcome, int? NonVisitReasonId, string? NonVisitReasonText, string? Notes, DateTime CreatedAt,
    string? Channel, string? City)
{
    public static CustomerVisitDto From(CustomerVisit v, Customer? customer = null) => new(
        v.Id, v.CompanyId, v.CustomerNumber, v.VisitDate, v.AgentName,
        v.Outcome.ToString(), v.NonVisitReasonId, v.NonVisitReason?.Text, v.Notes, v.CreatedAt,
        customer?.Channel, customer?.City);
}

record CreateCustomerVisitRequest(
    int CompanyId, string CustomerNumber, DateTime VisitDate, string? AgentName,
    string Outcome, int? NonVisitReasonId, string? Notes);

record AgentVisitPlanEntryDto(
    int PlanEntryId, int CompanyId, string CustomerNumber, string CustomerName, string? Phone, string? Address,
    decimal? SalesYtdCurrentYear, decimal? SalesYtdPreviousYear, string? AgentName,
    DateTime PlannedDate, decimal PriorityScore, string? Outcome, string? ReasonText, int? VisitId,
    decimal? RequiredVisitsPerWeek, DateTime? LastVisitDate, int? DaysSinceLastVisit, string? AdminNote,
    bool DistSunday, bool DistMonday, bool DistTuesday, bool DistWednesday, bool DistThursday, bool DistFriday, bool DistSaturday, bool DistDefined)
{
    public static AgentVisitPlanEntryDto From(VisitPlanEntry e, Customer c, CustomerVisit? visit,
        DateTime? lastVisitDate, int? daysSinceLastVisit, CustomerDistributionDay? dist, decimal? requiredVisitsPerWeek) => new(
        e.Id, e.CompanyId, e.CustomerNumber, c.CustomerName, c.Phone, c.Address,
        c.SalesYtdCurrentYear, c.SalesYtdPreviousYear, c.AgentName,
        e.PlannedDate!.Value, e.PriorityScore, visit?.Outcome.ToString(), visit?.NonVisitReason?.Text, visit?.Id,
        requiredVisitsPerWeek, lastVisitDate, daysSinceLastVisit, e.AdminNote,
        dist?.Sunday ?? false, dist?.Monday ?? false, dist?.Tuesday ?? false, dist?.Wednesday ?? false,
        dist?.Thursday ?? false, dist?.Friday ?? false, dist?.Saturday ?? false, dist != null);
}

record AgentSearchResultDto(string CustomerNumber, string CustomerName, DateTime PlannedDate);

record VisitPlanWeightsDto(
    int CompanyId, decimal SalesDropWeight, decimal DistributionWeight, decimal FrequencyWeight,
    decimal VisitStandardWeight, decimal DaysSinceVisitWeight, int FullDayCapacity, int HalfDayCapacity)
{
    public static VisitPlanWeightsDto From(VisitPlanWeights w) => new(
        w.CompanyId, w.SalesDropWeight, w.DistributionWeight, w.FrequencyWeight, w.VisitStandardWeight, w.DaysSinceVisitWeight,
        w.FullDayCapacity, w.HalfDayCapacity);
}

record GenerateVisitPlanRequest(int CompanyId, int Year, int Month);

record OptimizeByCityRequest(int CompanyId, int Year, int Month);

record BulkDeleteVisitPlanEntriesRequest(List<int> Ids);
record BulkDeleteCustomerVisitsRequest(List<int> Ids);

record VisitPlanEntryDto(
    int Id, string CustomerNumber, string? CustomerName, string? AgentName, string? AgentIdNumber, string? Channel, string? Phone, string? Address, string? City,
    DateTime? PlannedDate, decimal PriorityScore,
    decimal SalesDropScore, decimal DistributionScore, decimal FrequencyScore, decimal VisitStandardScore, decimal DaysSinceVisitScore,
    decimal? SalesYtdCurrentYear, decimal? SalesYtdPreviousYear, decimal? AvgMonthlyOrders, decimal? RequiredVisitsPerWeek, int? DaysSinceLastVisit,
    DateTime? ManuallyModifiedAt, string? ManuallyModifiedNote, DateTime? CityOptimizedAt, string? CityOptimizedNote, string? AdminNote)
{
    public static VisitPlanEntryDto From(VisitPlanEntry e, Customer? c, decimal? requiredVisitsPerWeek) => new(
        e.Id, e.CustomerNumber, c?.CustomerName, e.AgentName, c?.AgentIdNumber, c?.Channel, c?.Phone, c?.Address, c?.City,
        e.PlannedDate, e.PriorityScore,
        e.SalesDropScore, e.DistributionScore, e.FrequencyScore, e.VisitStandardScore, e.DaysSinceVisitScore,
        c?.SalesYtdCurrentYear, c?.SalesYtdPreviousYear, c?.AvgMonthlyOrders, requiredVisitsPerWeek, e.DaysSinceLastVisit,
        e.ManuallyModifiedAt, e.ManuallyModifiedNote, e.CityOptimizedAt, e.CityOptimizedNote, e.AdminNote);
}

record RescheduleVisitPlanEntryRequest(DateTime NewDate, string Source, string? ReasonText);
record SetVisitPlanEntryNoteRequest(string? Note);

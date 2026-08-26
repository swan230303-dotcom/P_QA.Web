using System.Text.Json;
using PQA.Web.Data;
using PQA.Web.Models;
using PQA.Web.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Services.AddSingleton<SecureSettingsProvider>();
builder.Services.AddSingleton<SessionService>();
builder.Services.AddSingleton<QaFileService>();
builder.Services.AddScoped<WorkflowRepository>();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.WriteIndented = false;
});

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseExceptionHandler(handler => handler.Run(async context =>
{
    Exception? error = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    context.Response.StatusCode = error switch
    {
        KeyNotFoundException or FileNotFoundException => StatusCodes.Status404NotFound,
        UnauthorizedAccessException => StatusCodes.Status403Forbidden,
        ArgumentException or InvalidDataException or InvalidOperationException => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status500InternalServerError
    };
    await context.Response.WriteAsJsonAsync(new { error = error?.Message ?? "伺服器發生未預期錯誤。" });
}));

app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api") ||
        context.Request.Path.StartsWithSegments("/api/auth/login") ||
        context.Request.Path.StartsWithSegments("/api/health"))
    {
        await next(); return;
    }
    string? token = context.Request.Headers.Authorization.FirstOrDefault()?.Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase).Trim();
    if (string.IsNullOrWhiteSpace(token)) token = context.Request.Cookies["pqa_session"];
    UserSession? user = context.RequestServices.GetRequiredService<SessionService>().Validate(token);
    if (user is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "登入已逾時，請重新登入。" }); return;
    }
    context.Items["user"] = user; context.Items["token"] = token;
    await next();
});

app.MapGet("/api/health", async (WorkflowRepository repository, CancellationToken ct) =>
    Results.Ok(new { status = await repository.CheckAsync(ct) ? "ok" : "error", time = DateTimeOffset.Now }));

app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext context, WorkflowRepository repository, SessionService sessions, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Password)) return Results.BadRequest(new { error = "請輸入密碼。" });
    UserSession? user = await repository.AuthenticateAsync(request.Password, ct);
    if (user is null) return Results.Unauthorized();
    string token = sessions.Create(user);
    context.Response.Cookies.Append("pqa_session", token, new CookieOptions
    {
        HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = context.Request.IsHttps, MaxAge = TimeSpan.FromHours(8)
    });
    return Results.Ok(new LoginResponse(token, user));
});

app.MapPost("/api/auth/logout", (HttpContext context, SessionService sessions) =>
{
    sessions.Remove(context.Items["token"] as string);
    context.Response.Cookies.Delete("pqa_session");
    return Results.Ok();
});

app.MapGet("/api/modules", () => Results.Ok(WorkflowDefinitions.All.Select(x => new
{
    x.Id, x.Name, x.Description, x.Accent, x.Fields, x.SearchFields, x.DetailFields, x.IsLineBased,
    SupportsAttachments = x.AttachmentTable is not null,
    SupportsHolidays = x.Id == "complaint"
})));

app.MapGet("/api/lookups/{type}", async (string type, WorkflowRepository repository, CancellationToken ct) =>
    Results.Ok(await repository.LookupAsync(type, ct)));

app.MapGet("/api/workflows/{moduleId}", async (string moduleId, string? query, bool unfinishedOnly, WorkflowRepository repository, CancellationToken ct) =>
    Results.Ok(await repository.SearchAsync(WorkflowDefinitions.Get(moduleId), query, unfinishedOnly, ct)));

app.MapGet("/api/workflows/{moduleId}/{caseNumber}", async (string moduleId, string caseNumber, WorkflowRepository repository, CancellationToken ct) =>
{
    WorkflowDocument? document = await repository.LoadAsync(WorkflowDefinitions.Get(moduleId), caseNumber, ct);
    return document is null ? Results.NotFound(new { error = "找不到案件資料。" }) : Results.Ok(document);
});

app.MapPut("/api/workflows/{moduleId}/{caseNumber}", async (string moduleId, string caseNumber, WorkflowDocument document, WorkflowRepository repository, CancellationToken ct) =>
{
    document.CaseNumber = caseNumber;
    await repository.SaveAsync(WorkflowDefinitions.Get(moduleId), document, ct);
    return Results.Ok(document);
});

app.MapDelete("/api/workflows/{moduleId}/{caseNumber}", async (string moduleId, string caseNumber, WorkflowRepository repository, CancellationToken ct) =>
{
    await repository.DeleteAsync(WorkflowDefinitions.Get(moduleId), caseNumber, ct);
    return Results.NoContent();
});

app.MapPost("/api/workflows/{moduleId}/{caseNumber}/attachments", async (string moduleId, string caseNumber, IFormFile file, WorkflowRepository repository, QaFileService files, CancellationToken ct) =>
{
    WorkflowDefinition definition = WorkflowDefinitions.Get(moduleId);
    if (await repository.LoadAsync(definition, caseNumber, ct) is null) return Results.BadRequest(new { error = "請先儲存案件後再上傳附件。" });
    string fileName = await files.SaveAsync(definition, file, ct);
    try { return Results.Ok(await repository.AddAttachmentAsync(definition, caseNumber, fileName, ct)); }
    catch { try { files.Delete(definition, fileName); } catch { } throw; }
}).DisableAntiforgery();

app.MapGet("/api/workflows/{moduleId}/{caseNumber}/attachments/{sequence:int}", async (string moduleId, string caseNumber, int sequence, WorkflowRepository repository, QaFileService files, CancellationToken ct) =>
{
    WorkflowDefinition definition = WorkflowDefinitions.Get(moduleId);
    WorkflowDocument document = await repository.LoadAsync(definition, caseNumber, ct) ?? throw new KeyNotFoundException("找不到案件資料。");
    AttachmentInfo attachment = document.Attachments.FirstOrDefault(x => x.Sequence == sequence) ?? throw new KeyNotFoundException("找不到附件資料。");
    string path = files.GetPath(definition, attachment.FileName);
    return Results.File(path, files.ContentType(path), attachment.FileName, enableRangeProcessing: true);
});

app.MapGet("/api/workflows/{moduleId}/{caseNumber}/attachments/{sequence:int}/preview", async (string moduleId, string caseNumber, int sequence, HttpContext context, WorkflowRepository repository, QaFileService files, CancellationToken ct) =>
{
    WorkflowDefinition definition = WorkflowDefinitions.Get(moduleId);
    WorkflowDocument document = await repository.LoadAsync(definition, caseNumber, ct) ?? throw new KeyNotFoundException("找不到案件資料。");
    AttachmentInfo attachment = document.Attachments.FirstOrDefault(x => x.Sequence == sequence) ?? throw new KeyNotFoundException("找不到附件資料。");
    string path = files.GetPath(definition, attachment.FileName);
    if (!files.IsPreviewableImage(path)) return Results.BadRequest(new { error = "此附件不是可線上預覽的圖片格式。" });
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.ContentDisposition = $"inline; filename*=UTF-8''{Uri.EscapeDataString(attachment.FileName)}";
    return Results.File(path, files.ContentType(path), enableRangeProcessing: true);
});

app.MapDelete("/api/workflows/{moduleId}/{caseNumber}/attachments/{sequence:int}", async (string moduleId, string caseNumber, int sequence, WorkflowRepository repository, QaFileService files, CancellationToken ct) =>
{
    WorkflowDefinition definition = WorkflowDefinitions.Get(moduleId);
    AttachmentInfo attachment = await repository.RemoveAttachmentAsync(definition, caseNumber, sequence, ct);
    try { files.Delete(definition, attachment.FileName); } catch (FileNotFoundException) { }
    return Results.NoContent();
});

app.MapGet("/api/holidays", async (WorkflowRepository repository, CancellationToken ct) => Results.Ok(await repository.GetHolidaysAsync(ct)));
app.MapPost("/api/holidays/{date}", async (DateTime date, WorkflowRepository repository, CancellationToken ct) => { await repository.AddHolidayAsync(date, ct); return Results.Ok(); });
app.MapDelete("/api/holidays/{date}", async (DateTime date, WorkflowRepository repository, CancellationToken ct) => { await repository.DeleteHolidayAsync(date, ct); return Results.NoContent(); });

app.MapFallbackToFile("index.html");
app.Run();

public partial class Program { }

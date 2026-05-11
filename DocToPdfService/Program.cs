using System.Text;
using DocToPdfService.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddSingleton<IDocumentConverter, LibreOfficeConverter>();

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 50 * 1024 * 1024; // 50 MB
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/health"))
    {
        await next();
        return;
    }

    var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
    if (authHeader == null || !authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = 401;
        context.Response.Headers.Append("WWW-Authenticate", "Basic realm=\"DocToPdfService\"");
        return;
    }

    string decoded;
    try
    {
        decoded = Encoding.ASCII.GetString(Convert.FromBase64String(authHeader["Basic ".Length..].Trim()));
    }
    catch
    {
        context.Response.StatusCode = 401;
        context.Response.Headers.Append("WWW-Authenticate", "Basic realm=\"DocToPdfService\"");
        return;
    }

    var sep = decoded.IndexOf(':');
    if (sep < 0)
    {
        context.Response.StatusCode = 401;
        context.Response.Headers.Append("WWW-Authenticate", "Basic realm=\"DocToPdfService\"");
        return;
    }

    var user = decoded[..sep];
    var pass = decoded[(sep + 1)..];
    var expectedUser = app.Configuration["BasicAuth:Username"];
    var expectedPass = app.Configuration["BasicAuth:Password"];

    if (user != expectedUser || pass != expectedPass)
    {
        context.Response.StatusCode = 401;
        context.Response.Headers.Append("WWW-Authenticate", "Basic realm=\"DocToPdfService\"");
        return;
    }

    await next();
});

app.MapControllers();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }))
   .WithName("HealthCheck");

app.Run();

using System.Reflection;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();

var app = builder.Build();
var serviceName = Assembly.GetExecutingAssembly().GetName().Name ?? "SmartMetrix.Service";

app.MapGet("/", () => Results.Redirect("/info"));
app.MapGet("/info", () => Results.Ok(new
{
    service = serviceName,
    version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
    environment = app.Environment.EnvironmentName,
    utcNow = DateTimeOffset.UtcNow
}));
app.MapHealthChecks("/health");
app.MapHealthChecks("/ready");

app.Run();

public partial class Program;

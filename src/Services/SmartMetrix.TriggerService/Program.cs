using SmartMetrix.TriggerService;
using SmartMetrix.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddSmartMetrixServiceDefaults();
builder.Services.AddTriggerService(builder.Configuration);

var app = builder.Build();
app.UseSmartMetrixServiceDefaults();
app.MapSmartMetrixDefaultEndpoints();

app.MapPost("/v1/trigger/evaluate", async (
    TriggerSnapshot snapshot,
    TriggerCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    var result = await coordinator.EvaluateAsync(snapshot, cancellationToken);
    return result.Decision.Accepted ? Results.Accepted(value: result) : Results.Ok(result);
});

app.Run();

public partial class Program;

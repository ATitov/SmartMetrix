using SmartMetrix.ServiceDefaults;
using SmartMetrix.GeoreferenceService;

var builder = WebApplication.CreateBuilder(args);
builder.AddSmartMetrixServiceDefaults();
builder.Services.AddSingleton<Georeferencer>();

var app = builder.Build();
app.UseSmartMetrixServiceDefaults();
app.MapSmartMetrixDefaultEndpoints();
app.MapPost("/v1/measurements/{measurementId:guid}/georeference", (Guid measurementId,
    GeoreferenceRequest request, Georeferencer georeferencer) =>
    Results.Ok(georeferencer.Georeference(measurementId, request)));

app.Run();

public partial class Program;

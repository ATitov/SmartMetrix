using SmartMetrix.TriggerService;
using SmartMetrix.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddSmartMetrixServiceDefaults();
builder.Services.AddTriggerService(builder.Configuration);

var app = builder.Build();
app.UseSmartMetrixServiceDefaults();
app.MapSmartMetrixDefaultEndpoints();

app.MapTriggerEndpoints();

app.Run();

public partial class Program;

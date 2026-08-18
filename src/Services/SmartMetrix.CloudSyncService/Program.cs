using SmartMetrix.ServiceDefaults;
using SmartMetrix.CloudSyncService;

var builder = WebApplication.CreateBuilder(args);
builder.AddSmartMetrixServiceDefaults();
builder.Services.AddCloudSync(builder.Configuration);

var app = builder.Build();
app.UseSmartMetrixServiceDefaults();
app.MapSmartMetrixDefaultEndpoints();
app.MapCloudSyncEndpoints();

app.Run();

public partial class Program;

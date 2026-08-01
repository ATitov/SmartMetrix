using SmartMetrix.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddSmartMetrixServiceDefaults();

var app = builder.Build();
app.UseSmartMetrixServiceDefaults();
app.MapSmartMetrixDefaultEndpoints();

app.Run();

public partial class Program;

using TmSpline.Server;

var builder = WebApplication.CreateBuilder(args);
ServerApp.AddServices(builder);
var app = builder.Build();
ServerApp.Map(app);
app.Run();

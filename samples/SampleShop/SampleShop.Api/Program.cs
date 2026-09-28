using SampleShop.Api.Clients;
using SampleShop.Api.Endpoints;
using SampleShop.Api.Services;
using SampleShop.Data;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<ShopDbContext>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddHttpClient<PricingClient>(c => c.BaseAddress = new Uri("https://pricing.example.com"));

var app = builder.Build();
app.MapControllers();
app.MapCatalog();
app.Run();

using SampleShop.Data;
using SampleShop.Data.Entities;

namespace SampleShop.Api.Endpoints;

public static class CatalogEndpoints
{
    public static void MapCatalog(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("api/catalog");
        group.MapGet("/items", GetItems);
        group.MapPost("/items", async (ShopDbContext db, Product product) =>
        {
            db.Products.Add(product);
            await db.SaveChangesAsync();
        });
    }

    public static List<Product> GetItems(ShopDbContext db)
    {
        return db.Products.ToList();
    }
}

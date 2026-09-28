using Microsoft.EntityFrameworkCore;
using SampleShop.Data.Entities;

namespace SampleShop.Data;

public class ShopDbContext : DbContext
{
    public DbSet<Order> Orders { get; set; } = null!;
    public DbSet<Product> Products { get; set; } = null!;
}

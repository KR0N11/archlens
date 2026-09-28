using SampleShop.Api.Clients;
using SampleShop.Api.Contracts;
using SampleShop.Data;
using SampleShop.Data.Entities;

namespace SampleShop.Api.Services;

public class OrderService : IOrderService
{
    private readonly ShopDbContext _db;
    private readonly PricingClient _pricing;

    public OrderService(ShopDbContext db, PricingClient pricing)
    {
        _db = db;
        _pricing = pricing;
    }

    public async Task<List<OrderDto>> GetOpenOrders()
    {
        var orders = _db.Orders.Where(o => o.IsOpen).ToList();
        return orders.Select(ToDto).ToList();
    }

    public async Task<OrderDto> PlaceOrder(CreateOrderRequest request)
    {
        var quote = await _pricing.GetQuote(request.ProductId);
        var order = new Order { CustomerName = request.CustomerName, IsOpen = true };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();
        return ToDto(order);
    }

    // Changes the entity by setting a property; EF saves it without any Update() call.
    public void CloseOrder(Order order)
    {
        order.IsOpen = false;
        _db.SaveChanges();
    }

    private static OrderDto ToDto(Order order)
    {
        return new OrderDto { Id = order.Id, CustomerName = order.CustomerName };
    }

    private static void NeverCalled()
    {
    }
}

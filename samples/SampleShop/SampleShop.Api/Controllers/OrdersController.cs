using Microsoft.AspNetCore.Mvc;
using SampleShop.Api.Contracts;
using SampleShop.Api.Services;
using SampleShop.Data;

namespace SampleShop.Api.Controllers;

[ApiController]
[Route("api/orders")]
public class OrdersController : ControllerBase
{
    private readonly IOrderService _orders;
    private readonly ShopDbContext _db;

    public OrdersController(IOrderService orders, ShopDbContext db)
    {
        _orders = orders;
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<List<OrderDto>>> Get()
    {
        return await _orders.GetOpenOrders();
    }

    [HttpPost]
    public async Task<ActionResult<OrderDto>> Create(CreateOrderRequest request)
    {
        return await _orders.PlaceOrder(request);
    }

    // Reads the table straight from the controller: the score should flag this.
    [HttpGet("count")]
    public int Count()
    {
        return _db.Orders.Count();
    }
}

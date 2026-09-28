using SampleShop.Api.Contracts;

namespace SampleShop.Api.Services;

// A second implementation that is NOT registered in DI.
public class LegacyOrderService : IOrderService
{
    public Task<List<OrderDto>> GetOpenOrders()
    {
        return Task.FromResult(new List<OrderDto>());
    }

    public Task<OrderDto> PlaceOrder(CreateOrderRequest request)
    {
        return Task.FromResult(new OrderDto());
    }
}

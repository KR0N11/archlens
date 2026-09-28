using SampleShop.Api.Contracts;

namespace SampleShop.Api.Services;

public interface IOrderService
{
    Task<List<OrderDto>> GetOpenOrders();
    Task<OrderDto> PlaceOrder(CreateOrderRequest request);
}

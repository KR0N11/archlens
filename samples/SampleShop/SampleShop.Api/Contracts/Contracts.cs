namespace SampleShop.Api.Contracts;

public class OrderDto
{
    public int Id { get; set; }
    public string CustomerName { get; set; } = "";
}

public class CreateOrderRequest
{
    public int ProductId { get; set; }
    public string CustomerName { get; set; } = "";
}

public record PriceQuote(int ProductId, decimal Price);

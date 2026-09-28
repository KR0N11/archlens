namespace SampleShop.Data.Entities;

public class OrderLine
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public int Quantity { get; set; }
}

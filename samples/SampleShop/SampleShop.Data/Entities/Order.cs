namespace SampleShop.Data.Entities;

public class Order
{
    public int Id { get; set; }
    public string CustomerName { get; set; } = "";
    public bool IsOpen { get; set; }
    public List<OrderLine> Lines { get; set; } = new();
}

namespace SampleShop.Data;

// Mostly properties but nothing stores or sends it: lands in the "ask the LLM" band.
public class ShippingOptions
{
    public string Carrier { get; set; } = "";
    public int MaxDays { get; set; }
}

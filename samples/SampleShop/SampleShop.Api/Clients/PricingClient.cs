using System.Net.Http.Json;
using SampleShop.Api.Contracts;

namespace SampleShop.Api.Clients;

public class PricingClient
{
    private readonly HttpClient _http;

    public PricingClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<PriceQuote?> GetQuote(int productId)
    {
        return await _http.GetFromJsonAsync<PriceQuote>($"/quotes/{productId}");
    }
}

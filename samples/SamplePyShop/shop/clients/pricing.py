import httpx

from shop.schemas import PriceQuote


class PricingClient:
    def __init__(self) -> None:
        self.http = httpx.Client(base_url="https://pricing.example.com")

    def get_quote(self, product_id: int) -> PriceQuote:
        response = self.http.get(f"/quotes/{product_id}")
        return PriceQuote.model_validate(response.json())

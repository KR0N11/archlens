from pydantic import BaseModel


class OrderOut(BaseModel):
    id: int
    status: str
    total: float


class CreateOrderRequest(BaseModel):
    product_id: int
    quantity: int


class ProductIn(BaseModel):
    name: str
    price: float


class PriceQuote(BaseModel):
    product_id: int
    unit_price: float

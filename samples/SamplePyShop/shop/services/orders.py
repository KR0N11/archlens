from sqlalchemy import select
from sqlalchemy.orm import Session

from shop.clients.pricing import PricingClient
from shop.models import Order
from shop.schemas import CreateOrderRequest, OrderOut
from shop.services.payments import PaymentGateway


class OrderService:
    def __init__(self, session: Session, pricing: PricingClient) -> None:
        self.session = session
        self.pricing = pricing

    def open_orders(self) -> list[OrderOut]:
        rows = self.session.scalars(select(Order).where(Order.status == "open"))
        return [self._to_out(r) for r in rows]

    def place_order(self, request: CreateOrderRequest) -> OrderOut:
        quote = self.pricing.get_quote(request.product_id)
        order = Order(status="open", total=quote.unit_price * request.quantity)
        self.session.add(order)
        self.session.commit()
        return self._to_out(order)

    def close_order(self, order_id: int) -> None:
        order = self.session.get(Order, order_id)
        order.close()

    def checkout(self, order_id: int, gateway: PaymentGateway) -> str:
        return gateway.charge(1.0)

    def _to_out(self, order: Order) -> OrderOut:
        return OrderOut(id=order.id, status=order.status, total=order.total)

    def _never_called(self) -> None:
        pass

from fastapi import Depends
from sqlalchemy.orm import Session

from shop.clients.pricing import PricingClient
from shop.db import get_session
from shop.services.orders import OrderService


def get_order_service(session: Session = Depends(get_session)) -> OrderService:
    return OrderService(session, PricingClient())

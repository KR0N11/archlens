from typing import Annotated

from fastapi import APIRouter, Depends
from sqlalchemy import func, select
from sqlalchemy.orm import Session

from shop.db import get_session
from shop.deps import get_order_service
from shop.models import Order
from shop.schemas import CreateOrderRequest, OrderOut
from shop.services.orders import OrderService

router = APIRouter(prefix="/orders")


@router.get("", response_model=list[OrderOut])
def list_orders(service: OrderService = Depends(get_order_service)):
    return service.open_orders()


@router.post("")
def create_order(request: CreateOrderRequest,
                 service: Annotated[OrderService, Depends(get_order_service)]) -> OrderOut:
    return service.place_order(request)


@router.get("/count")
def count_orders(session: Session = Depends(get_session)) -> int:
    return session.scalar(select(func.count()).select_from(Order))

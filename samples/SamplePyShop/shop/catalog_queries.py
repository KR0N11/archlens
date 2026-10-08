from sqlalchemy import select
from sqlalchemy.orm import Session

from shop.models import Product


def list_products(session: Session) -> list[Product]:
    return list(session.scalars(select(Product)))

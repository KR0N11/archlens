from fastapi import APIRouter, Depends
from sqlalchemy.orm import Session

from shop import catalog_queries
from shop.db import get_session
from shop.models import Product
from shop.schemas import ProductIn

router = APIRouter(prefix="/api/catalog")


@router.get("/items")
def get_items(session: Session = Depends(get_session)):
    return catalog_queries.list_products(session)


@router.post("/items")
def add_item(item: ProductIn, session: Session = Depends(get_session)):
    product = Product(name=item.name, price=item.price)
    session.add(product)
    session.commit()
    return product.id


@router.api_route("/items/{item_id}", methods=["GET", "DELETE"])
def item(item_id: int):
    return item_id

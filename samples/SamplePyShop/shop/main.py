from fastapi import FastAPI

from shop.routes import catalog, orders

app = FastAPI()
app.include_router(orders.router, prefix="/api")
app.include_router(catalog.router)

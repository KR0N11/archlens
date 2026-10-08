from alembic import op


def upgrade():
    op.create_table("orders")
    op.create_table("order_lines")
    op.create_table("products")

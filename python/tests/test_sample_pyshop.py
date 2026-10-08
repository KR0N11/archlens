"""End-to-end checks against the hand-written SamplePyShop fixture (samples/SamplePyShop).

Like the C# SampleShop tests, every expected value can be worked out by reading the fixture's source;
nothing is copied from a snapshot. Comparing whole sets catches a missing arrow and an extra arrow alike.
"""
import json
import subprocess
import sys
from pathlib import Path

import pytest

from archlens_py import analyze

ROOT = Path(__file__).resolve().parents[2] / "samples" / "SamplePyShop"


@pytest.fixture(scope="module")
def graph():
    return analyze(str(ROOT))


def short(i: str) -> str:
    prefix, _, rest = i.partition(":")
    if prefix in ("api", "ext", "cmp"):
        return i
    parts = rest.split(".")
    return f"{prefix}:" + (".".join(parts[-2:]) if prefix == "fn" else parts[-1])


def edges(g) -> set[str]:
    return {f"{r['type']}{'/' + r['access'] if r.get('access') else ''} {short(r['from'])} -> {short(r['to'])}"
            for r in g["relationships"]}


def get(g, i: str) -> dict:
    return next(e for e in g["elements"] if e["id"] == i)


def test_finds_exactly_the_expected_arrows(graph):
    assert edges(graph) == {
        # Routes: Depends(...) providers run first, then the body's calls.
        "Serving api:GET api/orders -> fn:deps.get_order_service",
        "Serving api:GET api/orders -> fn:OrderService.open_orders",
        "Serving api:POST api/orders -> fn:deps.get_order_service",
        "Serving api:POST api/orders -> fn:OrderService.place_order",
        "Serving api:GET api/orders/count -> fn:db.get_session",
        "Access/Read api:GET api/orders/count -> do:Order",
        "Serving api:GET api/catalog/items -> fn:db.get_session",
        "Serving api:GET api/catalog/items -> fn:catalog_queries.list_products",
        "Serving api:POST api/catalog/items -> fn:db.get_session",
        "Access/Write api:POST api/catalog/items -> do:Product",
        "Serving fn:deps.get_order_service -> fn:db.get_session",
        "Access/Read fn:catalog_queries.list_products -> do:Product",
        # OrderService: self.session / self.pricing types come from __init__'s annotations.
        "Access/Read fn:OrderService.open_orders -> do:Order",
        "Serving fn:OrderService.open_orders -> fn:OrderService._to_out",
        "Serving fn:OrderService.place_order -> fn:PricingClient.get_quote",
        "Access/Write fn:OrderService.place_order -> do:Order",
        "Serving fn:OrderService.place_order -> fn:OrderService._to_out",
        "Access/Read fn:OrderService.close_order -> do:Order",
        "Access/Write fn:OrderService.close_order -> do:Order",
        # An abstract method with two implementations: both drawn, each at confidence 0.5.
        "Serving fn:OrderService.checkout -> fn:StripeGateway.charge",
        "Serving fn:OrderService.checkout -> fn:FakeGateway.charge",
        "Flow fn:StripeGateway.charge -> ext:api.stripe.com",
        "Flow fn:PricingClient.get_quote -> ext:pricing.example.com",
        "Access/Read fn:PricingClient.get_quote -> do:PriceQuote",
        "Serving fn:tick.hit -> fn:tock.hit",
        "Serving fn:tock.hit -> fn:tick.hit",
        "Aggregation do:Order -> do:OrderLine",
    }


def test_every_arrow_has_evidence_that_exists(graph):
    for r in graph["relationships"]:
        path = ROOT / r["evidence"]["file"]
        assert path.exists(), r
        assert 1 <= r["evidence"]["line"] <= len(path.read_text().splitlines()), r


def test_finds_every_route_with_its_full_path(graph):
    # include_router(prefix="/api") + APIRouter(prefix="/orders") + "" -> "api/orders".
    routes = sorted(e["route"] for e in graph["elements"] if "route" in e)
    assert routes == ["GET api/catalog/items", "GET api/orders", "GET api/orders/count",
                      "GET,DELETE api/catalog/items/{item_id}", "POST api/catalog/items", "POST api/orders"]


def test_unsure_calls_through_an_abstract_method(graph):
    calls = [r for r in graph["relationships"] if r["from"].endswith("OrderService.checkout")]
    assert sorted(r["confidence"] for r in calls) == [0.5, 0.5]


@pytest.mark.parametrize("id_, score, kind, confidence", [
    # ORM mapping +5, table in the migration +5, mostly fields +2.
    ("do:shop.models.Order", 12, "Stored", 1.0),
    ("do:shop.models.OrderLine", 12, "Stored", 1.0),
    ("do:shop.models.Product", 12, "Stored", 1.0),
    # Pydantic +2, mostly fields +2: unsure, waiting for the LLM step, like the C# DTOs.
    ("do:shop.schemas.OrderOut", 4, "Dto", 0.5),
    ("do:shop.schemas.CreateOrderRequest", 4, "Dto", 0.5),
    # Parsed from an HTTP response in the same function: data that comes from outside.
    ("do:shop.schemas.PriceQuote", 4, "External", 0.5),
    # Only "mostly fields": unsure with no kind.
    ("do:shop.models.ShippingOptions", 2, None, 0.5),
])
def test_scores_entities(graph, id_, score, kind, confidence):
    e = get(graph, id_)
    assert e["type"] == "DataObject"
    assert e["entityScore"] == score
    assert e.get("entityKind") == kind
    assert e["confidence"] == confidence


@pytest.mark.parametrize("id_, type_, confidence", [
    # Returned by a Depends provider: the recognized shape of an injected service.
    ("cls:shop.services.orders.OrderService", "ApplicationService", 0.9),
    # Implements one of the repo's own base classes.
    ("cls:shop.services.payments.StripeGateway", "ApplicationService", 0.9),
    # Neither: unsure.
    ("cls:shop.clients.pricing.PricingClient", "ApplicationService", 0.5),
    # A module with routes is the controller; a module with functions is a service.
    ("mod:shop.routes.orders", "ApplicationInterface", 1.0),
    ("mod:shop.tick", "ApplicationService", 0.9),
])
def test_classifies_classes_and_modules(graph, id_, type_, confidence):
    e = get(graph, id_)
    assert (e["type"], e["confidence"]) == (type_, confidence)


def test_abstract_bases_exceptions_and_declarative_base_get_no_box(graph):
    ids = {e["id"] for e in graph["elements"]}
    assert not {"cls:shop.services.payments.PaymentGateway", "cls:shop.db.Base", "do:shop.db.Base"} & ids


def test_private_functions_are_marked_for_the_orphan_check(graph):
    assert get(graph, "fn:shop.services.orders.OrderService._never_called").get("isPrivate") is True
    assert "isPrivate" not in get(graph, "fn:shop.services.orders.OrderService.place_order")


def test_skips_tests_and_migrations_by_default(graph):
    files = {e["source"]["file"] for e in graph["elements"]}
    assert not any(f.startswith(("tests/", "migrations/")) for f in files)
    with_tests = analyze(str(ROOT), include_tests=True)
    assert any(e["id"] == "fn:test_orders.test_hit" for e in with_tests["elements"])


def test_same_input_gives_the_same_graph(graph):
    assert json.dumps(analyze(str(ROOT)), sort_keys=True) == json.dumps(graph, sort_keys=True)


def test_command_line_prints_json():
    out = subprocess.run([sys.executable, "-m", "archlens_py", str(ROOT)], capture_output=True, text=True,
                         cwd=Path(__file__).resolve().parents[1], check=True).stdout
    assert {"elements", "relationships"} <= json.loads(out).keys()

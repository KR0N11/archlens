"""Turns the index into boxes and arrows.

Two passes over every function body. The first pass only learns what `self.x` holds. The second pass
records events ("calls OrderService.place_order", "reads Order", "HTTP to pricing.example.com"),
each with its file and line. Classes are classified from those events, and only then do events
become arrows, because whether `order.close()` is a call or a write depends on what Order turned out to be.
"""
from __future__ import annotations

import ast
from urllib.parse import urlparse

from .analyzer import (
    AMBIGUOUS_THRESHOLD, COLLECTIONS, DATA_SUFFIXES, ENTITY_THRESHOLD, HTTP_CLIENT_TYPES, HTTP_MODULES,
    HTTP_VERBS, INSTANCE_WRITES, LOGIC_SUFFIXES, MANAGER_WRITES, PARSE_METHODS, REPOSITORY_MODULES,
    SESSION_WRITES, TABLE_READ_FUNCS, TABLE_WRITE_FUNCS, UNSURE, Analyzer, ClassInfo, Module,
    _dunder, _is_abstract, _is_property, _matches_table, _returns_value,
)

SESSION_TYPES = {"Session", "AsyncSession", "scoped_session"}
TABLE_JOIN_FUNCS = {"select_from", "join", "outerjoin"}


# ---------------------------------------------------------------- types

class Types:
    """Answers "what is this expression?" with a small marker:
    ("inst", key, host)  an instance of repo class `key`, or key "http" (a client, host = its base_url
                         host if known) or "session" (a database session)
    ("cls", key)         the class object itself (Order in select(Order))
    """

    def __init__(self, a: Analyzer):
        self.a = a

    def ann(self, module: Module, ann: ast.AST | None, depth: int = 0):
        if ann is None or depth > 6:
            return None
        if isinstance(ann, ast.Constant) and isinstance(ann.value, str):
            try:
                return self.ann(module, ast.parse(ann.value, mode="eval").body, depth + 1)
            except SyntaxError:
                return None
        if isinstance(ann, ast.Name) and ann.id in module.aliases:
            return self.ann(module, module.aliases[ann.id], depth + 1)
        if isinstance(ann, (ast.Name, ast.Attribute)):
            alias = self._imported_alias(module, ann)
            if alias is not None:
                return self.ann(alias[0], alias[1], depth + 1)
            key = self.a.resolve_class(module, ann)
            if key:
                return ("inst", key, None)
            canon = self.a.canonical(module, ann)
            last, top = canon.split(".")[-1], canon.split(".")[0]
            if top in HTTP_MODULES and last in HTTP_CLIENT_TYPES or canon.endswith("aiohttp.ClientSession"):
                return ("inst", "http", None)
            if last in SESSION_TYPES and top in ("sqlalchemy", "sqlmodel"):
                return ("inst", "session", None)
            return None
        if isinstance(ann, ast.Subscript):
            base = self.a.canonical(module, ann.value).split(".")[-1]
            args = ann.slice.elts if isinstance(ann.slice, ast.Tuple) else [ann.slice]
            if base in ("Optional", "Annotated", "Mapped"):
                return self.ann(module, args[0], depth + 1)
            if base == "Union":
                for arg in args:
                    if not (isinstance(arg, ast.Constant) and arg.value is None):
                        return self.ann(module, arg, depth + 1)
            return None
        if isinstance(ann, ast.BinOp) and isinstance(ann.op, ast.BitOr):
            for side in (ann.left, ann.right):
                if not (isinstance(side, ast.Constant) and side.value is None):
                    return self.ann(module, side, depth + 1)
        return None

    def _imported_alias(self, module: Module, expr: ast.AST):
        """SessionDep imported from app.api.deps: expand it in the module that defines it."""
        if not isinstance(expr, ast.Name) or expr.id not in module.imports:
            return None
        mod, _, name = module.imports[expr.id].rpartition(".")
        owner = self.a.modules.get(mod)
        if owner is not None and name in owner.aliases:
            return owner, owner.aliases[name]
        return None

    def call(self, module: Module, call: ast.Call, locals_: dict, cls: ClassInfo | None):
        f = call.func
        key = self.a.resolve_class(module, f)
        if key:
            return ("inst", key, None)
        canon = self.a.canonical(module, f)
        last, top = canon.split(".")[-1], canon.split(".")[0]
        if (top in HTTP_MODULES and last in HTTP_CLIENT_TYPES) or canon.endswith("aiohttp.ClientSession"):
            host = None
            for kw in call.keywords:
                if kw.arg == "base_url":
                    host = _host(self.a, module, kw.value)
            return ("inst", "http", host)
        if last in SESSION_TYPES or canon.endswith("sessionmaker()") or "SessionLocal" in canon:
            return ("inst", "session", None)
        if canon in self.a.functions:
            fmod, fnode, _ = self.a.functions[canon]
            return self.ann(fmod, getattr(fnode, "returns", None))
        if isinstance(f, ast.Attribute):
            # session.get(Order, 1) / db.get_or_404(Order, 1) hand back an Order.
            if f.attr in ("get", "get_or_404", "first_or_404", "one_or_404") and call.args:
                k = self.a.resolve_class(module, call.args[0])
                if k:
                    return ("inst", k, None)
            # Order.objects.get(...) / Order.query.first(): Django and Flask-SQLAlchemy managers.
            manager = manager_class(self.a, module, f)
            if manager and f.attr in ("get", "create", "first", "last", "get_or_404", "first_or_404", "one"):
                return ("inst", manager[0], None)
            # A repo method with a return annotation: service.get_order(...) -> Order.
            recv = self.expr(module, f.value, locals_, cls)
            if recv and recv[1] in self.a.classes:
                found = find_method(self.a, recv[1], f.attr)
                if found:
                    info, node = found
                    return self.ann(info.module, getattr(node, "returns", None))
        return None

    def expr(self, module: Module, e: ast.AST, locals_: dict, cls: ClassInfo | None):
        if isinstance(e, ast.Await):
            return self.expr(module, e.value, locals_, cls)
        if isinstance(e, ast.Name):
            if e.id in locals_:
                return locals_[e.id]
            if e.id in module.instance_calls:
                return self.call(module, module.instance_calls[e.id], {}, None)
            if e.id in module.imports:
                mod, _, name = module.imports[e.id].rpartition(".")
                owner = self.a.modules.get(mod)
                if owner is not None and name in owner.instance_calls:
                    return self.call(owner, owner.instance_calls[name], {}, None)
            key = self.a.resolve_class(module, e)
            return ("cls", key) if key else None
        if isinstance(e, ast.Attribute):
            if isinstance(e.value, ast.Name) and e.value.id in ("self", "cls") and cls is not None:
                return class_attr(self.a, cls.key, e.attr)
            key = self.a.resolve_class(module, e)
            if key:
                return ("cls", key)
            return None
        if isinstance(e, ast.Call):
            return self.call(module, e, locals_, cls)
        return None


def _host(a: Analyzer, module: Module, expr: ast.AST | None) -> str | None:
    # "https://host/path?x={}".format(...) and "https://host" + path: the address is the left-hand string.
    while isinstance(expr, ast.Call) and isinstance(expr.func, ast.Attribute) and expr.func.attr == "format":
        expr = expr.func.value
    while isinstance(expr, ast.BinOp) and isinstance(expr.op, ast.Add):
        expr = expr.left
    if expr is None:
        return None
    text = None
    if isinstance(expr, ast.Constant) and isinstance(expr.value, str):
        text = expr.value
    elif isinstance(expr, ast.JoinedStr) and expr.values and isinstance(expr.values[0], ast.Constant):
        text = str(expr.values[0].value)
    elif isinstance(expr, ast.Name) and expr.id in module.constants:
        text = module.constants[expr.id]
    if text and "://" in text:
        host = urlparse(text).hostname
        return host or None
    return None


def class_attr(a: Analyzer, key: str, attr: str, depth: int = 0):
    info = a.classes.get(key)
    if info is None or depth > 8:
        return None
    if attr in info.attrs:
        return info.attrs[attr]
    for base in info.bases:
        if base in a.classes:
            found = class_attr(a, base, attr, depth + 1)
            if found:
                return found
    return None


def find_method(a: Analyzer, key: str, name: str, depth: int = 0):
    info = a.classes.get(key)
    if info is None or depth > 8:
        return None
    if name in info.methods:
        return info, info.methods[name]
    for base in info.bases:
        if base in a.classes:
            found = find_method(a, base, name, depth + 1)
            if found:
                return found
    return None


def inherits(a: Analyzer, key: str, target: str, depth: int = 0) -> bool:
    info = a.classes.get(key)
    if info is None or depth > 8:
        return False
    return any(b == target or inherits(a, b, target, depth + 1) for b in info.bases)


def ancestors_canonical(a: Analyzer, key: str, depth: int = 0) -> list[str]:
    """Every base name up the chain, repo classes included: ['app.models.Base', 'sqlalchemy.orm.DeclarativeBase']."""
    info = a.classes.get(key)
    if info is None or depth > 8:
        return []
    out = []
    for b in info.bases:
        out.append(b)
        out += ancestors_canonical(a, b, depth + 1)
    return out


def manager_class(a: Analyzer, module: Module, f: ast.Attribute):
    """Order.objects.filter(...).update(...) -> ('app.models.Order', ['update', 'filter'])."""
    methods = [f.attr]
    e = f.value
    while True:
        if isinstance(e, ast.Call):
            e = e.func
        elif isinstance(e, ast.Attribute):
            if e.attr in ("objects", "query"):
                key = a.resolve_class(module, e.value)
                return (key, methods) if key else None
            methods.append(e.attr)
            e = e.value
        else:
            return None


# ---------------------------------------------------------------- body walking

class Walker(ast.NodeVisitor):
    """Walks one function body in source order, keeping local variable types up to date."""

    def __init__(self, g: "GraphBuilder", module: Module, cls: ClassInfo | None, func: ast.AST, record_attrs: bool):
        self.g, self.a, self.t = g, g.a, g.types
        self.module, self.cls, self.func = module, cls, func
        self.record_attrs = record_attrs
        self.locals: dict = {}
        self.events: list[tuple] = []
        args = func.args
        params = args.posonlyargs + args.args + args.kwonlyargs
        for i, p in enumerate(params):
            if i == 0 and cls is not None and p.arg == "self":
                self.locals["self"] = ("inst", cls.key, None)
            elif i == 0 and cls is not None and p.arg == "cls":
                self.locals["cls"] = ("cls", cls.key)
            else:
                ty = self.t.ann(module, p.annotation)
                if ty:
                    self.locals[p.arg] = ty

    def run(self) -> list[tuple]:
        self._depends()
        for stmt in self.func.body:
            self.visit(stmt)
        return self.events

    def _event(self, *payload, node: ast.AST, path: str | None = None):
        self.events.append((*payload, path or self.module.path, node.lineno))

    # FastAPI runs Depends(provider) before the function body: that's a call from this function.
    def _depends(self) -> None:
        args = self.func.args
        params = args.posonlyargs + args.args + args.kwonlyargs
        defaults = list(args.defaults) + [d for d in args.kw_defaults if d is not None]
        for p in params:
            self._depends_in(self.module, p.annotation, p)
        for d in defaults:
            self._depends_in(self.module, d, d)

    def _depends_in(self, module: Module, expr: ast.AST | None, site: ast.AST, depth: int = 0) -> None:
        if expr is None or depth > 4:
            return
        for node in ast.walk(expr):
            if isinstance(node, ast.Name):
                alias = module.aliases.get(node.id)
                owner = module
                if alias is None:
                    found = self.t._imported_alias(module, node)
                    if found:
                        owner, alias = found
                if alias is not None:
                    self._depends_in(owner, alias, site, depth + 1)
            if isinstance(node, ast.Call) and self.a.canonical(module, node.func).split(".")[-1] in ("Depends", "Security"):
                target = node.args[0] if node.args else next((k.value for k in node.keywords if k.arg == "dependency"), None)
                if target is not None:
                    fk = self.a.canonical(module, target)
                    if fk in self.a.functions:
                        self._event("dep", fk, node=site)

    # ---- statements that change what a name holds

    def visit_Assign(self, node: ast.Assign) -> None:
        self.visit(node.value)
        ty = self.t.expr(self.module, node.value, self.locals, self.cls)
        for target in node.targets:
            self._assign(target, ty, node)

    def visit_AnnAssign(self, node: ast.AnnAssign) -> None:
        if node.value is not None:
            self.visit(node.value)
        ty = self.t.ann(self.module, node.annotation) or (
            self.t.expr(self.module, node.value, self.locals, self.cls) if node.value is not None else None)
        self._assign(node.target, ty, node)

    def visit_AugAssign(self, node: ast.AugAssign) -> None:
        self.visit(node.value)
        self._assign(node.target, None, node, rebind=False)

    def _assign(self, target: ast.AST, ty, node: ast.AST, rebind: bool = True) -> None:
        if isinstance(target, ast.Name) and rebind:
            if ty:
                self.locals[target.id] = ty
            else:
                self.locals.pop(target.id, None)
        elif isinstance(target, ast.Attribute):
            if self.record_attrs and isinstance(target.value, ast.Name) and target.value.id == "self" \
                    and self.cls is not None and ty and ty[0] == "inst":
                self.cls.attrs.setdefault(target.attr, ty)
            owner = self.t.expr(self.module, target.value, self.locals, self.cls)
            # order.status = "closed" on an instance: a write if Order turns out to be stored data.
            if owner and owner[0] == "inst" and owner[1] in self.a.classes and not (
                    isinstance(target.value, ast.Name) and target.value.id == "self"):
                self._event("attr_write", owner[1], node=node)
            self.visit(target.value)

    def visit_With(self, node) -> None:
        for item in node.items:
            self.visit(item.context_expr)
            if isinstance(item.optional_vars, ast.Name):
                ty = self.t.expr(self.module, item.context_expr, self.locals, self.cls)
                if ty:
                    self.locals[item.optional_vars.id] = ty
        for stmt in node.body:
            self.visit(stmt)

    visit_AsyncWith = visit_With

    # ---- calls

    def visit_Call(self, node: ast.Call) -> None:
        self._call(node)
        self.generic_visit(node)

    def _call(self, node: ast.Call) -> None:
        f = node.func
        name = f.attr if isinstance(f, ast.Attribute) else f.id if isinstance(f, ast.Name) else None
        if name is None:
            return

        # select(Order), session.query(Order), session.get(Order, 1), insert(Order): the class is the table.
        if name in TABLE_READ_FUNCS or name in TABLE_WRITE_FUNCS or name in TABLE_JOIN_FUNCS:
            kind = "write" if name in TABLE_WRITE_FUNCS and name not in TABLE_READ_FUNCS else "read"
            if name == "delete" and isinstance(f, ast.Attribute):
                kind = None  # session.delete(obj) takes an instance, handled below
            if kind:
                for arg in node.args:
                    key = self._class_ref(arg)
                    if key:
                        self._event(kind, key, node=node)

        if isinstance(f, ast.Name):
            fk = self.a.canonical(self.module, f)
            if fk in self.a.functions:
                self._event("fcall", fk, node=node)
            elif fk in self.a.classes:
                # PriceQuote(**response.json()): building data from what an outside call returned.
                if any(isinstance(k.value, ast.Call) and isinstance(k.value.func, ast.Attribute)
                       and k.value.func.attr == "json" for k in node.keywords if k.arg is None):
                    self._event("parse", fk, node=node)
            elif fk == "urllib.request.urlopen" or fk.endswith("request.urlopen"):
                self._http(node, node.args[0] if node.args else None, None)
            return

        if not isinstance(f, ast.Attribute):
            return
        recv = f.value
        recv_canon = self.a.canonical(self.module, recv) if isinstance(recv, (ast.Name, ast.Attribute)) else ""
        rt = self.t.expr(self.module, recv, self.locals, self.cls)

        # requests.get(url) / httpx.post(url), and calls on a client object.
        if recv_canon in HTTP_MODULES and name in HTTP_VERBS:
            url = node.args[1] if name == "request" and len(node.args) > 1 else (node.args[0] if node.args else None)
            url = next((k.value for k in node.keywords if k.arg == "url"), url)
            self._http(node, url, None)
            return
        if rt and rt[1] == "http" and name in HTTP_VERBS:
            url = node.args[1] if name == "request" and len(node.args) > 1 else (node.args[0] if node.args else None)
            url = next((k.value for k in node.keywords if k.arg == "url"), url)
            self._http(node, url, rt[2])
            return

        # session.add(order), db.session.delete(order): a write to whatever the instance is.
        if name in SESSION_WRITES and self._is_session(recv, rt):
            for arg in node.args:
                items = arg.elts if isinstance(arg, (ast.List, ast.Tuple)) else [arg]
                for item in items:
                    ty = self.t.expr(self.module, item, self.locals, self.cls)
                    if ty and ty[0] == "inst" and ty[1] in self.a.classes:
                        self._event("write", ty[1], node=node)
            return

        manager = manager_class(self.a, self.module, f)
        if manager:
            key, methods = manager
            self._event("write" if any(m in MANAGER_WRITES for m in methods) else "read", key, node=node)
            return

        # super().save() / super().handle(): the base class's method.
        if isinstance(recv, ast.Call) and isinstance(recv.func, ast.Name) and recv.func.id == "super" and self.cls:
            for base in self.cls.bases:
                if base in self.a.classes and find_method(self.a, base, name):
                    self._event("call", base, name, node=node)
                    break
            return

        if rt and rt[0] == "cls":
            if name in PARSE_METHODS:
                self._event("parse", rt[1], node=node)
            else:
                self._event("call", rt[1], name, node=node)
            return
        if rt and rt[0] == "inst" and rt[1] in self.a.classes:
            self._event("instcall", rt[1], name, node=node)
            return

        # crud.create_user(...) where crud is an imported module.
        fk = self.a.canonical(self.module, f)
        if fk in self.a.functions:
            self._event("fcall", fk, node=node)

    def _class_ref(self, arg: ast.AST) -> str | None:
        key = self.a.resolve_class(self.module, arg) if isinstance(arg, (ast.Name, ast.Attribute)) else None
        if key:
            return key
        # select(Order.id, Order.total): a column of the class still reads its table.
        if isinstance(arg, ast.Attribute):
            return self.a.resolve_class(self.module, arg.value) if isinstance(arg.value, (ast.Name, ast.Attribute)) else None
        return None

    def _is_session(self, recv: ast.AST, rt) -> bool:
        if rt and rt[1] == "session":
            return True
        ident = recv.attr if isinstance(recv, ast.Attribute) else recv.id if isinstance(recv, ast.Name) else ""
        ident = ident.lower()
        return "session" in ident or ident in ("db", "sess")

    def _http(self, node: ast.Call, url: ast.AST | None, base_host: str | None) -> None:
        host = _host(self.a, self.module, url) or base_host
        self._event("http", host, node=node)


# ---------------------------------------------------------------- the graph

class GraphBuilder:
    def __init__(self, analyzer: Analyzer):
        self.a = analyzer
        self.types = Types(analyzer)
        self.elements: list[dict] = []
        self.by_id: dict[str, dict] = {}
        self.relationships: list[dict] = []
        self.edge_keys: set = set()
        self.class_elements: dict[str, str] = {}
        self.function_boxes: dict[str, str] = {}
        self.route_ids: dict[str, list[str]] = {}
        self.events: dict[str, list[tuple]] = {}

    def build(self) -> dict:
        a = self.a
        for info in a.classes.values():
            info.bases = [a.canonical(info.module, b) for b in info.node.bases]
            for item in info.node.body:
                if isinstance(item, ast.AnnAssign) and isinstance(item.target, ast.Name):
                    ty = self.types.ann(info.module, item.annotation)
                    if ty and ty[0] == "inst":
                        info.attrs.setdefault(item.target.id, ty)
        # Pass 1: learn self.x types. Pass 2: record events.
        for info in a.classes.values():
            for node in info.methods.values():
                Walker(self, info.module, info, node, record_attrs=True).run()
        for fk, (module, node, info) in self._functions_in_order():
            self.events[fk] = Walker(self, module, info, node, record_attrs=False).run()

        self._signals()
        self._components()
        self._classes()
        self._modules_and_routes()
        for fk, _ in self._functions_in_order():
            for owner in self._owners(fk):
                for event in self.events.get(fk, []):
                    self._edge_for(owner, fk, event)
        self._aggregations()
        return {"elements": self.elements, "relationships": self.relationships}

    def _functions_in_order(self):
        for module in sorted(self.a.modules.values(), key=lambda m: m.name):
            for name, node in module.functions.items():
                yield f"{module.name}.{name}", (module, node, None)
            for cname in module.classes:
                info = self.a.classes[f"{module.name}.{cname}"]
                for mname, node in info.methods.items():
                    yield f"{info.key}.{mname}", (module, node, info)

    # ---- signals the classifier needs

    def _signals(self) -> None:
        a = self.a
        self.route_funcs = {r.func_key for r in a.routes}
        self.wire_types: set[str] = set()
        self.injected: set[str] = set()
        self.repo_refs: set[str] = set()
        self.external_types: set[str] = set()

        for route in a.routes:
            func = route.node
            anns = [p.annotation for p in func.args.posonlyargs + func.args.args + func.args.kwonlyargs
                    if not self._has_depends(route.module, p.annotation)]
            anns.append(getattr(func, "returns", None))
            for dec in func.decorator_list:
                if isinstance(dec, ast.Call):
                    anns += [k.value for k in dec.keywords if k.arg == "response_model"]
            for ann in anns:
                self.wire_types |= self._classes_in(route.module, ann)

        # Depends(get_order_service) -> OrderService: the shape of an injected service.
        for fk, events in self.events.items():
            for event in events:
                if event[0] == "dep" and event[1] in a.functions:
                    mod, node, _ = a.functions[event[1]]
                    ty = self.types.ann(mod, getattr(node, "returns", None))
                    if ty and ty[1] in a.classes:
                        self.injected.add(ty[1])
            mod, node, info = a.functions[fk]
            for p in node.args.posonlyargs + node.args.args + node.args.kwonlyargs:
                if self._has_depends(mod, p.annotation) or self._default_is_depends(mod, node, p):
                    ty = self.types.ann(mod, p.annotation)
                    if ty and ty[1] in a.classes:
                        self.injected.add(ty[1])
            # Data parsed from an HTTP response in the same function came from outside.
            if any(e[0] == "http" for e in events):
                self.external_types |= {e[1] for e in events if e[0] == "parse"}

        for info in a.classes.values():
            last = info.module.name.split(".")[-1]
            if info.name.endswith(("Repository", "Repo")) or last in REPOSITORY_MODULES:
                for node in ast.walk(info.node):
                    if isinstance(node, (ast.Name, ast.Attribute)):
                        key = a.resolve_class(info.module, node)
                        if key and key != info.key:
                            self.repo_refs.add(key)
        for module in a.modules.values():
            if module.name.split(".")[-1] in REPOSITORY_MODULES:
                for func in module.functions.values():
                    for node in ast.walk(func):
                        if isinstance(node, (ast.Name, ast.Attribute)):
                            key = a.resolve_class(module, node)
                            if key:
                                self.repo_refs.add(key)

    def _has_depends(self, module: Module, ann: ast.AST | None, depth: int = 0) -> bool:
        if ann is None or depth > 4:
            return False
        for node in ast.walk(ann):
            if isinstance(node, ast.Call) and self.a.canonical(module, node.func).split(".")[-1] in ("Depends", "Security"):
                return True
            if isinstance(node, ast.Name):
                if node.id in module.aliases and self._has_depends(module, module.aliases[node.id], depth + 1):
                    return True
                found = self.types._imported_alias(module, node)
                if found and self._has_depends(found[0], found[1], depth + 1):
                    return True
        return False

    def _default_is_depends(self, module: Module, func: ast.AST, param: ast.arg) -> bool:
        args = func.args
        positional = args.posonlyargs + args.args
        if param in positional:
            offset = len(positional) - len(args.defaults)
            i = positional.index(param) - offset
            default = args.defaults[i] if i >= 0 else None
        else:
            default = args.kw_defaults[args.kwonlyargs.index(param)]
        return default is not None and self._has_depends(module, default)

    def _classes_in(self, module: Module, ann: ast.AST | None) -> set[str]:
        found = set()
        if ann is None:
            return found
        for node in ast.walk(ann):
            if isinstance(node, ast.Constant) and isinstance(node.value, str):
                try:
                    found |= self._classes_in(module, ast.parse(node.value, mode="eval").body)
                except SyntaxError:
                    pass
            elif isinstance(node, (ast.Name, ast.Attribute)):
                key = self.a.resolve_class(module, node)
                if key:
                    found.add(key)
        return found

    # ---- classification

    def _family(self, key: str) -> dict:
        a = self.a
        info = a.classes[key]
        names = ancestors_canonical(a, key)
        lasts = {n.split(".")[-1] for n in names}
        decorators = [a.canonical(info.module, d.func if isinstance(d, ast.Call) else d) for d in info.node.decorator_list]
        sqlmodel_table = any(k.arg == "table" and isinstance(k.value, ast.Constant) and k.value.value is True
                             for k in info.node.keywords)
        orm_base = any(
            n.split(".")[-1] in ("DeclarativeBase", "DeclarativeBaseNoMeta")
            or n.endswith("declarative_base()") or n.endswith("generate_base()")
            or (n.endswith(".Model") and "SQLAlchemy()" in n)
            or n.endswith("django.db.models.Model")
            for n in names) or any(d.endswith(("as_declarative", "as_declarative()", ".mapped")) for d in decorators)
        django = any(n.endswith("django.db.models.Model") for n in names)
        sqlmodel = "SQLModel" in lasts
        pydantic = (any(n.split(".")[-1] in ("BaseModel", "RootModel", "GenericModel") for n in names)
                    or (sqlmodel and not sqlmodel_table)) and "BaseSettings" not in lasts
        exception = any(n.split(".")[-1] in ("Exception", "BaseException") or n.endswith(("Error", "Exception"))
                        for n in names)
        enum = bool(lasts & {"Enum", "IntEnum", "StrEnum", "Flag", "IntFlag"})
        protocol = bool(lasts & {"Protocol"})
        return dict(orm_base=orm_base or (sqlmodel and sqlmodel_table), django=django, sqlmodel=sqlmodel,
                    sqlmodel_table=sqlmodel_table, pydantic=pydantic, exception=exception, enum=enum,
                    protocol=protocol)

    def _is_mapped(self, info: ClassInfo, fam: dict) -> bool:
        if not fam["orm_base"]:
            return False
        body = info.node.body
        for item in body:
            if isinstance(item, ast.Assign) and any(isinstance(t, ast.Name) and t.id == "__abstract__" for t in item.targets) \
                    and isinstance(item.value, ast.Constant) and item.value.value is True:
                return False
            if isinstance(item, ast.ClassDef) and item.name == "Meta" and "abstract = True" in ast.unparse(item):
                return False
        if fam["sqlmodel"]:
            return fam["sqlmodel_table"]
        if fam["django"]:
            return True
        for item in body:
            if isinstance(item, ast.Assign) and any(isinstance(t, ast.Name) and t.id in ("__tablename__", "__table__")
                                                    for t in item.targets):
                return True
            if isinstance(item, ast.AnnAssign) and "Mapped" in ast.unparse(item.annotation):
                return True
            if isinstance(item, ast.Assign) and isinstance(item.value, ast.Call) \
                    and ast.unparse(item.value.func).split(".")[-1] in ("Column", "mapped_column"):
                return True
        return False

    def _tablename(self, info: ClassInfo) -> str | None:
        for item in info.node.body:
            if isinstance(item, ast.Assign) and any(isinstance(t, ast.Name) and t.id == "__tablename__" for t in item.targets) \
                    and isinstance(item.value, ast.Constant) and isinstance(item.value.value, str):
                return item.value.value.lower()
        return None

    def _data_and_methods(self, info: ClassInfo) -> tuple[int, int]:
        data = methods = 0
        for item in info.node.body:
            if isinstance(item, ast.AnnAssign) and isinstance(item.target, ast.Name):
                if not _dunder(item.target.id) and item.target.id != "model_config":
                    data += 1
            elif isinstance(item, ast.Assign):
                names = [t.id for t in item.targets if isinstance(t, ast.Name)]
                if any(not n.startswith("_") and n != "model_config" for n in names):
                    data += 1
            elif isinstance(item, (ast.FunctionDef, ast.AsyncFunctionDef)) and not _dunder(item.name):
                if _is_property(item):
                    data += 1
                else:
                    methods += 1
        return data, methods

    def classify(self, key: str):
        info = self.a.classes[key]
        fam = self._family(key)
        if fam["exception"] or fam["enum"]:
            return None
        signals: list[str] = []
        score = 0

        def add(points: int, reason: str) -> None:
            nonlocal score
            score += points
            signals.append(f"{reason} {'+' if points > 0 else ''}{points}")

        orm = self._is_mapped(info, fam)
        if orm:
            add(5, "ORM mapping")
        table = self._tablename(info)
        schema = _matches_table(info.name, self.a.schema_tables) or (table is not None and table in self.a.schema_tables)
        if schema:
            add(5, "Schema file match")
        if key in self.repo_refs:
            add(3, "Used in a repository")
        wire = (fam["pydantic"] or key in self.wire_types) and key not in self.injected
        if wire:
            add(2, "Serialized over the wire")
        data, methods = self._data_and_methods(info)
        if data > 0 and data >= 0.8 * (data + methods):
            add(2, "Mostly properties")
        if info.name.endswith(DATA_SUFFIXES):
            add(1, "Data name suffix")
        if info.name.endswith(LOGIC_SUFFIXES):
            add(-5, "Logic name suffix")
        kind = "Stored" if orm or schema else "External" if key in self.external_types else "Dto" if wire else None

        if score >= ENTITY_THRESHOLD:
            return ("DataObject", 1.0, score, signals, kind)
        if score >= AMBIGUOUS_THRESHOLD:
            return ("DataObject", UNSURE, score, signals, kind)
        if not any(not _dunder(n) and not _is_abstract(m) and not _is_property(m) for n, m in info.methods.items()):
            return None
        # Injected through Depends, or implementing one of the repo's own base classes: a recognized service.
        recognized = key in self.injected or any(b in self.a.classes for b in info.bases)
        return ("ApplicationService", 0.9 if recognized else UNSURE, None, None, None)

    # ---- boxes

    def _add(self, element: dict) -> None:
        self.elements.append(element)
        self.by_id[element["id"]] = element

    def _components(self) -> None:
        seen: dict[str, Module] = {}
        for module in sorted(self.a.modules.values(), key=lambda m: m.path):
            if module.component not in seen or module.path.endswith("__init__.py") \
                    and not seen[module.component].path.endswith("__init__.py"):
                seen[module.component] = module
        for name in sorted(seen):
            self._add({"id": "cmp:" + name, "type": "ApplicationComponent", "layer": "Application", "name": name,
                       "source": {"file": seen[name].path, "line": 1}, "confidence": 1.0})

    def _classes(self) -> None:
        self.stored = set()
        for key in sorted(self.a.classes):
            info = self.a.classes[key]
            result = self.classify(key)
            if result is None:
                continue
            etype, confidence, score, signals, kind = result
            is_data = etype == "DataObject"
            element = {"id": ("do:" if is_data else "cls:") + key, "type": etype, "layer": "Application",
                       "name": info.name, "parent": "cmp:" + info.module.component,
                       "source": {"file": info.module.path, "line": info.node.lineno}, "confidence": confidence}
            if is_data:
                element.update({"entityScore": score, "entitySignals": signals, "fields": self._fields(info)})
                if kind:
                    element["entityKind"] = kind
                if kind == "Stored":
                    self.stored.add(key)
            self._add(element)
            self.class_elements[key] = element["id"]
            if is_data:
                continue
            for mname, node in info.methods.items():
                if _dunder(mname) or _is_abstract(node) or _is_property(node):
                    continue
                fid = f"fn:{key}.{mname}"
                fn = {"id": fid, "type": "ApplicationFunction", "layer": "Application", "name": f"{info.name}.{mname}",
                      "parent": element["id"], "source": {"file": info.module.path, "line": node.lineno},
                      "confidence": 1.0}
                if mname.startswith("_"):
                    fn["isPrivate"] = True
                self._add(fn)
                self.function_boxes[f"{key}.{mname}"] = fid

    def _fields(self, info: ClassInfo) -> list[dict]:
        out = []
        for item in info.node.body:
            if isinstance(item, ast.AnnAssign) and isinstance(item.target, ast.Name) and not item.target.id.startswith("_") \
                    and item.target.id != "model_config":
                out.append({"name": item.target.id, "type": ast.unparse(item.annotation)})
            elif isinstance(item, ast.Assign) and len(item.targets) == 1 and isinstance(item.targets[0], ast.Name) \
                    and not item.targets[0].id.startswith("_") and isinstance(item.value, ast.Call):
                call = item.value
                ty = ast.unparse(call.args[0]) if call.args and ast.unparse(call.func).split(".")[-1] in (
                    "Column", "mapped_column") else ast.unparse(call.func).split(".")[-1]
                out.append({"name": item.targets[0].id, "type": ty})
        return out

    def _modules_and_routes(self) -> None:
        routes_by_module: dict[str, list] = {}
        for route in self.a.routes:
            routes_by_module.setdefault(route.module.name, []).append(route)
        for module in sorted(self.a.modules.values(), key=lambda m: m.name):
            funcs = [n for n in module.functions if f"{module.name}.{n}" not in self.route_funcs]
            routes = routes_by_module.get(module.name, [])
            if not funcs and not routes:
                continue
            mid = "mod:" + module.name
            short = module.name[len(module.component) + 1:] if module.name.startswith(module.component + ".") else module.name
            self._add({"id": mid, "type": "ApplicationInterface" if routes else "ApplicationService",
                       "layer": "Application", "name": short or module.name.split(".")[-1],
                       "parent": "cmp:" + module.component, "source": {"file": module.path, "line": 1},
                       "confidence": 1.0 if routes else 0.9})
            last = module.name.split(".")[-1]
            for name in funcs:
                node = module.functions[name]
                fid = f"fn:{module.name}.{name}"
                fn = {"id": fid, "type": "ApplicationFunction", "layer": "Application", "name": f"{last}.{name}",
                      "parent": mid, "source": {"file": module.path, "line": node.lineno}, "confidence": 1.0}
                if name.startswith("_") and not _dunder(name):
                    fn["isPrivate"] = True
                self._add(fn)
                self.function_boxes[f"{module.name}.{name}"] = fid
            for route in routes:
                base = f"api:{route.verb} {route.path}"
                rid = base
                n = 2
                while rid in self.by_id:
                    rid = f"{base} #{n}"
                    n += 1
                self._add({"id": rid, "type": "ApplicationInterface", "layer": "Application",
                           "name": f"{last}.{route.node.name}", "route": f"{route.verb} {route.path}", "parent": mid,
                           "source": {"file": module.path, "line": route.node.lineno}, "confidence": 1.0})
                self.route_ids.setdefault(route.func_key, []).append(rid)

    def _owners(self, fk: str) -> list[str]:
        if fk in self.route_ids:
            return self.route_ids[fk]
        if fk in self.function_boxes:
            return [self.function_boxes[fk]]
        _, node, info = self.a.functions[fk]
        # __init__, properties and other dunders draw from the class box, like C# constructors.
        if info is not None and self.class_elements.get(info.key, "").startswith("cls:"):
            return [self.class_elements[info.key]]
        return []

    # ---- arrows

    def _edge(self, kind: str, src: str, dst: str, path: str, line: int, access: str | None = None,
              confidence: float = 1.0) -> None:
        if src == dst and kind == "Serving":
            return
        key = (src, dst, kind, access)
        if key in self.edge_keys:
            return
        self.edge_keys.add(key)
        rel = {"id": f"rel:{len(self.relationships) + 1}", "type": kind, "from": src, "to": dst,
               "evidence": {"file": path, "line": line}, "confidence": confidence}
        if access:
            rel["access"] = access
        self.relationships.append(rel)

    def _targets(self, key: str, method: str) -> list[tuple[str, float]]:
        found = find_method(self.a, key, method)
        if found is None:
            return []
        info, node = found
        abstract = _is_abstract(node) or self._family(info.key)["protocol"]
        if not abstract:
            return [(f"{info.key}.{method}", 1.0)]
        impls = [c.key for c in self.a.classes.values()
                 if c.key != info.key and inherits(self.a, c.key, info.key)
                 and method in c.methods and not _is_abstract(c.methods[method])]
        conf = 1.0 if len(impls) == 1 else UNSURE
        return [(f"{k}.{method}", conf) for k in impls]

    def _edge_for(self, owner: str, fk: str, event: tuple) -> None:
        kind, path, line = event[0], event[-2], event[-1]
        if kind in ("fcall", "dep"):
            target = event[1]
            if target in self.function_boxes:
                self._edge("Serving", owner, self.function_boxes[target], path, line)
        elif kind in ("call", "instcall"):
            key, method = event[1], event[2]
            if key in self.stored:
                found = find_method(self.a, key, method)
                if found is not None:
                    self._edge("Access", owner, self.class_elements[key], path, line,
                               "Read" if _returns_value(found[1]) else "Write")
                elif kind == "instcall" and method in INSTANCE_WRITES:
                    self._edge("Access", owner, self.class_elements[key], path, line, "Write")
                return
            for target, conf in self._targets(key, method):
                if target in self.function_boxes:
                    self._edge("Serving", owner, self.function_boxes[target], path, line, confidence=conf)
        elif kind in ("read", "write"):
            element = self.class_elements.get(event[1], "")
            if element.startswith("do:"):
                self._edge("Access", owner, element, path, line, "Read" if kind == "read" else "Write")
        elif kind == "attr_write":
            if event[1] in self.stored:
                self._edge("Access", owner, self.class_elements[event[1]], path, line, "Write")
        elif kind == "parse":
            # Only data parsed out of an HTTP response is a read (like GetFromJsonAsync<T> in C#);
            # ItemPublic.model_validate(item) in a route just reshapes a row for the response.
            element = self.class_elements.get(event[1], "")
            if element.startswith("do:") and any(e[0] == "http" for e in self.events.get(fk, [])):
                self._edge("Access", owner, element, path, line, "Read")
        elif kind == "http":
            self._edge("Flow", owner, self._external(event[1], owner, path, line), path, line)

    def _external(self, host: str | None, owner: str, path: str, line: int) -> str:
        if host:
            name, confidence = host, 1.0
        else:
            # No address in the code: name it after the caller and ask a human to look.
            element = self.by_id[owner]
            parent = self.by_id.get(element.get("parent", ""), element)
            name, confidence = f"{parent['name']} HTTP target", UNSURE
        eid = "ext:" + name
        if eid not in self.by_id:
            self._add({"id": eid, "type": "ExternalSystem", "layer": "Application", "name": name,
                       "source": {"file": path, "line": line}, "confidence": confidence})
        return eid

    def _aggregations(self) -> None:
        for key, eid in sorted(self.class_elements.items()):
            if not eid.startswith("do:"):
                continue
            info = self.a.classes[key]
            for item in info.node.body:
                if not (isinstance(item, ast.AnnAssign) and isinstance(item.target, ast.Name)):
                    continue
                child = self._collection_item(info.module, item.annotation)
                cid = self.class_elements.get(child or "", "")
                if cid.startswith("do:") and cid != eid:
                    self._edge("Aggregation", eid, cid, info.module.path, item.lineno)

    def _collection_item(self, module: Module, ann: ast.AST, depth: int = 0) -> str | None:
        if depth > 4:
            return None
        if isinstance(ann, ast.Constant) and isinstance(ann.value, str):
            try:
                return self._collection_item(module, ast.parse(ann.value, mode="eval").body, depth + 1)
            except SyntaxError:
                return None
        if isinstance(ann, ast.Subscript):
            base = self.a.canonical(module, ann.value).split(".")[-1]
            arg = ann.slice.elts[0] if isinstance(ann.slice, ast.Tuple) else ann.slice
            if base in ("Mapped", "Optional", "Annotated"):
                return self._collection_item(module, arg, depth + 1)
            if base in COLLECTIONS:
                if isinstance(arg, ast.Constant) and isinstance(arg.value, str):
                    try:
                        arg = ast.parse(arg.value, mode="eval").body
                    except SyntaxError:
                        return None
                return self.a.resolve_class(module, arg) if isinstance(arg, (ast.Name, ast.Attribute)) else None
        return None


def analyze(root: str, include_tests: bool = False) -> dict:
    analyzer = Analyzer(root, include_tests)
    analyzer.load()
    graph = GraphBuilder(analyzer).build()
    graph["warnings"] = analyzer.warnings
    return graph

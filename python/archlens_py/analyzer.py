"""Python front end for ArchLens.

Reads a Python repo with the standard library's ast module and produces the same boxes and arrows
the C# analyzer does (same ids, same 10 box types, same evidence rule: every arrow points at the
line that proves it). The C# side then traces call flows and scores the graph exactly as it does
for C# repos, so the viewer, the API and the score need no Python-specific code.

Rules mirror the C# ones:
  package                       -> Application Component
  FastAPI / Flask route         -> Application Interface (its module is the "controller")
  class with logic              -> Application Service (0.9 if injected via Depends or implements a
                                   repo base class, else 0.5)
  module with functions         -> Application Service holding Application Functions
  class with entity score >= 5  -> Data Object (2 to 4 = unsure)
  requests / httpx call         -> External System
Nothing here imports the analyzed code; it only reads it.
"""
from __future__ import annotations

import ast
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path
from urllib.parse import urlparse

UNSURE = 0.5
ENTITY_THRESHOLD = 5
AMBIGUOUS_THRESHOLD = 2

SKIP_DIRS = {
    ".git", ".hg", ".venv", "venv", "env", ".env", "node_modules", "site-packages", "__pycache__",
    "build", "dist", ".tox", ".nox", ".mypy_cache", ".pytest_cache", "docs", "docs_src", ".eggs",
}
TEST_DIRS = {"tests", "test", "testing"}
# Migration folders are generated code: read for table names, never drawn (the C# side skips EF migrations too).
MIGRATION_DIRS = {"migrations", "versions", "alembic"}

HTTP_VERBS = {"get", "post", "put", "patch", "delete", "head", "options", "request", "send", "stream"}
HTTP_MODULES = {"requests", "httpx"}
HTTP_CLIENT_TYPES = {"Client", "AsyncClient", "Session", "ClientSession"}
ROUTE_VERBS = {"get", "post", "put", "patch", "delete", "head", "options"}
ROUTER_TYPES = {"APIRouter", "FastAPI", "Flask", "Blueprint"}

# session.add(order) and friends change the table; Django manager methods split the same way.
SESSION_WRITES = {"add", "add_all", "delete", "merge"}
MANAGER_WRITES = {"create", "bulk_create", "get_or_create", "update_or_create", "update", "delete", "bulk_update"}
# select(Order), session.query(Order), session.get(Order, 1): a class handed to these is a read.
TABLE_READ_FUNCS = {"select", "query", "get", "get_or_404", "one_or_404", "first_or_404"}
TABLE_WRITE_FUNCS = {"insert", "update", "delete"}
INSTANCE_WRITES = {"save", "delete"}
PARSE_METHODS = {"model_validate", "model_validate_json", "parse_obj", "parse_raw", "from_orm"}

DATA_SUFFIXES = ("Entity", "Model", "Record", "Dto", "DTO", "Schema")
LOGIC_SUFFIXES = ("Service", "Controller", "Handler", "Helper", "Factory")
REPOSITORY_MODULES = {"crud", "repository", "repositories", "repo", "repos", "dao"}
COLLECTIONS = {"list", "List", "set", "Set", "Sequence", "Iterable", "tuple", "Tuple", "frozenset"}


# ---------------------------------------------------------------- index

@dataclass
class Module:
    name: str
    path: str
    tree: ast.Module
    package: str
    component: str
    imports: dict[str, str] = field(default_factory=dict)
    # Module-level NAME = Call(...): what kind of object NAME is ("httpx.Client", "fastapi.APIRouter", ...).
    instances: dict[str, str] = field(default_factory=dict)
    instance_calls: dict[str, ast.Call] = field(default_factory=dict)
    constants: dict[str, str] = field(default_factory=dict)
    # SessionDep = Annotated[Session, Depends(get_db)]: annotation aliases, expanded where they're used.
    aliases: dict[str, ast.AST] = field(default_factory=dict)
    functions: dict[str, ast.AST] = field(default_factory=dict)
    classes: dict[str, ast.ClassDef] = field(default_factory=dict)


@dataclass
class ClassInfo:
    key: str
    name: str
    module: Module
    node: ast.ClassDef
    bases: list[str] = field(default_factory=list)
    methods: dict[str, ast.AST] = field(default_factory=dict)
    # self.x -> type marker, from __init__ parameters, constructor calls and class-level annotations.
    attrs: dict[str, tuple] = field(default_factory=dict)


@dataclass
class Router:
    key: str
    kind: str
    prefix: str
    includes: list[tuple[str, str]] = field(default_factory=list)


@dataclass
class Route:
    verb: str
    path: str
    func_key: str
    node: ast.AST
    module: Module


def _is_test_path(parts: tuple[str, ...]) -> bool:
    name = parts[-1]
    return (any(p in TEST_DIRS for p in parts[:-1]) or name.startswith("test_") or name.endswith("_test.py")
            or name in ("conftest.py", "tests.py"))


class Analyzer:
    def __init__(self, root: str, include_tests: bool = False):
        self.root = Path(root).resolve()
        self.include_tests = include_tests
        self.modules: dict[str, Module] = {}
        self.classes: dict[str, ClassInfo] = {}
        self.functions: dict[str, tuple[Module, ast.AST, ClassInfo | None]] = {}
        self.routers: dict[str, Router] = {}
        self.routes: list[Route] = []
        self.schema_tables: set[str] = set()
        self.warnings: list[str] = []

    # ---- loading

    def load(self) -> None:
        files = []
        for path in sorted(self.root.rglob("*.py")):
            rel = path.relative_to(self.root)
            parts = rel.parts
            if any(p in SKIP_DIRS or (p.startswith(".") and p != ".") for p in parts[:-1]):
                continue
            if any(p in MIGRATION_DIRS for p in parts[:-1]):
                self._read_migration(path)
                continue
            if not self.include_tests and _is_test_path(parts):
                continue
            files.append(path)
        for sql in sorted(self.root.rglob("*.sql")):
            if not any(p in SKIP_DIRS for p in sql.relative_to(self.root).parts):
                for m in re.finditer(r"create\s+table\s+(?:if\s+not\s+exists\s+)?[\"`\[]?(?:\w+[\"`\]]?\.)?[\"`\[]?(\w+)",
                                     sql.read_text(errors="ignore"), re.IGNORECASE):
                    self.schema_tables.add(m.group(1).lower())

        for path in files:
            tree = self._parse(path)
            if tree is None:
                continue
            name, package, component = self._module_name(path)
            # Two loose scripts called main.py: the second one keeps its folder in the name.
            if name in self.modules:
                name = ".".join(path.relative_to(self.root).with_suffix("").parts)
            self.modules[name] = Module(name, path.relative_to(self.root).as_posix(), tree, package, component)

        for module in self.modules.values():
            self._index_module(module)
        for module in self.modules.values():
            self._index_routers(module)
        # Every include_router must be known before any route's full path is worked out.
        for module in self.modules.values():
            self._index_includes(module)
        for module in self.modules.values():
            self._index_routes(module)

    def _parse(self, path: Path) -> ast.Module | None:
        text = path.read_text(encoding="utf-8", errors="ignore")
        try:
            return ast.parse(text, filename=str(path))
        except (SyntaxError, ValueError):
            pass
        # Python 3.14 allows "except A, B:"; older parsers need the parentheses. Same line numbers either way.
        patched = re.sub(r"^(\s*except\s+)(?!\()([\w.]+(?:\s*,\s*[\w.]+)+)(\s+as\s+\w+)?\s*:", r"\1(\2)\3:",
                         text, flags=re.MULTILINE)
        try:
            return ast.parse(patched, filename=str(path))
        except (SyntaxError, ValueError) as e:
            self.warnings.append(f"skipped {path.relative_to(self.root)}: {e}")
            return None

    def _read_migration(self, path: Path) -> None:
        text = path.read_text(errors="ignore")
        for m in re.finditer(r"create_table\(\s*['\"](\w+)['\"]", text):
            self.schema_tables.add(m.group(1).lower())
        for m in re.finditer(r"CreateModel\(\s*name\s*=\s*['\"](\w+)['\"]", text):
            self.schema_tables.add(m.group(1).lower())

    def _module_name(self, path: Path) -> tuple[str, str, str]:
        # The nearest folder with an __init__.py marks a package. Folders without one between it and the
        # file are namespace packages (PEP 420) and still part of the dotted name.
        package_dir = path.parent
        while package_dir != self.root and not (package_dir / "__init__.py").exists():
            package_dir = package_dir.parent
        if package_dir == self.root:
            # A loose script: its name is just the file name, its component the folder it sits in.
            rel_dir = path.parent.relative_to(self.root)
            return path.stem, "", rel_dir.as_posix() if rel_dir.parts else self.root.name
        # The topmost package above it, even across a gap (dispatch/data/source/ has no __init__.py while
        # dispatch/ and dispatch/data/source/status/ do: Python still imports dispatch.data.source.status).
        top = package_dir
        for ancestor in package_dir.parents:
            if ancestor == self.root or self.root not in ancestor.parents:
                break
            if (ancestor / "__init__.py").exists():
                top = ancestor
        parts = list(path.relative_to(top.parent).with_suffix("").parts)
        if parts[-1] == "__init__":
            parts.pop()
            package = ".".join(parts)
        else:
            package = ".".join(parts[:-1])
        return ".".join(parts), package, parts[0]

    def _index_module(self, module: Module) -> None:
        for node in module.tree.body:
            if isinstance(node, ast.Import):
                for a in node.names:
                    if a.asname:
                        module.imports[a.asname] = a.name
                    else:
                        module.imports[a.name.split(".")[0]] = a.name.split(".")[0]
            elif isinstance(node, ast.ImportFrom):
                base = self._import_base(module, node)
                for a in node.names:
                    if a.name != "*":
                        module.imports[a.asname or a.name] = f"{base}.{a.name}" if base else a.name
            elif isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
                module.functions[node.name] = node
                self.functions[f"{module.name}.{node.name}"] = (module, node, None)
            elif isinstance(node, ast.ClassDef):
                module.classes[node.name] = node
            elif isinstance(node, ast.Assign) and len(node.targets) == 1 and isinstance(node.targets[0], ast.Name):
                target = node.targets[0].id
                if isinstance(node.value, ast.Constant) and isinstance(node.value.value, str):
                    module.constants[target] = node.value.value
                elif isinstance(node.value, ast.Call):
                    module.instance_calls[target] = node.value
                elif isinstance(node.value, (ast.Subscript, ast.BinOp)):
                    module.aliases[target] = node.value
            elif isinstance(node, ast.AnnAssign) and isinstance(node.target, ast.Name) and node.value is not None:
                if isinstance(node.value, ast.Constant) and isinstance(node.value.value, str):
                    module.constants[node.target.id] = node.value.value
                elif isinstance(node.value, ast.Call):
                    module.instance_calls[node.target.id] = node.value
        # Imports inside "if TYPE_CHECKING:" blocks and inside functions (Flask's create_app imports its
        # blueprints there) still name things; top-level imports win on a clash.
        for node in module.tree.body:
            if isinstance(node, (ast.If, ast.FunctionDef, ast.AsyncFunctionDef, ast.Try)):
                for sub in ast.walk(node):
                    if isinstance(sub, ast.ImportFrom):
                        base = self._import_base(module, sub)
                        for a in sub.names:
                            if a.name != "*":
                                module.imports.setdefault(a.asname or a.name, f"{base}.{a.name}" if base else a.name)
        for target, call in module.instance_calls.items():
            module.instances[target] = self.canonical(module, call.func) + "()"
        for name, node in module.classes.items():
            info = ClassInfo(f"{module.name}.{name}", name, module, node)
            for item in node.body:
                if isinstance(item, (ast.FunctionDef, ast.AsyncFunctionDef)):
                    info.methods[item.name] = item
                    self.functions[f"{info.key}.{item.name}"] = (module, item, info)
            self.classes[info.key] = info

    def _import_base(self, module: Module, node: ast.ImportFrom) -> str:
        if not node.level:
            return node.module or ""
        pkg = module.package.split(".") if module.package else []
        if node.level > 1:
            pkg = pkg[: len(pkg) - (node.level - 1)]
        return ".".join(pkg + ([node.module] if node.module else []))

    # ---- name resolution

    def canonical(self, module: Module, expr: ast.AST) -> str:
        """Dotted name an expression refers to, following imports: 'select' -> 'sqlalchemy.select'."""
        parts = []
        while isinstance(expr, ast.Attribute):
            parts.insert(0, expr.attr)
            expr = expr.value
        if isinstance(expr, ast.Call):
            return self.canonical(module, expr.func) + "()" + ("." + ".".join(parts) if parts else "")
        if not isinstance(expr, ast.Name):
            return ""
        head = expr.id
        if head in module.functions or head in module.classes:
            base = f"{module.name}.{head}"
        elif head in module.instances:
            base = module.instances[head]
        elif head in module.imports:
            base = self._follow(module.imports[head])
        else:
            base = head
        return ".".join([base] + parts)

    def _follow(self, dotted: str, depth: int = 0) -> str:
        """'app.models.User' re-exported from app/models/__init__.py -> 'app.models.user.User'."""
        if depth > 5 or dotted in self.classes or dotted in self.functions or dotted in self.modules:
            return dotted
        head, _, last = dotted.rpartition(".")
        owner = self.modules.get(head)
        if owner is None:
            # 'app.db.Model' where app.db is an instance (db = SQLAlchemy()).
            mod, _, inst = head.rpartition(".")
            m = self.modules.get(mod)
            if m is not None and inst in m.instances:
                return m.instances[inst] + "." + last
            return dotted
        if last in owner.imports:
            return self._follow(owner.imports[last], depth + 1)
        if last in owner.instances:
            return owner.instances[last]
        return dotted

    def resolve_class(self, module: Module, expr: ast.AST) -> str | None:
        key = self.canonical(module, expr)
        return key if key in self.classes else None

    # ---- routers and routes

    def _index_routers(self, module: Module) -> None:
        for target, call in module.instance_calls.items():
            kind = self.canonical(module, call.func).split(".")[-1]
            if kind in ROUTER_TYPES:
                prefix = ""
                for kw in call.keywords:
                    if kw.arg in ("prefix", "url_prefix"):
                        prefix = self._string_of(module, kw.value)
                self.routers[f"{module.name}.{target}"] = Router(f"{module.name}.{target}", kind, prefix)

    def _router_key(self, module: Module, expr: ast.AST) -> str | None:
        if isinstance(expr, ast.Name) and f"{module.name}.{expr.id}" in self.routers:
            return f"{module.name}.{expr.id}"
        if isinstance(expr, (ast.Name, ast.Attribute)):
            dotted = self._dotted(module, expr)
            if dotted in self.routers:
                return dotted
        return None

    def _dotted(self, module: Module, expr: ast.AST) -> str:
        """Like canonical, but keeps instances as names: 'orders.router' -> 'app.routes.orders.router'."""
        parts = []
        while isinstance(expr, ast.Attribute):
            parts.insert(0, expr.attr)
            expr = expr.value
        if not isinstance(expr, ast.Name):
            return ""
        head = expr.id
        if head in module.imports:
            base = module.imports[head]
            # 'from app.api.routes import items' then 'items.router'.
            if parts:
                return ".".join([base] + parts)
            head_mod, _, last = base.rpartition(".")
            owner = self.modules.get(head_mod)
            if owner is not None and last in owner.imports:
                return owner.imports[last]
            return base
        return ".".join([f"{module.name}.{head}"] + parts)

    def _index_includes(self, module: Module) -> None:
        for node in ast.walk(module.tree):
            if isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute) \
                    and node.func.attr in ("include_router", "register_blueprint") and node.args:
                child = self._router_key(module, node.args[0])
                parent = self._router_key(module, node.func.value)
                if child is None:
                    continue
                prefix = ""
                for kw in node.keywords:
                    if kw.arg in ("prefix", "url_prefix"):
                        prefix = self._string_of(module, kw.value)
                self.routers[child].includes.append((parent or "", prefix))

    def _index_routes(self, module: Module) -> None:
        for name, func in module.functions.items():
            for dec in func.decorator_list:
                if not (isinstance(dec, ast.Call) and isinstance(dec.func, ast.Attribute)):
                    continue
                router = self._router_key(module, dec.func.value)
                if router is None:
                    continue
                attr = dec.func.attr
                path = self._string_of(module, dec.args[0]) if dec.args else ""
                for kw in dec.keywords:
                    if kw.arg == "path":
                        path = self._string_of(module, kw.value)
                if attr in ROUTE_VERBS:
                    verbs = [attr.upper()]
                elif attr in ("route", "api_route"):
                    verbs = ["GET"]
                    for kw in dec.keywords:
                        if kw.arg == "methods" and isinstance(kw.value, (ast.List, ast.Tuple, ast.Set)):
                            verbs = [str(e.value).upper() for e in kw.value.elts if isinstance(e, ast.Constant)]
                else:
                    continue
                # One handler serving GET and POST is one box ("GET,POST /"): the body can't be split by verb,
                # so two boxes would each claim what only one of them does.
                for full in self._full_prefixes(router, set()):
                    self.routes.append(Route(",".join(verbs), _join(full, path), f"{module.name}.{name}", func, module))

    def _full_prefixes(self, router_key: str, seen: set[str]) -> list[str]:
        router = self.routers[router_key]
        if router_key in seen or not router.includes:
            return [router.prefix]
        result = []
        for parent, prefix in router.includes:
            parents = self._full_prefixes(parent, seen | {router_key}) if parent in self.routers else [""]
            result += [p + prefix + router.prefix for p in parents]
        return result

    def _instance_default(self, module: Module, name: str, attr: str) -> str | None:
        owner, call = module, module.instance_calls.get(name)
        if call is None and name in module.imports:
            mod, _, var = module.imports[name].rpartition(".")
            owner = self.modules.get(mod)
            call = owner.instance_calls.get(var) if owner else None
        if call is None:
            return None
        key = self.canonical(owner, call.func)
        info = self.modules.get(key.rpartition(".")[0])
        node = info.classes.get(key.rpartition(".")[2]) if info else None
        for item in node.body if node else []:
            target = item.target if isinstance(item, ast.AnnAssign) else item.targets[0] if isinstance(item, ast.Assign) else None
            value = getattr(item, "value", None)
            if isinstance(target, ast.Name) and target.id == attr and isinstance(value, ast.Constant) \
                    and isinstance(value.value, str):
                return value.value
        return None

    def _string_of(self, module: Module, expr: ast.AST) -> str:
        if isinstance(expr, ast.Constant) and isinstance(expr.value, str):
            return expr.value
        if isinstance(expr, ast.Name) and expr.id in module.constants:
            return module.constants[expr.id]
        # settings.API_V1_STR where settings = Settings() and the class has API_V1_STR: str = "/api/v1".
        if isinstance(expr, ast.Attribute) and isinstance(expr.value, ast.Name):
            value = self._instance_default(module, expr.value.id, expr.attr)
            if value is not None:
                return value
        if isinstance(expr, ast.JoinedStr):
            return "".join(v.value if isinstance(v, ast.Constant) else "{" + ast.unparse(v.value) + "}"
                           for v in expr.values)
        # settings.API_V1_STR: not knowable without running the app; keep the expression visible.
        return "{" + ast.unparse(expr) + "}"


def _join(*parts: str) -> str:
    path = "/".join(p.strip("/") for p in parts if p and p.strip("/"))
    return re.sub(r"/{2,}", "/", path) or "/"


def _snake(name: str) -> str:
    return re.sub(r"(?<!^)(?=[A-Z])", "_", name).lower()


def _matches_table(class_name: str, tables: set[str]) -> bool:
    for name in {class_name.lower(), _snake(class_name)}:
        if name in tables or name + "s" in tables or name + "es" in tables:
            return True
        if name.endswith("y") and name[:-1] + "ies" in tables:
            return True
    return False


def _is_abstract(func: ast.AST) -> bool:
    for dec in func.decorator_list:
        if (isinstance(dec, ast.Name) and dec.id in ("abstractmethod", "overload")) or \
                (isinstance(dec, ast.Attribute) and dec.attr in ("abstractmethod", "overload")):
            return True
    body = [s for s in func.body if not (isinstance(s, ast.Expr) and isinstance(s.value, ast.Constant)
                                          and isinstance(s.value.value, str))]
    if not body:
        return True
    if len(body) == 1:
        s = body[0]
        # "pass" is a real (empty) method; "..." is the stub spelling used by protocols and ABCs.
        if isinstance(s, ast.Expr) and isinstance(s.value, ast.Constant) and s.value.value is Ellipsis:
            return True
        if isinstance(s, ast.Raise) and s.exc is not None and "NotImplementedError" in ast.unparse(s.exc):
            return True
    return False


def _is_property(func: ast.AST) -> bool:
    return any((isinstance(d, ast.Name) and d.id in ("property", "cached_property"))
               or (isinstance(d, ast.Attribute) and d.attr in ("setter", "cached_property"))
               for d in func.decorator_list)


def _returns_value(func: ast.AST) -> bool:
    if getattr(func, "returns", None) is not None and not (
            isinstance(func.returns, ast.Constant) and func.returns.value is None):
        return True
    return any(isinstance(n, ast.Return) and n.value is not None for n in ast.walk(func))


def _dunder(name: str) -> bool:
    return name.startswith("__") and name.endswith("__")

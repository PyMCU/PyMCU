import ast
import re
import tomllib
from dataclasses import dataclass, field
from pathlib import Path


REPO = Path(__file__).resolve().parents[2]
STDLIB = REPO / "lib" / "src" / "pymcu"
HAL = STDLIB / "hal"
ALLOWLIST = Path(__file__).with_name("hal_parity_allowlist.toml")

LAYERS = {
    "circuitpython": Path("/Users/begeistert/Repos/pymcu-circuitpython/src"),
    "micropython": Path("/Users/begeistert/Repos/pymcu-micropython/src"),
}

ARCH_COLUMNS = ("avr", "pic12", "pic14", "pic18", "riscv", "rp2040", "rp2350")
CHIP_TO_COLUMN = {"rp2040": "rp2040", "rp2350": "rp2350"}
DECORATORS = {"inline", "outline"}

PORT_NAME = re.compile(r"^(?:P[A-Z][0-9]{1,2}|GP[0-9]{1,2}|GPIO[0-9]{1,2}|R[A-Z][0-9]{1,2})$")


@dataclass(frozen=True)
class Signature:
    params: tuple[tuple[str, str | None], ...]
    decorators: tuple[str, ...]
    lineno: int = field(compare=False)


@dataclass(frozen=True)
class ClassSurface:
    decorators: tuple[str, ...]
    constants: tuple[str, ...]
    methods: tuple[tuple[str, tuple[Signature, ...]], ...]
    lineno: int = field(compare=False)


@dataclass(frozen=True)
class ModuleSurface:
    functions: dict[str, tuple[Signature, ...]]
    classes: dict[str, ClassSurface]


@dataclass(frozen=True)
class Claim:
    peripheral: str
    column: str
    module: str
    symbol: str
    implementation: str
    facade_signature: Signature | ClassSurface | None = None


@dataclass(frozen=True)
class Deviation:
    symbol: str
    status: str
    reason: str
    quote: str = ""


@dataclass(frozen=True)
class ApiViolation:
    symbol: str
    detail: str


@dataclass(frozen=True)
class UniversalityViolation:
    symbol: str
    layer: str
    path: str
    line: int
    kinds: tuple[str, ...]
    detail: str


def parse_source(path: Path) -> ast.Module:
    return ast.parse(path.read_text(), filename=str(path))


def module_path(dotted: str) -> Path:
    rel = Path(*dotted.split(".")[1:])
    as_file = STDLIB / rel.with_suffix(".py")
    if as_file.exists():
        return as_file
    return STDLIB / rel / "__init__.py"


def _decorator_name(node: ast.expr) -> str:
    target = node.func if isinstance(node, ast.Call) else node
    if isinstance(target, ast.Name):
        return target.id
    if isinstance(target, ast.Attribute):
        return target.attr
    return ""


def decorators(node: ast.FunctionDef | ast.ClassDef) -> tuple[str, ...]:
    return tuple(sorted(d for d in (_decorator_name(d) for d in node.decorator_list)
                        if d in DECORATORS))


def signature(node: ast.FunctionDef) -> Signature:
    args = list(node.args.posonlyargs) + list(node.args.args)
    defaults: list[str | None] = [None] * (len(args) - len(node.args.defaults))
    defaults += [ast.dump(d, include_attributes=False) for d in node.args.defaults]
    params = tuple((arg.arg, default) for arg, default in zip(args, defaults))
    return Signature(params=params, decorators=decorators(node), lineno=node.lineno)


def _constant_name(node: ast.stmt) -> str | None:
    if isinstance(node, ast.Assign) and len(node.targets) == 1:
        target = node.targets[0]
    elif isinstance(node, ast.AnnAssign):
        target = node.target
    else:
        return None
    if isinstance(target, ast.Name) and not target.id.startswith("_"):
        return target.id
    return None


def module_surface(path: Path) -> ModuleSurface:
    functions: dict[str, list[Signature]] = {}
    classes: dict[str, ClassSurface] = {}
    for node in parse_source(path).body:
        if isinstance(node, ast.FunctionDef) and not node.name.startswith("_"):
            functions.setdefault(node.name, []).append(signature(node))
        elif isinstance(node, ast.ClassDef) and not node.name.startswith("_"):
            methods: dict[str, list[Signature]] = {}
            constants = []
            for item in node.body:
                if isinstance(item, ast.FunctionDef) and not item.name.startswith("_"):
                    methods.setdefault(item.name, []).append(signature(item))
                elif isinstance(item, ast.FunctionDef) and item.name in {"__init__", "__enter__", "__exit__"}:
                    methods.setdefault(item.name, []).append(signature(item))
                else:
                    name = _constant_name(item)
                    if name:
                        constants.append(name)
            classes[node.name] = ClassSurface(
                decorators=decorators(node),
                constants=tuple(sorted(constants)),
                methods=tuple((k, tuple(v)) for k, v in sorted(methods.items())),
                lineno=node.lineno,
            )
    return ModuleSurface(
        functions={k: tuple(v) for k, v in functions.items()},
        classes=classes,
    )


def _condition_columns(node: ast.expr | None) -> tuple[str, ...]:
    if node is None:
        return ()
    if isinstance(node, ast.BoolOp) and isinstance(node.op, ast.Or):
        cols = []
        for value in node.values:
            cols.extend(_condition_columns(value))
        return tuple(dict.fromkeys(cols))
    if isinstance(node, ast.Compare) and len(node.ops) == 1 and len(node.comparators) == 1:
        left = node.left
        right = node.comparators[0]
        if isinstance(node.ops[0], ast.Eq) and isinstance(right, ast.Constant) and isinstance(right.value, str):
            if isinstance(left, ast.Attribute) and isinstance(left.value, ast.Name) and left.value.id == "__CHIP__":
                if left.attr == "arch" and right.value in ARCH_COLUMNS:
                    return (right.value,)
                if left.attr == "name" and right.value in CHIP_TO_COLUMN:
                    return (CHIP_TO_COLUMN[right.value],)
    return ()


def _imports_from_body(body: list[ast.stmt], columns: tuple[str, ...]) -> list[Claim]:
    claims = []
    for node in body:
        if isinstance(node, ast.ImportFrom) and node.module and node.module.startswith("pymcu.hal."):
            for alias in node.names:
                public = alias.asname or alias.name
                if public.startswith("_"):
                    continue
                for column in columns:
                    claims.append((column, node.module, public, alias.name))
        elif isinstance(node, ast.If):
            # A nested `if` either narrows which columns its body applies to
            # (its test itself names an arch/chip, e.g. a board check inside
            # an arch branch) or it does not (e.g. a board-name guard, which
            # is orthogonal to architecture); in the latter case the body is
            # still reached for every column already established by the
            # enclosing branch, so it must not be dropped.
            nested = _condition_columns(node.test)
            if nested:
                claims.extend(_imports_from_body(node.body, nested))
            else:
                claims.extend(_imports_from_body(node.body, columns))
            claims.extend(_imports_from_body(node.orelse, columns))
    return claims


def _call_names(node: ast.AST) -> set[str]:
    names = set()
    for item in ast.walk(node):
        if isinstance(item, ast.Call):
            func = item.func
            if isinstance(func, ast.Name):
                names.add(func.id)
            elif isinstance(func, ast.Attribute):
                names.add(func.attr)
    return names


def facade_claims() -> list[Claim]:
    claims: list[Claim] = []
    for path in sorted(HAL.glob("*.py")):
        if path.name in {"__init__.py", "cyw43_regs.py"}:
            continue
        peripheral = path.stem
        tree = parse_source(path)
        imports: dict[str, tuple[str, str, tuple[str, ...]]] = {}
        for node in tree.body:
            columns = _condition_columns(node.test) if isinstance(node, ast.If) else ()
            if isinstance(node, ast.If) and columns:
                for column, module, public, implementation in _imports_from_body([node], columns):
                    imports[public] = (module, implementation, (column,))
                    claims.append(Claim(peripheral, column, module, public, implementation))
        for node in tree.body:
            if isinstance(node, ast.FunctionDef) and not node.name.startswith("_"):
                facade_sig = signature(node)
                for called in _call_names(node):
                    if called in imports:
                        module, implementation, columns = imports[called]
                        for column in columns:
                            claims.append(Claim(peripheral, column, module, node.name,
                                                implementation, facade_sig))
            elif isinstance(node, ast.ClassDef) and not node.name.startswith("_"):
                surface = module_surface(path).classes[node.name]
                direct = imports.get(node.name)
                if direct:
                    module, implementation, columns = direct
                    for column in columns:
                        claims.append(Claim(peripheral, column, module, node.name,
                                            implementation, surface))
    seen = {}
    for claim in claims:
        key = (claim.peripheral, claim.column, claim.module, claim.symbol, claim.implementation)
        seen[key] = claim
    return list(seen.values())


def _signature_text(sig: Signature) -> str:
    params = []
    for name, default in sig.params:
        params.append(name if default is None else f"{name}={default}")
    deco = f" @{','.join(sig.decorators)}" if sig.decorators else ""
    return f"({', '.join(params)}){deco}"


def _surface_for_claim(claim: Claim) -> Signature | ClassSurface | None:
    surface = module_surface(module_path(claim.module))
    if claim.implementation in surface.functions:
        sigs = surface.functions[claim.implementation]
        return sigs[0] if len(sigs) == 1 else None
    return surface.classes.get(claim.implementation)


def _method_map(surface: ClassSurface) -> dict[str, tuple[Signature, ...]]:
    return dict(surface.methods)


def _class_violations(symbol: str, baseline: ClassSurface, claim: Claim,
                      current: ClassSurface) -> list[ApiViolation]:
    violations = []
    if baseline.decorators != current.decorators:
        violations.append(ApiViolation(
            f"{symbol}.__class__",
            f"{claim.column}:{claim.module}.{claim.implementation} decorators "
            f"{current.decorators} differ from {baseline.decorators}",
        ))
    if baseline.constants != current.constants:
        missing = sorted(set(baseline.constants) - set(current.constants))
        extra = sorted(set(current.constants) - set(baseline.constants))
        bits = []
        if missing:
            bits.append("missing " + ", ".join(missing))
        if extra:
            bits.append("extra " + ", ".join(extra))
        violations.append(ApiViolation(
            symbol,
            f"{claim.column}:{claim.module}.{claim.implementation} constants differ: "
            + "; ".join(bits),
        ))
    base_methods = _method_map(baseline)
    cur_methods = _method_map(current)
    for name in sorted(set(base_methods) | set(cur_methods)):
        if name not in cur_methods:
            violations.append(ApiViolation(
                f"{symbol}.{name}",
                f"{claim.column}:{claim.module}.{claim.implementation} is missing method {name}",
            ))
        elif name not in base_methods:
            violations.append(ApiViolation(
                f"{symbol}.{name}",
                f"{claim.column}:{claim.module}.{claim.implementation} adds method {name}",
            ))
        elif base_methods[name] != cur_methods[name]:
            want = ", ".join(_signature_text(s) for s in base_methods[name])
            got = ", ".join(_signature_text(s) for s in cur_methods[name])
            violations.append(ApiViolation(
                f"{symbol}.{name}",
                f"{claim.column}:{claim.module}.{claim.implementation}.{name} "
                f"signature/decorators {got} differ from {want}",
            ))
    return violations


def api_violations() -> list[ApiViolation]:
    claims = facade_claims()
    by_symbol: dict[str, list[tuple[Claim, Signature | ClassSurface | None]]] = {}
    for claim in claims:
        if claim.facade_signature is not None:
            baseline = claim.facade_signature
        else:
            baseline = _surface_for_claim(claim)
        by_symbol.setdefault(f"{claim.peripheral}.{claim.symbol}", []).append((claim, baseline))

    violations = []
    for symbol, entries in sorted(by_symbol.items()):
        baseline = next((surface for _, surface in entries if surface is not None), None)
        if baseline is None:
            continue
        for claim, current in entries:
            if current is None:
                violations.append(ApiViolation(
                    symbol,
                    f"{claim.column}:{claim.module}.{claim.implementation} is not a public function/class",
                ))
                continue
            if isinstance(baseline, Signature) and isinstance(current, Signature):
                if baseline != current:
                    violations.append(ApiViolation(
                        symbol,
                        f"{claim.column}:{claim.module}.{claim.implementation} "
                        f"{_signature_text(current)} differs from {_signature_text(baseline)}",
                    ))
            elif isinstance(baseline, ClassSurface) and isinstance(current, ClassSurface):
                violations.extend(_class_violations(symbol, baseline, claim, current))
            else:
                violations.append(ApiViolation(
                    symbol,
                    f"{claim.column}:{claim.module}.{claim.implementation} changes kind",
                ))
    return violations


def allowlist() -> dict[str, Deviation]:
    if not ALLOWLIST.exists():
        return {}
    data = tomllib.loads(ALLOWLIST.read_text())
    return {
        item["symbol"]: Deviation(
            symbol=item["symbol"],
            status=item["status"],
            reason=item.get("reason", ""),
            quote=item.get("quote", ""),
        )
        for item in data.get("deviation", [])
    }


def _register_names() -> set[str]:
    names = set()
    for path in (STDLIB / "chips").glob("*.py"):
        tree = parse_source(path)
        for node in tree.body:
            name = _constant_name(node)
            if name and name.isupper() and not name.startswith("__"):
                text = ast.dump(getattr(node, "value", ast.Constant(None)), include_attributes=False)
                if "ptr" in text or re.search(
                        r"(REG|PORT|DDR|PIN|TCCR|UCSR|UDR|ADMUX|ADCS|GPIO|SIO|IO_BANK|"
                        r"PADS|RESET|PWM|UART|SPI|I2C|TIMER|TIMSK|OCR|TCNT|WDT|EE|"
                        r"INTCON|TRIS|LAT|ANSEL|CMCON|OPTION)", name):
                    names.add(name)
    return names


def universality_violations() -> list[UniversalityViolation]:
    registers = _register_names()
    violations = []
    for layer, root in LAYERS.items():
        if not root.exists():
            continue
        for path in sorted(root.rglob("*.py")):
            if "boards" in path.relative_to(root).parts:
                # board.D5 mapping to PD5 on the Uno IS the design: a board
                # module's whole job is naming a chip's pins for a specific
                # piece of hardware. The universality rule is about the
                # layer's own modules (digitalio, busio, pwmio, ...), not
                # about the board pin tables they are handed.
                continue
            tree = parse_source(path)
            by_line: dict[int, list[tuple[str, str]]] = {}

            def add(node: ast.AST, kind: str, detail: str) -> None:
                by_line.setdefault(node.lineno, []).append((kind, detail))

            for node in ast.walk(tree):
                if isinstance(node, ast.ImportFrom) and node.module and node.module.startswith("pymcu.chips"):
                    add(node, "pymcu.chips import", node.module)
                elif isinstance(node, ast.Import):
                    for alias in node.names:
                        if alias.name.startswith("pymcu.chips"):
                            add(node, "pymcu.chips import", alias.name)
                elif isinstance(node, ast.Name) and node.id == "__CHIP__":
                    add(node, "__CHIP__ conditional", "__CHIP__")
                elif isinstance(node, ast.Name) and node.id in registers:
                    add(node, "chip register", node.id)
                elif isinstance(node, ast.Constant) and isinstance(node.value, str) and PORT_NAME.match(node.value):
                    add(node, "port name", node.value)

            rel = path.relative_to(root).as_posix()
            for line, items in sorted(by_line.items()):
                kinds = tuple(sorted({kind for kind, _ in items}))
                detail = ", ".join(sorted({detail for _, detail in items}))
                violations.append(UniversalityViolation(
                    symbol=f"compat.{layer}.{rel}:{line}",
                    layer=layer,
                    path=rel,
                    line=line,
                    kinds=kinds,
                    detail=detail,
                ))
    return violations


def matrix() -> dict[str, dict[str, str]]:
    claims = facade_claims()
    claimed: dict[str, set[str]] = {}
    partial: dict[str, set[str]] = {}
    for claim in claims:
        claimed.setdefault(claim.peripheral, set()).add(claim.column)
    for violation in api_violations():
        peripheral = violation.symbol.split(".", 1)[0]
        for claim in claims:
            if claim.peripheral == peripheral and claim.column in violation.detail:
                partial.setdefault(peripheral, set()).add(claim.column)
    result = {}
    for peripheral in sorted(claimed):
        row = {}
        for column in ARCH_COLUMNS:
            if column in partial.get(peripheral, set()):
                row[column] = "partial"
            elif column in claimed.get(peripheral, set()):
                row[column] = "provided"
            else:
                row[column] = "missing"
        result[peripheral] = row
    return result


def tracked_issue_numbers() -> set[int]:
    numbers = set()
    for item in allowlist().values():
        match = re.fullmatch(r"tracked:#(\d+)", item.status)
        if match:
            numbers.add(int(match.group(1)))
    return numbers

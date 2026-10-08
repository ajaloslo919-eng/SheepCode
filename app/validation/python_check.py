"""Trusted SheepCode worker. Input is JSON data; candidate code is never executed."""
import ast
import json
import sys
import warnings


def calls(nodes, name):
    return sum(isinstance(n, ast.Call) and isinstance(n.func, ast.Name)
               and n.func.id == name for n in nodes)


def inspect(request):
    source = request["content"]
    path = request["path"]
    facts = {}
    issues = []
    try:
        with warnings.catch_warnings(record=True) as caught:
            warnings.simplefilter("always", SyntaxWarning)
            tree = ast.parse(source, filename=path, mode="exec")
            # AST parsing alone accepts e.g. return/break outside their scopes.
            # Compilation checks them, but its code object is never run or saved.
            compile(tree, path, "exec", dont_inherit=True)
        for warning in caught:
            issues.append(dict(code="python_warning", message=str(warning.message),
                               line=warning.lineno, column=0, severity="warning"))
        nodes = list(ast.walk(tree))
        loops = [n for n in nodes if isinstance(n, (ast.While, ast.For, ast.AsyncFor))]
        for name, kinds in (("while", (ast.While,)), ("for", (ast.For, ast.AsyncFor))):
            selected = [n for n in loops if isinstance(n, kinds) and not (
                isinstance(n, ast.While) and isinstance(n.test, ast.Constant) and not n.test.value)]
            facts[name] = len(selected)
            body = [n for loop in selected for statement in loop.body for n in ast.walk(statement)]
            facts[name + "_input"] = calls(body, "input")
            facts[name + "_print"] = calls(body, "print")
        facts["input"] = calls(nodes, "input")
        facts["print"] = calls(nodes, "print")
        for loop in (n for n in loops if isinstance(n, ast.While)):
            body = [n for statement in loop.body for n in ast.walk(statement)]
            tested = {n.id for n in ast.walk(loop.test) if isinstance(n, ast.Name)}
            assigned = {n.id for n in body if isinstance(n, ast.Name) and isinstance(n.ctx, ast.Store)}
            exits = any(isinstance(n, (ast.Break, ast.Return, ast.Raise, ast.Await, ast.Yield)) for n in body)
            opaque_calls = any(isinstance(n, ast.Call) and not (
                isinstance(n.func, ast.Name) and n.func.id in ("input", "print", "int", "str", "float", "len", "range")) for n in body)
            # Only flag a simple, side-effect-free comparison with an unchanged
            # counter. Complex termination cannot be proved by static inspection.
            if isinstance(loop.test, ast.Compare) and tested and not (tested & assigned) and not exits and not opaque_calls:
                issues.append(dict(code="unchanged_counter", message="El while compara variables que su bloque no actualiza y no tiene salida explícita.",
                                   line=loop.lineno, column=loop.col_offset + 1, severity="warning"))
        # Repeating a complete interactive program is usually a failed edit.
        dumps = [ast.dump(n, include_attributes=False) for n in tree.body]
        for size in range(3, len(dumps) // 2 + 1):
            if len(dumps) == 2 * size and dumps[:size] == dumps[size:]:
                if calls([n for s in tree.body[:size] for n in ast.walk(s)], "input"):
                    issues.append(dict(code="duplicate_program", message="El programa interactivo completo aparece duplicado consecutivamente.",
                                       line=tree.body[size].lineno, column=1, severity="warning"))
                break
        return dict(parser="CPython " + sys.version.split()[0] + " AST + compile", syntax=True,
                    facts=facts, diagnostics=issues)
    except (SyntaxError, ValueError, RecursionError, MemoryError) as error:
        return dict(parser="CPython " + sys.version.split()[0] + " AST + compile", syntax=False, facts={},
                    diagnostics=[dict(code="python_syntax", message=str(error),
                                      line=getattr(error, "lineno", 0) or 0,
                                      column=getattr(error, "offset", 0) or 0, severity="error")])


if __name__ == "__main__":
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
    data = sys.stdin.read(2000001)
    if len(data) > 2000000:
        raise ValueError("Validation input exceeds the trusted worker limit")
    print(json.dumps(inspect(json.loads(data)), ensure_ascii=True))

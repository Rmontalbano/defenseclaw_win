#!/usr/bin/env python3
"""Generate DefenseClaw.Core/Cli/TuiRegistryData.Generated.cs from DefenseClaw's TUI command registry.

The command palette offers every entry of the DefenseClaw TUI's own command registry (``defenseclaw/tui/registry_data.py``,
the tuples the TUI's palette is built from: name, binary, argv, description, category, needs-argument flag, hint). The TUI
registry differs between runtimes, so there is one catalogue per runtime, checked in as C# and chosen at run time from the
capabilities the app probed (``RuntimeCapability.TuiRegistry``; see ``TuiRegistryCatalogues.For``):

  Baseline   the installed DefenseClaw 0.8.10                       (231 entries)
  Extended   the pinned newer source commit (reports version 1.0.0) (253 entries)

Nothing of DefenseClaw is imported or executed. Every file is parsed with ``ast`` and only literals are evaluated (the registry
tuple, the Windows platform table, the module-level string constants); a file that is not shaped as expected stops the run
instead of producing a guess.

Which entries are not offered on Windows. Each runtime's own ``build_registry`` decides what its TUI lists per operating
system (in the pinned source: ``setup local-observability`` where the controller is unsupported, ``sandbox`` where OpenShell
sandboxes are, and ``setup <connector>`` where the connector is neither supported nor preview on that OS). The installed 0.8.10
does not filter, but its CLI refuses the same things on Windows (``setup <connector>`` for a connector that is not supported or
preview, and ``sandbox`` - ``WINDOWS_UNSUPPORTED_FEATURES``), so the same three rules are applied to each runtime's own
platform table. An entry that fails one of them is still in the catalogue, with the runtime's own reason, and the palette
hides it and says how many it hid.

Usage (run it with ``-I`` so the interpreter reads nothing from the environment):

  python -I tools/gen-tui-registry.py ^
      --baseline <site-packages>/defenseclaw/tui/registry_data.py ^
      --pinned   <checkout>/cli/defenseclaw/tui/registry_data.py --pinned-commit 95159fd

  --out PATH   where to write (default: DefenseClaw.Core/Cli/TuiRegistryData.Generated.cs next to this script's repository)
  --check      write nothing; exit 1 when PATH differs from what would be generated (line endings are ignored)

Python 3.9 or later, standard library only.
"""

from __future__ import annotations

import argparse
import ast
import hashlib
import pathlib
import re
import sys

BINARIES = {"defenseclaw": "Cli", "defenseclaw-gateway": "Gateway"}
SANDBOX_REASON = "Sandboxes run on Linux and macOS only."
UNKNOWN_CONNECTOR_REASON = "This connector has not completed native Windows x64 certification."
AVAILABLE_STATUSES = {"supported", "preview"}
DEFAULT_OUT = pathlib.Path(__file__).resolve().parent.parent / "DefenseClaw.Core" / "Cli" / "TuiRegistryData.Generated.cs"


class GeneratorError(Exception):
    """An input is not shaped the way this generator was written for."""


# ---------------------------------------------------------------------------------------------------------------------
# Reading the sources as data
# ---------------------------------------------------------------------------------------------------------------------

def read_text(path: pathlib.Path) -> str:
    try:
        return path.read_bytes().decode("utf-8").replace("\r\n", "\n")
    except OSError as exc:
        raise GeneratorError(f"cannot read {path}: {exc}") from exc


def parse(path: pathlib.Path) -> ast.Module:
    try:
        return ast.parse(read_text(path), filename=str(path))
    except SyntaxError as exc:
        raise GeneratorError(f"{path} is not valid Python: {exc}") from exc


def top_level_assignment(tree: ast.Module, name: str) -> ast.expr | None:
    for node in tree.body:
        if isinstance(node, ast.AnnAssign) and isinstance(node.target, ast.Name) and node.target.id == name and node.value is not None:
            return node.value
        if isinstance(node, ast.Assign) and len(node.targets) == 1 and isinstance(node.targets[0], ast.Name) and node.targets[0].id == name:
            return node.value
    return None


def string_constants(tree: ast.Module) -> dict[str, str]:
    """Module-level ``NAME = "text"`` assignments (adjacent literals are already one constant)."""

    found: dict[str, str] = {}
    for node in tree.body:
        value = None
        target = None
        if isinstance(node, ast.AnnAssign) and isinstance(node.target, ast.Name):
            target, value = node.target.id, node.value
        elif isinstance(node, ast.Assign) and len(node.targets) == 1 and isinstance(node.targets[0], ast.Name):
            target, value = node.targets[0].id, node.value
        if target is not None and isinstance(value, ast.Constant) and isinstance(value.value, str):
            found[target] = value.value
    return found


def resolve_string(node: ast.expr, constants: dict[str, str], what: str) -> str:
    if isinstance(node, ast.Constant) and isinstance(node.value, str):
        return node.value
    if isinstance(node, ast.Name) and node.id in constants:
        return constants[node.id]
    raise GeneratorError(f"{what}: expected a string literal or a module-level string constant, found {ast.dump(node)[:80]}")


def package_version(package: pathlib.Path) -> str:
    value = top_level_assignment(parse(package / "__init__.py"), "__version__")
    if not isinstance(value, ast.Constant) or not isinstance(value.value, str):
        raise GeneratorError(f"{package / '__init__.py'}: __version__ is not a string literal")
    return value.value


def read_registry(path: pathlib.Path) -> list[tuple]:
    node = top_level_assignment(parse(path), "GO_PARITY_REGISTRY")
    if node is None:
        raise GeneratorError(f"{path}: no GO_PARITY_REGISTRY assignment")
    try:
        rows = ast.literal_eval(node)
    except ValueError as exc:
        raise GeneratorError(f"{path}: GO_PARITY_REGISTRY is not a plain literal: {exc}") from exc

    if not isinstance(rows, tuple) or not rows:
        raise GeneratorError(f"{path}: GO_PARITY_REGISTRY is not a non-empty tuple")

    names: set[str] = set()
    for row in rows:
        if not (isinstance(row, tuple) and len(row) == 7):
            raise GeneratorError(f"{path}: an entry is not a 7-tuple: {row!r}")
        name, binary, argv, description, category, needs_arg, hint = row
        if not (isinstance(name, str) and name and isinstance(description, str) and isinstance(category, str) and category):
            raise GeneratorError(f"{path}: malformed entry {row!r}")
        if binary not in BINARIES:
            raise GeneratorError(f"{path}: entry '{name}' names an unknown binary {binary!r}")
        if not (isinstance(argv, tuple) and argv and all(isinstance(a, str) and a for a in argv)):
            # An empty argv for the gateway would be a bare `defenseclaw-gateway`, which starts a second daemon.
            raise GeneratorError(f"{path}: entry '{name}' has no argv (or an empty argument)")
        if not (isinstance(needs_arg, bool) and isinstance(hint, str)):
            raise GeneratorError(f"{path}: entry '{name}' has a malformed needs-argument flag or hint")
        if needs_arg != bool(hint):
            raise GeneratorError(f"{path}: entry '{name}': the needs-argument flag and the hint disagree")
        if name in names:
            raise GeneratorError(f"{path}: duplicate entry name '{name}'")
        names.add(name)
    return list(rows)


class Platform:
    """What a runtime's own platform_support.py says about Windows."""

    def __init__(self, connectors, unsupported_features, local_observability_oses, sandbox_oses, local_observability_reason):
        self.connectors = connectors                                  # connector -> (status, reason)
        self.unsupported_features = unsupported_features              # set of feature names
        self.local_observability_oses = local_observability_oses      # set of OS tokens
        self.sandbox_oses = sandbox_oses                              # set of OS tokens, or None when the file has no such function
        self.local_observability_reason = local_observability_reason

    @property
    def sandbox_on_windows(self) -> bool:
        return "sandbox" not in self.unsupported_features and (self.sandbox_oses is None or "windows" in self.sandbox_oses)


def os_set_of(tree: ast.Module, function: str, required: bool, path: pathlib.Path) -> set[str] | None:
    """The OS tokens ``return resolved_os in {...}`` of a platform predicate names."""

    for node in tree.body:
        if isinstance(node, ast.FunctionDef) and node.name == function:
            for statement in ast.walk(node):
                if (
                    isinstance(statement, ast.Return)
                    and isinstance(statement.value, ast.Compare)
                    and len(statement.value.ops) == 1
                    and isinstance(statement.value.ops[0], ast.In)
                    and isinstance(statement.value.comparators[0], ast.Set)
                ):
                    return set(ast.literal_eval(statement.value.comparators[0]))
            raise GeneratorError(f"{path}: {function} no longer ends in `return os in {{...}}`; update the generator")
    if required:
        raise GeneratorError(f"{path}: no {function} function")
    return None


def read_platform(path: pathlib.Path) -> Platform:
    tree = parse(path)
    constants = string_constants(tree)

    table = top_level_assignment(tree, "WINDOWS_CONNECTOR_SUPPORT")
    if not isinstance(table, ast.Dict):
        raise GeneratorError(f"{path}: WINDOWS_CONNECTOR_SUPPORT is not a dict literal")
    connectors: dict[str, tuple[str, str]] = {}
    for key, value in zip(table.keys, table.values):
        if not (isinstance(key, ast.Constant) and isinstance(key.value, str)):
            raise GeneratorError(f"{path}: a WINDOWS_CONNECTOR_SUPPORT key is not a string")
        if not (isinstance(value, ast.Call) and isinstance(value.func, ast.Name) and value.func.id == "ConnectorPlatformSupport" and len(value.args) == 2 and not value.keywords):
            raise GeneratorError(f"{path}: WINDOWS_CONNECTOR_SUPPORT[{key.value!r}] is not ConnectorPlatformSupport(status, reason)")
        connectors[key.value] = (
            resolve_string(value.args[0], constants, f"{key.value} status"),
            resolve_string(value.args[1], constants, f"{key.value} reason"),
        )

    features_node = top_level_assignment(tree, "WINDOWS_UNSUPPORTED_FEATURES")
    if not (isinstance(features_node, ast.Call) and isinstance(features_node.func, ast.Name) and features_node.func.id == "frozenset" and len(features_node.args) == 1):
        raise GeneratorError(f"{path}: WINDOWS_UNSUPPORTED_FEATURES is not frozenset({{...}})")
    features = set(ast.literal_eval(features_node.args[0]))

    reason = constants.get("LOCAL_OBSERVABILITY_UNSUPPORTED_REASON")
    if reason is None:
        raise GeneratorError(f"{path}: no LOCAL_OBSERVABILITY_UNSUPPORTED_REASON constant")

    return Platform(
        connectors,
        features,
        os_set_of(tree, "local_observability_stack_supported", True, path),
        os_set_of(tree, "openshell_sandboxes_supported", False, path),
        reason,
    )


def read_setup_aliases(path: pathlib.Path) -> dict[str, str] | None:
    """The pinned registry.py maps ``setup <word>`` to a connector name; the installed 0.8.10 has no such table."""

    value = top_level_assignment(parse(path), "_SETUP_CONNECTOR_ALIASES")
    if value is None:
        return None
    try:
        aliases = ast.literal_eval(value)
    except ValueError as exc:
        raise GeneratorError(f"{path}: _SETUP_CONNECTOR_ALIASES is not a plain literal: {exc}") from exc
    if not (isinstance(aliases, dict) and all(isinstance(k, str) and isinstance(v, str) for k, v in aliases.items())):
        raise GeneratorError(f"{path}: _SETUP_CONNECTOR_ALIASES is not a str -> str dict")
    return aliases


# ---------------------------------------------------------------------------------------------------------------------
# Windows availability
# ---------------------------------------------------------------------------------------------------------------------

def setup_connector(word: str, aliases: dict[str, str] | None, platform: Platform) -> str | None:
    """The connector a ``setup <word>`` command configures, or None when it is not a connector command."""

    if aliases is not None:
        return aliases.get(word)
    # No alias table: the connector's name is the command word without its hyphens (claude-code -> claudecode).
    plain = word.replace("-", "")
    return plain if plain in platform.connectors else None


def windows_unavailable(argv: tuple[str, ...], platform: Platform, aliases: dict[str, str] | None) -> str | None:
    """Why the runtime's own platform table says Windows cannot run ``argv`` (None: it can)."""

    if argv[:2] == ("setup", "local-observability") and "windows" not in platform.local_observability_oses:
        return platform.local_observability_reason
    if argv[:1] == ("sandbox",) and not platform.sandbox_on_windows:
        return SANDBOX_REASON
    if len(argv) >= 2 and argv[0] == "setup":
        connector = setup_connector(argv[1], aliases, platform)
        if connector is not None:
            status, reason = platform.connectors.get(connector, ("not_certified", UNKNOWN_CONNECTOR_REASON))
            if status not in AVAILABLE_STATUSES:
                return reason
    return None


class Source:
    """One runtime's registry, with everything the header records about where it came from."""

    def __init__(self, registry_path: pathlib.Path, label: str, display_path: str):
        package = registry_path.resolve().parent.parent
        if package.name != "defenseclaw" or registry_path.name != "registry_data.py" or registry_path.parent.name != "tui":
            raise GeneratorError(f"{registry_path}: expected .../defenseclaw/tui/registry_data.py")
        self.version = package_version(package)
        self.label = label.format(version=self.version)
        self.display_path = display_path
        self.sha256 = hashlib.sha256(read_text(registry_path).encode("utf-8")).hexdigest()
        self.entries = read_registry(registry_path)
        platform = read_platform(package / "platform_support.py")
        aliases = read_setup_aliases(package / "tui" / "registry.py")
        self.rows = [(row, windows_unavailable(row[2], platform, aliases)) for row in self.entries]

        # Where a runtime has no alias table, the hyphen rule stands in for it. Where it has one, the two must agree on every
        # `setup <word>` the registry lists, or the stand-in is not a faithful reading of the table.
        if aliases is not None:
            for row in self.entries:
                if len(row[2]) >= 2 and row[2][0] == "setup" and setup_connector(row[2][1], aliases, platform) != setup_connector(row[2][1], None, platform):
                    raise GeneratorError(f"{registry_path}: the alias table and the hyphen rule disagree about 'setup {row[2][1]}'")

    @property
    def windows_count(self) -> int:
        return sum(1 for _, reason in self.rows if reason is None)


# ---------------------------------------------------------------------------------------------------------------------
# Writing the C#
# ---------------------------------------------------------------------------------------------------------------------

def cs_string(text: str) -> str:
    """A C# string literal that is pure ASCII, whatever the text holds."""

    out = ['"']
    for ch in text:
        code = ord(ch)
        if ch == '"':
            out.append('\\"')
        elif ch == "\\":
            out.append("\\\\")
        elif 0x20 <= code < 0x7F:
            out.append(ch)
        elif code <= 0xFFFF:
            out.append(f"\\u{code:04x}")
        else:
            out.append(f"\\U{code:08x}")
    out.append('"')
    return "".join(out)


def cs_entry(row: tuple, unavailable: str | None) -> str:
    name, binary, argv, description, category, needs_arg, hint = row
    argv_text = ", ".join(cs_string(a) for a in argv)
    return (
        f"        new({cs_string(name)}, TuiBinary.{BINARIES[binary]}, [{argv_text}], {cs_string(description)}, "
        f"{cs_string(category)}, {'true' if needs_arg else 'false'}, {cs_string(hint)}, "
        f"{'null' if unavailable is None else cs_string(unavailable)}),"
    )


def render(baseline: Source, extended: Source, pinned_commit: str) -> str:
    lines: list[str] = []
    add = lines.append

    add("// <auto-generated>")
    add("//   Generated by tools/gen-tui-registry.py from DefenseClaw's TUI command registry. Do not edit by hand: change the")
    add("//   generator or its inputs and run it again (the script's header says how; --check compares without writing).")
    add("//")
    add(f"//   Baseline  {baseline.label}")
    add(f"//             {baseline.display_path}")
    add(f"//             sha256 {baseline.sha256} (line endings as LF)")
    add(f"//             {len(baseline.entries)} entries, {baseline.windows_count} offered on Windows, {len(baseline.entries) - baseline.windows_count} hidden")
    add(f"//   Extended  {extended.label}")
    add(f"//             {extended.display_path}")
    add(f"//             sha256 {extended.sha256} (line endings as LF)")
    add(f"//             {len(extended.entries)} entries, {extended.windows_count} offered on Windows, {len(extended.entries) - extended.windows_count} hidden")
    add("//")
    add("//   Only the registry tuples and each runtime's Windows platform table were read, as data (ast); no DefenseClaw code was")
    add("//   imported or run. The entries are DefenseClaw's (Apache License 2.0, Cisco Systems, Inc. and its affiliates); see NOTICE.")
    add("// </auto-generated>")
    add("")
    add("#nullable enable")
    add("")
    add("namespace DefenseClaw.Core.Cli;")
    add("")
    add("/// <summary>The TUI command registry of each runtime the app knows, as generated data. Read it through <see cref=\"TuiRegistryCatalogues\"/>.</summary>")
    add("internal static class TuiRegistryData")
    add("{")
    for prefix, source in (("Baseline", baseline), ("Extended", extended)):
        add(f"    public const string {prefix}Runtime = {cs_string(source.label)};")
        add(f"    public const string {prefix}Source = {cs_string(source.display_path)};")
        add(f"    public const string {prefix}SourceSha256 = {cs_string(source.sha256)};")
        add(f"    public const int {prefix}SourceEntries = {len(source.entries)};")
        add("")
    add(f"    public const string ExtendedCommit = {cs_string(pinned_commit)};")
    add("")
    for prefix, source in (("Baseline", baseline), ("Extended", extended)):
        add(f"    public static readonly TuiRegistryEntry[] {prefix} =")
        add("    [")
        for row, unavailable in source.rows:
            add(cs_entry(row, unavailable))
        add("    ];")
        if prefix == "Baseline":
            add("")
    add("}")
    add("")
    return "\n".join(lines)


# ---------------------------------------------------------------------------------------------------------------------

def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Generate the TUI registry catalogues (see the module docstring).")
    parser.add_argument("--baseline", required=True, type=pathlib.Path, help="defenseclaw/tui/registry_data.py of the installed 0.8.10")
    parser.add_argument("--pinned", required=True, type=pathlib.Path, help="cli/defenseclaw/tui/registry_data.py of the pinned source checkout")
    parser.add_argument("--pinned-commit", required=True, help="the pinned source's commit (7 to 40 hex digits)")
    parser.add_argument("--pinned-subdir", default="cli", help="where the package sits in that repository (default: cli)")
    parser.add_argument("--out", type=pathlib.Path, default=DEFAULT_OUT)
    parser.add_argument("--check", action="store_true", help="write nothing; exit 1 when --out differs")
    args = parser.parse_args(argv)

    if not re.fullmatch(r"[0-9a-f]{7,40}", args.pinned_commit):
        parser.error("--pinned-commit must be 7 to 40 lower-case hex digits")

    try:
        baseline = Source(args.baseline, "DefenseClaw {version}, the installed Windows build", "defenseclaw/tui/registry_data.py")
        extended = Source(
            args.pinned,
            f"DefenseClaw source commit {args.pinned_commit} (reports version {{version}})",
            f"{args.pinned_subdir}/defenseclaw/tui/registry_data.py",
        )
    except GeneratorError as exc:
        print(f"gen-tui-registry: {exc}", file=sys.stderr)
        return 2

    text = render(baseline, extended, args.pinned_commit)

    if args.check:
        current = args.out.read_bytes().decode("utf-8").replace("\r\n", "\n") if args.out.exists() else ""
        if current != text:
            print(f"gen-tui-registry: {args.out} is out of date; run the generator again.", file=sys.stderr)
            return 1
        print(f"gen-tui-registry: {args.out} is up to date.")
        return 0

    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_bytes(text.encode("utf-8"))
    print(
        f"gen-tui-registry: wrote {args.out} - baseline {len(baseline.entries)} entries ({baseline.windows_count} on Windows), "
        f"extended {len(extended.entries)} ({extended.windows_count} on Windows)."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

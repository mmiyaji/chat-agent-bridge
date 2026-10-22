"""Validate CommunityToolkit SettingsExpander.Items XAML child types."""

from __future__ import annotations

import argparse
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable


TOOLKIT_NAMESPACE = "CommunityToolkit.WinUI.Controls"
ITEMS_PROPERTY = "SettingsExpander.Items"
SETTINGS_CARD = "SettingsCard"


@dataclass(frozen=True)
class ContractViolation:
    path: Path
    expander_id: str
    child_tag: str

    def __str__(self) -> str:
        location = f" in SettingsExpander '{self.expander_id}'" if self.expander_id else " in SettingsExpander.Items"
        return f"{self.path}:{location}: direct child '{self.child_tag}' must be a CommunityToolkit SettingsCard"


def _name_parts(tag: str) -> tuple[str, str]:
    if tag.startswith("{"):
        namespace, _, local_name = tag[1:].partition("}")
        return namespace, local_name
    return "", tag


def _is_toolkit_element(element: ET.Element, local_name: str) -> bool:
    namespace, name = _name_parts(element.tag)
    return name == local_name and namespace == f"using:{TOOLKIT_NAMESPACE}"


def validate_xaml(path: Path) -> list[ContractViolation]:
    """Return invalid direct children of each CommunityToolkit Items property."""
    try:
        root = ET.parse(path).getroot()
    except ET.ParseError as error:
        raise ValueError(f"Could not parse XAML {path}: {error}") from error

    violations: list[ContractViolation] = []
    for items in root.iter():
        if not _is_toolkit_element(items, ITEMS_PROPERTY):
            continue

        parent = next((node for node in root.iter() if items in list(node)), None)
        expander_id = ""
        if parent is not None:
            expander_id = next(
                (value for key, value in parent.attrib.items() if _name_parts(key)[1].endswith(".AutomationId")),
                "",
            )

        for child in list(items):
            if not _is_toolkit_element(child, SETTINGS_CARD):
                namespace, name = _name_parts(child.tag)
                rendered_name = f"{{{namespace}}}{name}" if namespace else name
                violations.append(ContractViolation(path, expander_id, rendered_name))
    return violations


def validate_tree(root: Path) -> list[ContractViolation]:
    violations: list[ContractViolation] = []
    for path in sorted(root.rglob("*.xaml")):
        if not path.is_file() or any(part.lower() in {"bin", "obj"} for part in path.parts):
            continue
        violations.extend(validate_xaml(path))
    return violations


def main(arguments: Iterable[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--root",
        type=Path,
        default=Path(__file__).resolve().parent / "ChatAgentBridge.Desktop",
        help="directory containing the XAML files to inspect",
    )
    options = parser.parse_args(arguments)
    root = options.root.resolve()
    if not root.is_dir():
        print(f"XAML root directory does not exist: {root}", file=sys.stderr)
        return 2

    try:
        violations = validate_tree(root)
    except ValueError as error:
        print(error, file=sys.stderr)
        return 2

    if violations:
        for violation in violations:
            print(violation, file=sys.stderr)
        return 1

    print(f"Validated SettingsExpander.Items child types in {root}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

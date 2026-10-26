"""Validate bilingual resource coverage and .NET format arguments without starting an agent."""
from pathlib import Path
import re
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parent
errors = []

def read(path):
    entries = {}
    for item in ET.parse(path).getroot().findall("data"):
        name = item.attrib["name"]
        if name in entries:
            errors.append(f"Duplicate resource {path.name}: {name}")
        entries[name] = item.findtext("value", "")
    return entries

def arguments(value):
    """Validate composite-format strings while allowing reordered arguments."""
    # Some unformatted help text contains literal braces. Only strings with a
    # numeric format item use the composite-format contract checked here.
    if not re.search(r"\{\d", value):
        return set()
    found = set()
    index = 0
    while index < len(value):
        if value[index:index + 2] in ("{{", "}}"):
            index += 2
        elif value[index] == "{":
            item = re.match(r"\{(\d+)(?:,\s*-?\d+)?(?::[^{}]*)?\}", value[index:])
            if item is None:
                raise ValueError("invalid or unclosed composite-format item")
            found.add(item.group(1))
            index += item.end()
        elif value[index] == "}":
            raise ValueError("unescaped closing brace")
        else:
            index += 1
    return found


def resource_keys(source, helper):
    """Yield keys from literal Get arguments and conditional/switch result arms."""
    for call in re.finditer(rf"{helper}\.Get\(", source):
        depth, quoted, escaped = 0, False, False
        start = call.end()
        for end in range(start, len(source)):
            char = source[end]
            if quoted:
                if escaped:
                    escaped = False
                elif char == "\\":
                    escaped = True
                elif char == '"':
                    quoted = False
            elif char == '"':
                quoted = True
            elif char in "({[":
                depth += 1
            elif char == ")":
                if depth == 0:
                    break
                depth -= 1
            elif char in "}]":
                depth -= 1
            elif char == "," and depth == 0:
                break
        argument = source[start:end]
        if re.match(r'^\s*"[^"\\]*"\s*\+', argument):
            # Suffixes in a constructed key are not complete resource keys.
            continue
        for literal in re.finditer(r'"([^"\\]+)"', argument):
            prefix = argument[:literal.start()].rstrip()
            suffix = argument[literal.end():].lstrip()
            if not prefix and not suffix:
                yield literal.group(1)
            elif re.search(r"(?:\?|:|=>|\?\?)\s*$", prefix):
                yield literal.group(1)


def check_resource_key_scanner():
    """Guard the scanner against extracting labels or dynamic key prefixes."""
    cases = (
        ('DesktopText.Get("Greeting")', "DesktopText", {"Greeting"}),
        ('DesktopText.Get(ready ? "Ready" : "Waiting")', "DesktopText", {"Ready", "Waiting"}),
        ('ViewModelText.Get(mode switch { "first" => "ApprovalFirst", "auto" => "ApprovalAuto", _ => "ApprovalAlways" })',
         "ViewModelText", {"ApprovalFirst", "ApprovalAuto", "ApprovalAlways"}),
        ('DesktopText.Get("SettingsReview" + key)', "DesktopText", set()),
        ('MrtText.Get("Confirm.Content", culture)', "MrtText", {"Confirm.Content"}),
        ('MrtText.Get("Mode" + (mode switch { "auto" => "Auto", _ => "Always" }) + ".Content")',
         "MrtText", set()),
    )
    for source, helper, expected in cases:
        actual = set(resource_keys(source, helper))
        if actual != expected:
            raise SystemExit(f"Resource key scanner regression: {source}: expected {expected}, got {actual}")


def check_format_scanner():
    for value, expected in (("{1}: {0:N0}", {"0", "1"}),
                            ("{{literal}} {0,-12}", {"0"}),
                            ("{{{0}}}", {"0"}),
                            ("JSON: {example}", set())):
        if arguments(value) != expected:
            raise SystemExit(f"Format scanner regression: {value}")
    for value in ("{0", "{0} }", "{0} {other}"):
        try:
            arguments(value)
        except ValueError:
            continue
        raise SystemExit(f"Invalid format was accepted: {value}")

pairs = [
    ("ChatAgentBridge.Core/Localization/Strings.resx", "ChatAgentBridge.Core/Localization/Strings.ja.resx"),
    ("ChatAgentBridge.Desktop/Localization/DesktopStrings.resx", "ChatAgentBridge.Desktop/Localization/DesktopStrings.ja.resx"),
    ("ChatAgentBridge.Desktop/Localization/LanguageUi.resx", "ChatAgentBridge.Desktop/Localization/LanguageUi.ja.resx"),
    ("ChatAgentBridge.Desktop/Strings/en-US/Resources.resw", "ChatAgentBridge.Desktop/Strings/ja-JP/Resources.resw"),
    ("ChatAgentBridge.Desktop/Localization/ViewModelStrings.resx", "ChatAgentBridge.Desktop/Localization/ViewModelStrings.ja.resx"),
    ("ChatAgentBridge.Desktop/Diagnostics/DiagnosticsStrings.resx", "ChatAgentBridge.Desktop/Diagnostics/DiagnosticsStrings.ja.resx"),
    ("ChatAgentBridge.Desktop/Maintenance/MaintenanceStrings.resx", "ChatAgentBridge.Desktop/Maintenance/MaintenanceStrings.ja.resx"),
    ("ChatAgentBridge.Desktop/Maintenance/LogRetentionStrings.resx", "ChatAgentBridge.Desktop/Maintenance/LogRetentionStrings.ja.resx"),
]
catalogs = []
check_resource_key_scanner()
check_format_scanner()
for english, japanese in pairs:
    en, ja = read(root / english), read(root / japanese)
    catalogs.append(en)
    if en.keys() != ja.keys():
        errors.append(f"Resource keys differ: {english}; EN only={sorted(en.keys()-ja.keys())}, JA only={sorted(ja.keys()-en.keys())}")
    for key in en.keys() & ja.keys():
        if not en[key].strip() or not ja[key].strip():
            errors.append(f"Empty translation: {english}: {key}")
        try:
            if arguments(en[key]) != arguments(ja[key]):
                errors.append(f"Format arguments differ: {english}: {key}")
        except ValueError as error:
            errors.append(f"Invalid format arguments: {english}: {key}: {error}")
    print(f"{english}: {len(en)} English/Japanese resource keys")

for helper, catalog in (("CoreText", catalogs[0]), ("DesktopText", catalogs[1]),
                        ("ViewModelText", catalogs[4]), ("MrtText", catalogs[3])):
    for path in root.glob("ChatAgentBridge.*/**/*.cs"):
        if any(part in ("bin", "obj") for part in path.parts):
            continue
        for key in resource_keys(path.read_text(encoding="utf-8-sig"), helper):
            if key not in catalog:
                errors.append(f"Missing {helper} resource: {path.relative_to(root)}: {key}")

for path in (root / "ChatAgentBridge.Desktop").rglob("*.xaml"):
    if any(part in ("bin", "obj") for part in path.parts):
        continue
    tree = ET.parse(path)
    for element in tree.iter():
        uid = element.attrib.get("{http://schemas.microsoft.com/winfx/2006/xaml}Uid")
        if uid and not any(key.startswith(uid + ".") for key in catalogs[3]):
            errors.append(f"Missing x:Uid resources: {path.relative_to(root)}: {uid}")
        localized_key = element.attrib.get(
            "{using:ChatAgentBridge_Desktop.Localization}LocalizedResourceKey.Key")
        if localized_key and not localized_key.startswith("{") and not any(
                key.startswith(localized_key + ".") for key in catalogs[3]):
            errors.append(f"Missing attached localization resources: {path.relative_to(root)}: {localized_key}")
        # MRT applies every matching property at runtime. A leftover '.Text' for
        # a Button can pass the XAML compiler and still crash InitializeComponent.
        control = element.tag.removeprefix("{http://schemas.microsoft.com/winfx/2006/xaml/presentation}")
        no_text_property = {"Button", "HyperlinkButton", "AppBarButton", "ToggleButton",
                            "CheckBox", "RadioButton", "ToggleSwitch", "Expander"}
        if uid and control in no_text_property and uid + ".Text" in catalogs[3]:
            errors.append(f"Invalid x:Uid Text property on {control}: {path.relative_to(root)}: {uid}")

if errors:
    raise SystemExit("\n".join(errors))
print("Resource keys, format arguments, and resource references are consistent.")

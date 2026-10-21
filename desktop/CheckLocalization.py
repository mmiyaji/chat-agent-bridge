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
    value = value.replace("{{", "").replace("}}", "")
    return set(re.findall(r"\{(\d+)(?:[^{}]*)\}", value))


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
    )
    for source, helper, expected in cases:
        actual = set(resource_keys(source, helper))
        if actual != expected:
            raise SystemExit(f"Resource key scanner regression: {source}: expected {expected}, got {actual}")

pairs = [
    ("ChatAgentBridge.Core/Localization/Strings.resx", "ChatAgentBridge.Core/Localization/Strings.ja.resx"),
    ("ChatAgentBridge.Desktop/Localization/DesktopStrings.resx", "ChatAgentBridge.Desktop/Localization/DesktopStrings.ja.resx"),
    ("ChatAgentBridge.Desktop/Localization/LanguageUi.resx", "ChatAgentBridge.Desktop/Localization/LanguageUi.ja.resx"),
    ("ChatAgentBridge.Desktop/Strings/en-US/Resources.resw", "ChatAgentBridge.Desktop/Strings/ja-JP/Resources.resw"),
    ("ChatAgentBridge.Desktop/Localization/ViewModelStrings.resx", "ChatAgentBridge.Desktop/Localization/ViewModelStrings.ja.resx"),
]
catalogs = []
check_resource_key_scanner()
for english, japanese in pairs:
    en, ja = read(root / english), read(root / japanese)
    catalogs.append(en)
    if en.keys() != ja.keys():
        errors.append(f"Resource keys differ: {english}; EN only={sorted(en.keys()-ja.keys())}, JA only={sorted(ja.keys()-en.keys())}")
    for key in en.keys() & ja.keys():
        if not en[key].strip() or not ja[key].strip():
            errors.append(f"Empty translation: {english}: {key}")
        if arguments(en[key]) != arguments(ja[key]):
            errors.append(f"Format arguments differ: {english}: {key}")
    print(f"{english}: {len(en)} English/Japanese resource keys")

for helper, catalog in (("CoreText", catalogs[0]), ("DesktopText", catalogs[1]), ("ViewModelText", catalogs[4])):
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

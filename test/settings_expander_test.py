import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "desktop"))
import CheckSettingsExpander as checker


TOOLKIT = "using:CommunityToolkit.WinUI.Controls"


class SettingsExpanderContractTests(unittest.TestCase):
    def validate_fixture(self, body):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "fixture.xaml"
            path.write_text(
                f'''<UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:settings="{TOOLKIT}" xmlns:local="using:Example.Controls">
                    {body}
                </UserControl>''',
                encoding="utf-8",
            )
            return checker.validate_xaml(path)

    def test_accepts_settings_cards_and_nested_non_card_content(self):
        issues = self.validate_fixture(
            """<settings:SettingsExpander AutomationProperties.AutomationId="Safe">
                <settings:SettingsExpander.Items>
                  <settings:SettingsCard><StackPanel><TextBlock Text="nested content" /></StackPanel></settings:SettingsCard>
                  <settings:SettingsCard />
                </settings:SettingsExpander.Items>
              </settings:SettingsExpander>"""
        )
        self.assertEqual([], issues)

    def test_rejects_direct_user_control_child(self):
        issues = self.validate_fixture(
            """<settings:SettingsExpander AutomationProperties.AutomationId="Broken">
                <settings:SettingsExpander.Items><local:DiagnosticsControl /></settings:SettingsExpander.Items>
              </settings:SettingsExpander>"""
        )
        self.assertEqual(1, len(issues))
        self.assertIn("DiagnosticsControl", str(issues[0]))
        self.assertIn("Broken", str(issues[0]))

    def test_rejects_direct_textblock_child(self):
        issues = self.validate_fixture(
            """<settings:SettingsExpander>
                <settings:SettingsExpander.Items><TextBlock Text="status" /></settings:SettingsExpander.Items>
              </settings:SettingsExpander>"""
        )
        self.assertEqual(1, len(issues))
        self.assertIn("TextBlock", str(issues[0]))

    def test_rejects_non_toolkit_settings_card_name(self):
        issues = self.validate_fixture(
            """<settings:SettingsExpander>
                <settings:SettingsExpander.Items><local:SettingsCard /></settings:SettingsExpander.Items>
              </settings:SettingsExpander>"""
        )
        self.assertEqual(1, len(issues))
        self.assertIn("SettingsCard", str(issues[0]))

    def test_ignores_non_items_content_and_non_toolkit_items_properties(self):
        issues = self.validate_fixture(
            """<settings:SettingsExpander>
                <settings:SettingsExpander.Header><TextBlock Text="header" /></settings:SettingsExpander.Header>
                <local:SettingsExpander.Items><TextBlock Text="not the toolkit property" /></local:SettingsExpander.Items>
              </settings:SettingsExpander>"""
        )
        self.assertEqual([], issues)


if __name__ == "__main__":
    unittest.main()

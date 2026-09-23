"""Source-contract regressions for unsupported native-shell settings.

Run with: python3 -m unittest discover -s Tests/GUI -p test_modern_settings_contract.py -v
These checks do not replace a Windows WinForms build or interactive UI test.
"""
import pathlib
import re
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[2]


class ModernSettingsContractTests(unittest.TestCase):
    def test_settings_page_only_offers_consumed_switches(self):
        source = (ROOT / "GUI/Main/ModernShell.cs").read_text()
        offered = re.findall(r"MakeSettingToggle\((\d+)\)", source)
        self.assertEqual(["0", "1", "2"], offered,
                         "Do not offer verification/artwork switches without consumers")


    def test_shell_does_not_read_or_write_unused_preferences(self):
        main = (ROOT / "GUI/Main/Main.cs").read_text()
        for field in ("ModernVerifyDownloads", "ModernCacheArtwork"):
            with self.subTest(field=field):
                self.assertNotIn(field, main,
                                 "Legacy config fields must not masquerade as working settings")
        shell = (ROOT / "GUI/Main/ModernShell.cs").read_text()
        states = re.search(r"bool\[\] settingToggles\s*=\s*\{([^}]+)\}", shell)
        self.assertIsNotNone(states)
        self.assertEqual(3, len(re.findall(r"\b(?:true|false)\b", states.group(1))),
                         "Only read the three supported configuration-backed switches")


    def test_refresh_catalogue_updates_repositories_not_only_local_grid(self):
        main = (ROOT / "GUI/Main/Main.cs").read_text()
        body = main.split("internal void RefreshModernCatalogue()", 1)[1].split("///", 1)[0]
        self.assertIn("UpdateRepo(", body,
                      "The native Refresh action must fetch repository updates like classic Refresh")
        self.assertNotIn("RefreshModList(false)", body)
        self.assertIn("!Waiting", body)


if __name__ == "__main__":
    unittest.main()

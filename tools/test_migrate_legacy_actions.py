"""Check data preservation and repeatability without executing any remote action."""
import unittest
import importlib.util
from pathlib import Path

spec = importlib.util.spec_from_file_location('migrate_legacy_actions', Path(__file__).with_name('migrate-legacy-actions.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
convert = module.convert


class MigrationTests(unittest.TestCase):
    def test_power_actions_keep_identity_and_use_official_icons(self):
        examples = [('rundll32.exe', 'user32.dll,LockWorkStation', 'Lock', 'Lock'),
                    ('shutdown', '/h', 'Hibernate', 'Recent'),
                    ('shutdown', '/l', 'LogOff', 'SignOut'),
                    ('shutdown', '/r /t 0', 'Restart', 'UpdateRestore'),
                    ('shutdown', '/s /t 0', 'Shutdown', 'PowerButton'),
                    ('loginctl', 'lock-session', 'Lock', 'Lock')]
        for path, args, kind, icon in examples:
            with self.subTest(kind=kind, path=path):
                result, changed = convert({'$type': 'Process', 'Id': 'saved-id', 'Name': 'Custom name', 'Path': path, 'Arguments': args})
                self.assertTrue(changed)
                self.assertEqual((result['Id'], result['Name']), ('saved-id', 'Custom name'))
                self.assertEqual((result['ActionId'], result['Settings'], result['Icon']), ('Power', {'Kind': kind}, icon))
                self.assertTrue(result['AskForConfirmation'])
                self.assertEqual(convert(result), (result, False))

    def test_custom_command_and_confirmation_are_preserved(self):
        original = {'$type': 'Process', 'Id': 'custom', 'Name': 'Custom', 'Path': 'tool.exe', 'Arguments': '--custom', 'StartInDirectory': 'C:/work', 'EnvironmentVariables': {'MODE': 'local'}, 'UseShellExecute': False, 'CreateNoWindow': True, 'AskForConfirmation': False}
        result, _ = convert(original)
        self.assertEqual(result['ActionId'], 'Run')
        for key in ('Path', 'Arguments', 'StartInDirectory', 'EnvironmentVariables', 'UseShellExecute', 'CreateNoWindow'):
            self.assertEqual(result['Settings'][key], original[key])
        self.assertFalse(result['AskForConfirmation'])

    def test_unknown_legacy_type_is_rejected_without_silent_loss(self):
        with self.assertRaises(ValueError):
            convert({'$type': 'Unknown', 'Id': 'unknown'})


if __name__ == '__main__':
    unittest.main()

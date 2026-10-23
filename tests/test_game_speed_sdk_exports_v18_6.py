"""Regression checks for the v18.6 native linker export repair."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / 'tools' / 'prepare_game_speed_sdk.py'
spec = importlib.util.spec_from_file_location('game_speed_sdk_v18_6', SCRIPT)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

class RuntimeExportsTest(unittest.TestCase):
    def test_exact_export_set(self):
        self.assertEqual(len(module.SPEED_EXPORTS), 4)
        self.assertEqual(len(module.RECOMP_LINK_EXPORTS), 2)
        self.assertEqual(len(module.REQUIRED_EXPORTS), 6)
        self.assertEqual(len(set(module.REQUIRED_EXPORTS)), 6)
        self.assertIn('ListFlags@cvar@rex', module.RECOMP_LINK_EXPORTS[0])
        self.assertIn('QueryRegionInfo@BaseHeap@memory@rex', module.RECOMP_LINK_EXPORTS[1])
        self.assertTrue(module.RECOMP_LINK_EXPORTS[0].endswith('XZ'))
        self.assertTrue(module.RECOMP_LINK_EXPORTS[1].endswith('Z'))

    def test_add_export_symbols_without_modifying_existing_exports(self):
        before = 'EXPORTS\nexisting_export_1\nexisting_export_2\n' + '\n'.join(module.SPEED_EXPORTS) + '\n'
        after = module.transform_exports(before)
        self.assertTrue(after.startswith(before))
        self.assertEqual(after.count('existing_export_1'), 1)
        self.assertEqual(after.count('existing_export_2'), 1)
        self.assertEqual(len(after.splitlines()), len(before.splitlines()) + 2)
        for name in module.REQUIRED_EXPORTS:
            self.assertEqual(after.splitlines().count(name), 1)

    def test_idempotent_repeated_build(self):
        original = 'EXPORTS\nsdk_entry_1\n'
        once = module.transform_exports(original)
        twice = module.transform_exports(once)
        self.assertEqual(once, twice)

    def test_partial_missing_export_recovers_only_it(self):
        original = 'EXPORTS\n' + '\n'.join(module.REQUIRED_EXPORTS[:-1]) + '\ncustom_user_symbol\n'
        out = module.transform_exports(original)
        self.assertTrue(out.startswith(original))
        self.assertEqual(out.splitlines().count(module.RECOMP_LINK_EXPORTS[1]), 1)
        self.assertEqual(out.splitlines().count('custom_user_symbol'), 1)

    def test_unrecognized_def_is_not_modified(self):
        for source in ('', 'LIBRARY rexruntime\n', 'EXPORTS extra\n'):
            with self.subTest(source=source), self.assertRaises(ValueError):
                module.transform_exports(source)

    def test_duplicate_symbol_refused(self):
        symbol = module.RECOMP_LINK_EXPORTS[0]
        with self.assertRaisesRegex(ValueError, 'Duplicate'):
            module.transform_exports('EXPORTS\n' + symbol + '\n' + symbol + '\n')

    def test_signature_is_exactly_msvc_x64(self):
        # MSVC uses ?ListFlags for a returning std::vector<std::string>.
        # HeapAllocationInfo is an SDK struct; member QueryRegionInfo is bool.
        self.assertEqual(module.RECOMP_LINK_EXPORTS, (
            '?ListFlags@cvar@rex@@YA?AV?$vector@V?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@V?$allocator@V?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@@2@@std@@XZ',
            '?QueryRegionInfo@BaseHeap@memory@rex@@QEAA_NIPEAUHeapAllocationInfo@23@@Z',
        ))

if __name__ == '__main__':
    unittest.main()

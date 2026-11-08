"""Security boundary tests; synthetic files are not release-validation evidence."""
import json
import stat
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch

import package_directory as pkg


class PackageBoundaryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.archive = Path(self.temp.name) / 'USTC_Automatic_Course_Selection_Tool_2.2.1_linux-x64.zip'
        self.entry = pkg.ENTRY['linux-x64']
        self.files = {self.entry: b'not a real ELF', pkg.ASSEMBLY: b'not a real assembly',
                      'ustc-assistant': b'launcher', 'service/ustc-course-assistant.service': b'unit'}

    def manifest(self):
        return {'FormatVersion': 1, 'Version': '2.2.1', 'Rid': 'linux-x64', 'EntryPoint': self.entry,
                'Files': [{'Path': n, 'Length': len(v), 'Sha256': pkg.digest(v), 'Executable': n in (self.entry, 'ustc-assistant')} for n, v in self.files.items()]}

    def archive_with(self, manifest=None, extra=(), mutate=None):
        manifest = self.manifest() if manifest is None else manifest
        with zipfile.ZipFile(self.archive, 'w') as z:
            def put(name, data, mode):
                info = zipfile.ZipInfo(name)
                info.create_system = 3
                info.external_attr = mode << 16
                z.writestr(info, data)
            put('data/', b'', stat.S_IFDIR | 0o700)
            put(pkg.MANIFEST, json.dumps(manifest).encode(), stat.S_IFREG | 0o644)
            for name, data in self.files.items():
                put(name, data, stat.S_IFREG | (0o755 if name in (self.entry, 'ustc-assistant') else 0o644))
            for name, data, mode in extra:
                put(name, data, mode)
        if mutate: mutate(self.archive)

    def verify(self):
        # These tests isolate ZIP/manifest handling; actual executable checks are separate.
        with patch.object(pkg, 'validate_binary'):
            pkg.verify_directory_archive(self.archive, 'linux-x64', '2.2.1')

    def test_valid_structure(self):
        self.archive_with()
        self.verify()

    def test_path_traversal_and_data_rejected(self):
        for name in ('../escape', '/absolute', 'C:/file', 'app/../escape', 'data/private'):
            with self.subTest(name=name):
                m = self.manifest()
                m['Files'].append({'Path': name, 'Length': 0, 'Sha256': pkg.digest(b''), 'Executable': False})
                self.archive_with(m)
                with self.assertRaises(ValueError): self.verify()

    def test_duplicates_and_symlinks_rejected(self):
        for extra in ((self.entry.upper(), b'x', stat.S_IFREG | 0o644), ('app/link', b'target', stat.S_IFLNK | 0o777), ('data/private', b'x', stat.S_IFREG | 0o600)):
            with self.subTest(extra=extra):
                self.archive_with(extra=[extra])
                with self.assertRaises(ValueError): self.verify()

    def test_hash_length_mode_and_extra_files(self):
        for change in ('hash', 'length', 'mode', 'extra'):
            with self.subTest(change=change):
                m = self.manifest()
                extras = []
                if change == 'hash': m['Files'][0]['Sha256'] = '0' * 64
                if change == 'length': m['Files'][0]['Length'] += 1
                if change == 'mode': m['Files'][0]['Executable'] = False
                if change == 'extra': extras.append(('app/extra', b'x', stat.S_IFREG | 0o644))
                self.archive_with(m, extras)
                with self.assertRaises(ValueError): self.verify()

    def test_fake_binaries_rejected_without_mock(self):
        self.archive_with()
        with self.assertRaises(ValueError):
            pkg.verify_directory_archive(self.archive, 'linux-x64', '2.2.1')
        with self.assertRaises(ValueError):
            pkg.validate_binary({pkg.ENTRY['win-x64']: b'MZ'}, 'win-x64', '2.2.1')

    def test_both_platforms_reject_browser_and_driver_payloads(self):
        for rid in ('win-x64', 'linux-x64'):
            for forbidden in ('app/.playwright/node/linux-x64/node', 'app/browser/chromium/chrome.exe',
                              'app/UstcCourseAssistant.Browser.dll', 'app/Microsoft.Playwright.dll',
                              'app/playwright.ps1', 'app/chromedriver', 'app/chromedriver.exe'):
                with self.subTest(rid=rid, path=forbidden):
                    manifest = {'FormatVersion': 1, 'Version': '2.2.1', 'Rid': rid,
                                'EntryPoint': pkg.ENTRY[rid],
                                'Files': [{'Path': forbidden, 'Length': 0, 'Sha256': pkg.digest(b''), 'Executable': False}]}
                    with self.assertRaisesRegex(ValueError, 'browser or Playwright driver'):
                        pkg._validate_manifest(manifest, rid, '2.2.1')

    def test_both_platforms_reject_updater_payloads(self):
        for rid in ('win-x64', 'linux-x64'):
            for forbidden in ('app/updater', 'app/updater/other',
                              'app/UstcCourseAssistant.Updater', 'app/UstcCourseAssistant.Updater.exe',
                              'app/UstcCourseAssistant.Updater.dll', 'APP/UPDATER/helper'):
                with self.subTest(rid=rid, path=forbidden):
                    manifest = {'FormatVersion': 1, 'Version': '2.2.1', 'Rid': rid,
                                'EntryPoint': pkg.ENTRY[rid],
                                'Files': [{'Path': forbidden, 'Length': 0, 'Sha256': pkg.digest(b''), 'Executable': False}]}
                    with self.assertRaisesRegex(ValueError, 'updater payloads'):
                        pkg._validate_manifest(manifest, rid, '2.2.1')

    def test_windows_rejects_cli_payloads(self):
        manifest = {'FormatVersion': 1, 'Version': '2.2.1', 'Rid': 'win-x64',
                    'EntryPoint': pkg.ENTRY['win-x64'],
                    'Files': [{'Path': 'app/UstcCourseAssistant.Cli.dll', 'Length': 0,
                               'Sha256': pkg.digest(b''), 'Executable': False}]}
        with self.assertRaisesRegex(ValueError, 'CLI payloads'):
            pkg._validate_manifest(manifest, 'win-x64', '2.2.1')

    def test_generator_rejects_input_data_and_symlink(self):
        root = Path(self.temp.name) / 'root'
        (root / 'data').mkdir(parents=True)
        (root / 'data' / 'secret').write_text('secret')
        with self.assertRaises(ValueError):
            pkg.create_package(root, self.archive, 'linux-x64', '2.2.1')


if __name__ == '__main__':
    unittest.main()

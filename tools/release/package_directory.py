"""Create and verify portable directory packages without executing their contents."""
import argparse
import hashlib
import json
import os
import re
import stat
import struct
import zipfile
from pathlib import Path

RIDS = ('win-x64', 'linux-x64')
MANIFEST = 'package-manifest.json'
ENTRY = {'win-x64': 'app/UstcCourseAssistant.Route2.exe',
         'linux-x64': 'app/UstcCourseAssistant.Cli'}
ASSEMBLY = 'app/UstcCourseAssistant.Cli.dll'
VERSION = re.compile(r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\Z')


def safe_path(name):
    if (not isinstance(name, str) or not name or '\\' in name or '\0' in name
            or name.startswith('/') or ':' in name
            or any(p in ('', '.', '..') for p in name.split('/'))):
        raise ValueError(f'Unsafe package path: {name!r}')
    if any(p.endswith((' ', '.')) or any(ord(c) < 32 for c in p) or re.fullmatch(r'(?i:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\..*)?', p) for p in name.split('/')):
        raise ValueError(f'Unsafe package path: {name!r}')
    return name


def digest(data):
    return hashlib.sha256(data).hexdigest()


def _u16(data, pos):
    return struct.unpack_from('<H', data, pos)[0]


def _u32(data, pos):
    return struct.unpack_from('<I', data, pos)[0]


class PE:
    def __init__(self, data):
        self.data = data
        if data[:2] != b'MZ':
            raise ValueError('Not a PE image')
        pe = _u32(data, 0x3c)
        if data[pe:pe + 4] != b'PE\0\0':
            raise ValueError('Invalid PE signature')
        self.machine = _u16(data, pe + 4)
        count, optsize = _u16(data, pe + 6), _u16(data, pe + 20)
        opt = pe + 24
        magic = _u16(data, opt)
        if magic not in (0x10b, 0x20b):
            raise ValueError('Invalid PE optional header')
        self.subsystem = _u16(data, opt + 68)
        directory = opt + (112 if magic == 0x20b else 96)
        if directory + 15 * 8 > opt + optsize:
            raise ValueError('Truncated PE data directories')
        self.directories = [(_u32(data, directory + i * 8), _u32(data, directory + i * 8 + 4)) for i in range(15)]
        table = opt + optsize
        self.sections = []
        for i in range(count):
            p = table + i * 40
            self.sections.append((_u32(data, p + 12), _u32(data, p + 8), _u32(data, p + 20), _u32(data, p + 16)))

    def offset(self, rva, size=1):
        for va, virtual, raw, length in self.sections:
            if va <= rva and rva - va + size <= length and raw + rva - va + size <= len(self.data):
                return raw + rva - va
        raise ValueError('PE address outside image')

    def windows_version(self):
        rva, size = self.directories[2]
        base = self.offset(rva, size)
        end = base + size
        def entries(relative):
            p = base + relative
            if p < base or p + 16 > end:
                raise ValueError('Invalid resource directory')
            count = _u16(self.data, p + 12) + _u16(self.data, p + 14)
            if p + 16 + count * 8 > end:
                raise ValueError('Invalid resource entries')
            return [(_u32(self.data, p + 16 + i * 8), _u32(self.data, p + 20 + i * 8)) for i in range(count)]
        roots = [v for k, v in entries(0) if k == 16 and v & 0x80000000]
        if len(roots) != 1:
            raise ValueError('Expected one version resource')
        names = entries(roots[0] & 0x7fffffff)
        if len(names) != 1 or not names[0][1] & 0x80000000:
            raise ValueError('Expected one version resource name')
        langs = entries(names[0][1] & 0x7fffffff)
        if len(langs) != 1 or langs[0][1] & 0x80000000:
            raise ValueError('Expected one version resource language')
        p = base + langs[0][1]
        if p + 16 > end:
            raise ValueError('Invalid version resource')
        resource_rva, length = _u32(self.data, p), _u32(self.data, p + 4)
        p = self.offset(resource_rva, length)
        resource = self.data[p:p + length]
        if len(resource) < 6 or _u16(resource, 0) > len(resource):
            raise ValueError('Invalid version data')
        key = 'VS_VERSION_INFO\0'.encode('utf-16le')
        if resource[6:6 + len(key)] != key:
            raise ValueError('Invalid version key')
        value = (6 + len(key) + 3) & ~3
        if _u16(resource, 2) < 52 or value + 52 > len(resource) or _u32(resource, value) != 0xFEEF04BD:
            raise ValueError('Invalid fixed version info')
        def version_at(pos):
            ms, ls = _u32(resource, pos), _u32(resource, pos + 4)
            return (ms >> 16, ms & 65535, ls >> 16, ls & 65535)
        return version_at(value + 8), version_at(value + 16)

    def assembly_version(self):
        cli, _ = self.directories[14]
        cp = self.offset(cli, 16)
        metadata, length = _u32(self.data, cp + 8), _u32(self.data, cp + 12)
        p = self.offset(metadata, length)
        end = p + length
        if self.data[p:p + 4] != b'BSJB':
            raise ValueError('Invalid CLI metadata')
        q = p + 16 + _u32(self.data, p + 12)
        q = (q + 3) & ~3
        streams = _u16(self.data, q + 2)
        q += 4
        tables = None
        for _ in range(streams):
            if q + 8 > end:
                raise ValueError('Invalid metadata stream')
            off, size = _u32(self.data, q), _u32(self.data, q + 4)
            start = q + 8
            stop = self.data.find(b'\0', start, min(end, start + 32))
            if stop < 0 or p + off + size > end:
                raise ValueError('Invalid metadata stream name')
            if self.data[start:stop] in (b'#~', b'#-'):
                if tables is not None:
                    raise ValueError('Duplicate metadata tables')
                tables = (p + off, p + off + size)
            q = (stop + 4) & ~3
        if tables is None:
            raise ValueError('Missing metadata tables')
        t, limit = tables
        if t + 24 > limit:
            raise ValueError('Invalid metadata tables')
        heaps = self.data[t + 6]
        valid = struct.unpack_from('<Q', self.data, t + 8)[0]
        q = t + 24
        rows = [0] * 64
        for i in range(64):
            if valid >> i & 1:
                if q + 4 > limit:
                    raise ValueError('Invalid metadata row counts')
                rows[i] = _u32(self.data, q)
                q += 4
        if rows[32] != 1:
            raise ValueError('Expected exactly one Assembly row')
        s = 4 if heaps & 1 else 2
        g = 4 if heaps & 2 else 2
        b = 4 if heaps & 4 else 2
        def idx(i): return 4 if rows[i] >= 65536 else 2
        def coded(bits, ids): return 4 if max((rows[i] for i in ids), default=0) >= (1 << (16 - bits)) else 2
        tdor = coded(2, [2, 1, 27]); resolution = coded(2, [0, 26, 35, 1])
        hasconstant = coded(2, [4, 8, 23]); hascustom = coded(5, [6, 4, 1, 2, 8, 9, 10, 0, 14, 23, 20, 17, 26, 27, 32, 35, 38, 39, 40, 42, 44, 43])
        customtype = coded(3, [6, 10]); hasmarshal = coded(1, [4, 8]); hassecurity = coded(2, [2, 6, 32])
        memberparent = coded(3, [2, 1, 26, 6, 27]); semantics = coded(1, [20, 23]); method = coded(1, [6, 10]); forwarded = coded(1, [4, 6])
        sizes = [2+s+g*3, resolution+s*2, 4+s*2+tdor+idx(4)+idx(6), idx(4), 2+s+b, idx(6), 8+s+b+idx(8), idx(8), 4+s, idx(2)+tdor, memberparent+s+b, 2+hasconstant+b, hascustom+customtype+b, hasmarshal+b, 2+hassecurity+b, 6+idx(2), 4+idx(4), b, idx(2)+idx(20), idx(20), 2+s+tdor, idx(2)+idx(23), idx(23), 2+s+b, 2+idx(6)+semantics, idx(2)+method*2, s, b, 2+forwarded+s+idx(26), 4+idx(4), 8, 4]
        for i in range(32):
            q += rows[i] * sizes[i]
            if q > limit:
                raise ValueError('Metadata table outside stream')
        if q + 16 > limit:
            raise ValueError('Truncated Assembly row')
        return struct.unpack_from('<HHHH', self.data, q + 4)


def validate_binary(files, rid, version):
    expected = (*map(int, version.split('.')), 0)
    try:
        image = files[ENTRY[rid]]
        if rid == 'win-x64':
            pe = PE(image)
            if pe.machine != 0x8664 or pe.subsystem != 2 or pe.windows_version() != (expected, expected):
                raise ValueError('Windows entry architecture, subsystem or version mismatch')
        else:
            if (len(image) < 64 or image[:6] != b'\x7fELF\x02\x01' or _u16(image, 18) != 62):
                raise ValueError('Linux entry must be a little-endian ELF x86_64 image')
            if PE(files[ASSEMBLY]).assembly_version() != expected:
                raise ValueError('Linux managed assembly version mismatch')
    except (IndexError, KeyError, struct.error, OverflowError) as exc:
        raise ValueError('Missing or malformed package executable') from exc


def _validate_manifest(manifest, rid, version):
    if not isinstance(manifest, dict) or set(manifest) != {'FormatVersion', 'Version', 'Rid', 'EntryPoint', 'Files'}:
        raise ValueError('Invalid manifest fields')
    if type(manifest['FormatVersion']) is not int or manifest['FormatVersion'] != 1 or manifest['Version'] != version or manifest['Rid'] != rid or manifest['EntryPoint'] != ENTRY[rid]:
        raise ValueError('Manifest format, version, RID or entry point mismatch')
    if not isinstance(manifest['Files'], list):
        raise ValueError('Invalid manifest files')
    result = {}
    folded = set()
    for item in manifest['Files']:
        if not isinstance(item, dict) or set(item) != {'Path', 'Length', 'Sha256', 'Executable'}:
            raise ValueError('Invalid manifest file record')
        name = safe_path(item['Path'])
        if name.casefold() == MANIFEST.casefold() or name.split('/')[0].casefold() == 'data' or name.casefold() in folded:
            raise ValueError('Duplicate or forbidden manifest path')
        lower = name.casefold()
        parts = lower.split('/')
        if (any(part in ('.playwright', 'browser', 'chromium', 'chromedriver', 'chromedriver.exe') for part in parts)
                or parts[-1] in ('ustccourseassistant.browser.dll', 'microsoft.playwright.dll',
                                 'playwright.ps1', 'playwright.sh')):
            raise ValueError('Package must not include browser or Playwright driver payloads')
        if (lower == 'app/updater' or lower.startswith('app/updater/')
                or parts[-1] == 'ustccourseassistant.updater'
                or parts[-1].startswith('ustccourseassistant.updater.')):
            raise ValueError('Package must not include updater payloads')
        if rid == 'win-x64' and lower.startswith('app/ustccourseassistant.cli.'):
            raise ValueError('Windows package must not include CLI payloads')
        if type(item['Length']) is not int or item['Length'] < 0 or type(item['Executable']) is not bool or not isinstance(item['Sha256'], str) or not re.fullmatch('[0-9a-f]{64}', item['Sha256']):
            raise ValueError('Invalid manifest file attributes')
        folded.add(name.casefold())
        result[name] = item
    for name in result:
        parts = name.split('/')
        if any('/'.join(parts[:i]).casefold() in folded for i in range(1, len(parts))):
            raise ValueError('File conflicts with a directory')
    required = {'USTC选课助手.cmd'} if rid == 'win-x64' else {'ustc-assistant', 'service/ustc-course-assistant.service', ASSEMBLY}
    if not required <= result.keys():
        raise ValueError('Missing required package files')
    if rid == 'linux-x64' and not result['ustc-assistant']['Executable']:
        raise ValueError('Linux launcher must be executable')
    if ENTRY[rid] not in result or (rid == 'linux-x64' and not result[ENTRY[rid]]['Executable']):
        raise ValueError('Missing executable entry point')
    return result


def verify_directory_archive(archive, rid, version):
    if rid not in RIDS or not VERSION.fullmatch(version):
        raise ValueError('Invalid runtime or version')
    expected_name = f'USTC_Automatic_Course_Selection_Tool_{version}_{rid}.zip'
    if Path(archive).name != expected_name:
        raise ValueError(f'Archive name must be {expected_name}')
    with zipfile.ZipFile(archive) as z:
        entries = {}; folded = set(); directories = set()
        for info in z.infolist():
            name = info.filename
            if info.orig_filename != name:
                raise ValueError('ZIP filename contains NUL')
            is_dir = name.endswith('/')
            safe_path(name[:-1] if is_dir else name)
            key = name.rstrip('/').casefold()
            if key in folded:
                raise ValueError('Duplicate ZIP path')
            folded.add(key)
            mode = info.external_attr >> 16
            kind = stat.S_IFMT(mode)
            if info.flag_bits & 1 or kind not in (0, stat.S_IFREG, stat.S_IFDIR) or (kind == stat.S_IFDIR) != is_dir and kind != 0:
                raise ValueError('Encrypted or special ZIP entry')
            if is_dir:
                if info.file_size != 0:
                    raise ValueError('Nonempty directory entry')
                directories.add(name)
            else:
                entries[name] = info
        if 'data/' not in directories or MANIFEST not in entries or entries[MANIFEST].file_size > 16 * 1024 * 1024:
            raise ValueError('Missing data directory or manifest')
        def no_duplicates(pairs):
            result = {}
            for key, value in pairs:
                if key in result: raise ValueError('Duplicate JSON property')
                result[key] = value
            return result
        manifest = json.loads(z.read(entries[MANIFEST]), object_pairs_hook=no_duplicates)
        listed = _validate_manifest(manifest, rid, version)
        if set(entries) != set(listed) | {MANIFEST}:
            raise ValueError('ZIP files do not match manifest')
        allowed_dirs = {'data/'}
        for name in listed.keys() | {MANIFEST}:
            parts = name.split('/')
            allowed_dirs.update('/'.join(parts[:i]) + '/' for i in range(1, len(parts)))
        if not directories <= allowed_dirs:
            raise ValueError('Unexpected ZIP directory')
        binaries = {}
        for name, item in listed.items():
            info = entries[name]
            if info.file_size != item['Length']:
                raise ValueError(f'Length mismatch: {name}')
            mode = info.external_attr >> 16
            if bool(mode & 0o111) != item['Executable']:
                raise ValueError(f'Executable mode mismatch: {name}')
            h = hashlib.sha256(); chunks = [] if name in (ENTRY[rid], ASSEMBLY) else None
            with z.open(info) as stream:
                while True:
                    chunk = stream.read(1024 * 1024)
                    if not chunk: break
                    h.update(chunk)
                    if chunks is not None: chunks.append(chunk)
            if h.hexdigest() != item['Sha256']:
                raise ValueError(f'SHA256 mismatch: {name}')
            if chunks is not None: binaries[name] = b''.join(chunks)
        validate_binary(binaries, rid, version)


def create_package(root, archive, rid, version):
    root = Path(root)
    if rid not in RIDS or not VERSION.fullmatch(version) or root.is_symlink() or not root.is_dir():
        raise ValueError('Invalid package root, runtime or version')
    files = {}; folded = set()
    for directory, dirs, names in os.walk(root, followlinks=False):
        for name in dirs + names:
            path = Path(directory) / name
            relative = path.relative_to(root).as_posix()
            safe_path(relative)
            if path.is_symlink() or relative.casefold() in folded:
                raise ValueError('Symlink or duplicate path')
            folded.add(relative.casefold())
            if relative.casefold() == 'data':
                if not path.is_dir() or any(path.iterdir()): raise ValueError('data must be empty')
                dirs.remove(name)
            elif relative.casefold().startswith('data/') or relative == MANIFEST:
                raise ValueError('Forbidden package input')
            elif path.is_file():
                files[relative] = path
            elif not path.is_dir():
                raise ValueError('Special package input')
    if Path(archive).resolve().is_relative_to(root.resolve()):
        raise ValueError('Output must be outside package root')
    records = []
    for name, path in sorted(files.items()):
        content = path.read_bytes()
        executable = bool(path.stat().st_mode & 0o111) if os.name != 'nt' else (rid == 'linux-x64' and name in ('ustc-assistant', ENTRY[rid]))
        records.append({'Path': name, 'Length': len(content), 'Sha256': digest(content), 'Executable': executable})
    manifest = {'FormatVersion': 1, 'Version': version, 'Rid': rid, 'EntryPoint': ENTRY[rid], 'Files': records}
    _validate_manifest(manifest, rid, version)
    validate_binary({name: files[name].read_bytes() for name in (ENTRY[rid], ASSEMBLY) if name in files}, rid, version)
    with zipfile.ZipFile(archive, 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        def put(name, content, mode):
            info = zipfile.ZipInfo(name)
            info.create_system = 3
            info.external_attr = mode << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, content)
        put('data/', b'', stat.S_IFDIR | 0o700)
        for record in records:
            put(record['Path'], files[record['Path']].read_bytes(), stat.S_IFREG | (0o755 if record['Executable'] else 0o644))
        put(MANIFEST, json.dumps(manifest, ensure_ascii=False, indent=2).encode('utf-8') + b'\n', stat.S_IFREG | 0o644)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', required=True, type=Path)
    parser.add_argument('--archive', required=True, type=Path)
    parser.add_argument('--runtime', required=True, choices=RIDS)
    parser.add_argument('--version', required=True)
    args = parser.parse_args()
    create_package(args.root, args.archive, args.runtime, args.version)


if __name__ == '__main__':
    main()

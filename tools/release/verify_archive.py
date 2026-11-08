import argparse
import re
from pathlib import Path
from package_directory import RIDS, verify_directory_archive


def verify_archive(archive: Path, tag: str, runtime: str) -> None:
    if re.fullmatch(r'v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)', tag) is None:
        raise ValueError(f'Invalid release tag: {tag}')
    if runtime not in RIDS:
        raise ValueError(f'Invalid runtime: {runtime}')
    version = tag[1:]
    name = f'USTC_Automatic_Course_Selection_Tool_{version}_{runtime}.zip'
    if archive.name != name:
        raise ValueError(f'Archive name must be {name}')
    verify_directory_archive(archive, runtime, version)
    print(f'Verified {name}: manifest, binaries and file hashes')


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument('--tag', required=True)
    parser.add_argument('--runtime', required=True, choices=RIDS)
    parser.add_argument('--archive', required=True, type=Path)
    args = parser.parse_args()
    verify_archive(args.archive, args.tag, args.runtime)


if __name__ == '__main__':
    main()

import argparse
import re
import zipfile
from pathlib import Path

import pefile


def verify_archive(archive: Path, tag: str) -> None:
    if re.fullmatch(r"v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", tag) is None:
        raise ValueError(f"Invalid release tag: {tag}")
    version = tag[1:]
    stem = f"USTC_Automatic_Course_Selection_Tool_{version}"
    if archive.name != f"{stem}.zip":
        raise ValueError(f"Archive name must be {stem}.zip")
    with zipfile.ZipFile(archive) as package:
        entries = package.infolist()
        if len(entries) != 1 or entries[0].filename != f"{stem}.exe":
            raise ValueError(f"ZIP must contain only {stem}.exe at its root")
        entry = entries[0]
        if entry.file_size <= 0:
            raise ValueError("Executable must not be empty")
        if entry.flag_bits & 1:
            raise ValueError("Encrypted ZIP is not supported")
        executable = package.read(entry)
    with pefile.PE(data=executable) as image:
        if image.FILE_HEADER.Machine != 0x8664:
            raise ValueError("Executable architecture must be Windows x64")
        if image.OPTIONAL_HEADER.Subsystem != 2:
            raise ValueError("Executable must be a Windows GUI application")
        if len(image.VS_FIXEDFILEINFO) != 1:
            raise ValueError("Executable must have exactly one version resource")
        info = image.VS_FIXEDFILEINFO[0]
        actual = (info.FileVersionMS >> 16, info.FileVersionMS & 65535,
                  info.FileVersionLS >> 16, info.FileVersionLS & 65535)
        expected = (*map(int, version.split(".")), 0)
        if actual != expected:
            raise ValueError(f"Executable FileVersion {actual} does not match {tag}")
        product = (info.ProductVersionMS >> 16, info.ProductVersionMS & 65535,
                   info.ProductVersionLS >> 16, info.ProductVersionLS & 65535)
        if product != expected:
            raise ValueError(f"Executable ProductVersion {product} does not match {tag}")
    print(f"Verified {archive.name}: Windows x64, FileVersion={version}.0, one root EXE")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--tag", required=True)
    parser.add_argument("--archive", required=True, type=Path)
    args = parser.parse_args()
    verify_archive(args.archive, args.tag)


if __name__ == "__main__":
    main()

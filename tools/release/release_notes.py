from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path
from typing import List, Sequence, Tuple

from markdown_it import MarkdownIt
from markdown_it.token import Token


DEFAULT_CHANGELOG = Path(__file__).resolve().parents[2] / "docs" / "CHANGELOG.md"
_VERSION_CORE = r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)"
_PRERELEASE_PART = r"(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)"
_VERSION = re.compile(
    rf"v?({_VERSION_CORE}(?:-{_PRERELEASE_PART}(?:\.{_PRERELEASE_PART})*)?"
    r"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?)"
)


def normalize_version(tag: str) -> str:
    match = _VERSION.fullmatch(tag)
    if match is None:
        raise ValueError(f"版本标签格式无效：{tag!r}，应为 v主版本.次版本.修订。")
    return match.group(1)


def inline_text(token: Token) -> str:
    # 读取解析后的可见文字，避免把代码或格式标记误认成版本号。
    parts = []
    for child in token.children or []:
        if child.type in ("text", "code_inline", "image"):
            parts.append(child.content)
        elif child.type in ("softbreak", "hardbreak"):
            parts.append(" ")
    return "".join(parts).strip()


def table_rows(tokens: Sequence[Token]) -> List[Tuple[bool, List[Token]]]:
    rows = []
    cells: List[Token] = []
    in_table = False
    in_header = False
    in_row = False
    for token in tokens:
        if token.type == "table_open":
            in_table = True
        elif token.type == "table_close":
            in_table = False
            in_row = False
            in_header = False
        elif in_table and token.type == "thead_open":
            in_header = True
        elif in_table and token.type == "thead_close":
            in_header = False
        elif in_table and token.type == "tr_open":
            cells = []
            in_row = True
        elif in_table and token.type == "tr_close":
            rows.append((in_header, cells))
            in_row = False
        elif in_row and token.type == "inline":
            cells.append(token)
    return rows


def build_release_notes(tag: str, changelog: Path = DEFAULT_CHANGELOG) -> str:
    version = normalize_version(tag)
    source = changelog.read_text(encoding="utf-8-sig")
    tokens = MarkdownIt("commonmark").enable("table").parse(source)
    matches = []
    valid_header = False
    for is_header, cells in table_rows(tokens):
        if is_header:
            valid_header = [inline_text(cell) for cell in cells] == ["版本", "内容"]
            continue
        if cells and inline_text(cells[0]) in (version, f"v{version}"):
            if not valid_header or len(cells) != 2:
                raise ValueError(f"版本 {version} 所在表格必须使用“版本”和“内容”两列。")
            matches.append(cells[1])
    if len(matches) != 1:
        raise ValueError(f"版本 {version} 必须精确匹配一行，实际匹配 {len(matches)} 行。")
    description = matches[0]
    if not inline_text(description):
        raise ValueError(f"版本 {version} 的更新内容为空。")
    return f"## v{version} 更新说明\n\n{description.content.strip()}\n"


def main() -> int:
    parser = argparse.ArgumentParser(description="从版本历史表格生成唯一对应版本的中文 Release 说明。")
    parser.add_argument("--tag", required=True, help="版本标签，例如 v2.2.0")
    parser.add_argument("--changelog", type=Path, default=DEFAULT_CHANGELOG, help="版本历史 Markdown 路径")
    parser.add_argument("--output", type=Path, required=True, help="UTF-8 Markdown 输出路径")
    args = parser.parse_args()
    try:
        notes = build_release_notes(args.tag, args.changelog)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        with args.output.open("w", encoding="utf-8", newline="\n") as output:
            output.write(notes)
    except (OSError, UnicodeError, ValueError) as error:
        print(f"生成发布说明失败：{error}", file=sys.stderr)
        return 1
    print(f"已生成 {args.tag} 的发布说明：{args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

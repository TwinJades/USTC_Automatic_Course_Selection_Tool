from __future__ import annotations

import argparse
import json
import os
import sys
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Optional, Tuple

from release_notes import DEFAULT_CHANGELOG, build_release_notes


REPOSITORY = "TwinJades/USTC_Automatic_Course_Selection_Tool"
HISTORICAL_TAGS = ("v1.0.0", "v2.0.0", "v2.0.1", "v2.1.0")


class SyncError(RuntimeError):
    pass


def windows_credential() -> Optional[str]:
    if os.name != "nt":
        return None
    import ctypes
    from ctypes import wintypes

    class Credential(ctypes.Structure):
        _fields_ = [
            ("Flags", wintypes.DWORD),
            ("Type", wintypes.DWORD),
            ("TargetName", wintypes.LPWSTR),
            ("Comment", wintypes.LPWSTR),
            ("LastWritten", wintypes.FILETIME),
            ("CredentialBlobSize", wintypes.DWORD),
            ("CredentialBlob", ctypes.POINTER(ctypes.c_ubyte)),
            ("Persist", wintypes.DWORD),
            ("AttributeCount", wintypes.DWORD),
            ("Attributes", ctypes.c_void_p),
            ("TargetAlias", wintypes.LPWSTR),
            ("UserName", wintypes.LPWSTR),
        ]

    # 只读取已存在的 GitHub 通用凭据，不打开登录窗口。
    library = ctypes.WinDLL("advapi32", use_last_error=True)
    library.CredReadW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.POINTER(ctypes.POINTER(Credential))]
    library.CredReadW.restype = wintypes.BOOL
    library.CredFree.argtypes = [ctypes.c_void_p]
    library.CredFree.restype = None
    pointer = ctypes.POINTER(Credential)()
    if not library.CredReadW("gh:github.com:", 1, 0, ctypes.byref(pointer)):
        error = ctypes.get_last_error()
        if error == 1168:
            return None
        raise SyncError(f"读取 Windows GitHub 凭据失败：Win32 {error}。")
    try:
        blob = ctypes.string_at(pointer.contents.CredentialBlob, pointer.contents.CredentialBlobSize)
        try:
            return blob.decode("utf-8").strip() or None
        except UnicodeError:
            raise SyncError("Windows GitHub 凭据不是有效的 UTF-8 文本。") from None
    finally:
        library.CredFree(pointer)


def get_credential() -> Tuple[str, str]:
    for name in ("GH_TOKEN", "GITHUB_TOKEN"):
        value = os.environ.get(name, "").strip()
        if value:
            return value, name
    token = windows_credential()
    if token:
        return token, "windows-credential:gh:github.com:"
    raise SyncError("未找到 GH_TOKEN/GITHUB_TOKEN 或已有 Windows GitHub 凭据；无法授权 GitHub 操作。")


class GitHubAPI:
    def __init__(self, token: str, report: Dict[str, Any]) -> None:
        self.token = token
        self.report = report

    def request(self, method: str, path: str, payload: Optional[Dict[str, Any]] = None) -> Dict[str, Any]:
        data = None if payload is None else json.dumps(payload, ensure_ascii=False).encode("utf-8")
        request = urllib.request.Request(
            f"https://api.github.com{path}",
            data=data,
            method=method,
            headers={
                "Accept": "application/vnd.github+json",
                "Authorization": f"Bearer {self.token}",
                "Content-Type": "application/json; charset=utf-8",
                "User-Agent": "USTC-Release-Notes-Maintenance",
                "X-GitHub-Api-Version": "2022-11-28",
            },
        )
        evidence: Dict[str, Any] = {"method": method, "path": path}
        self.report["requests"].append(evidence)
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                evidence["status"] = response.status
                evidence["request_id"] = response.headers.get("X-GitHub-Request-Id")
                if response.status != 200:
                    raise SyncError(f"{method} {path} 返回非预期 HTTP {response.status}。")
                result = json.loads(response.read().decode("utf-8"))
        except urllib.error.HTTPError as error:
            evidence["status"] = error.code
            evidence["request_id"] = error.headers.get("X-GitHub-Request-Id")
            evidence["accepted_permissions"] = error.headers.get("X-Accepted-GitHub-Permissions")
            evidence["oauth_scopes"] = error.headers.get("X-OAuth-Scopes")
            raise SyncError(f"{method} {path} 失败：HTTP {error.code}。") from None
        except (OSError, ValueError) as error:
            evidence["error_type"] = type(error).__name__
            raise SyncError(f"{method} {path} 无法完成：{type(error).__name__}。") from None
        if not isinstance(result, dict):
            raise SyncError(f"{method} {path} 未返回 JSON 对象。")
        return result


def synchronize(args: argparse.Namespace, report: Dict[str, Any]) -> None:
    # 先生成全部说明，避免后续版本内容不合格时已经改写前面的 Release。
    desired = {tag: build_release_notes(tag, args.changelog) for tag in args.tag}
    token, source = get_credential()
    report["credential_source"] = source
    api = GitHubAPI(token, report)
    repository = api.request("GET", f"/repos/{REPOSITORY}")
    if repository.get("full_name", "").lower() != REPOSITORY.lower():
        raise SyncError("GitHub 返回的仓库与目标仓库不一致。")
    report["repository_permissions"] = repository.get("permissions")
    releases = []
    for tag, body in desired.items():
        release = api.request("GET", f"/repos/{REPOSITORY}/releases/tags/{tag}")
        if release.get("tag_name") != tag or not isinstance(release.get("id"), int):
            raise SyncError(f"{tag} 的远端 Release 标识不一致。")
        if release.get("draft"):
            raise SyncError(f"{tag} 是草稿，不属于已发布的历史 Release。")
        item = {"tag": tag, "id": release["id"], "url": release.get("html_url"), "status": "pending"}
        report["releases"].append(item)
        releases.append((release, body, item))
    for release, body, item in releases:
        path = f"/repos/{REPOSITORY}/releases/{release['id']}"
        if release.get("body") != body:
            # 唯一写入字段为 body，保留历史 Release、标签和所有附件。
            item["status"] = "patch_started"
            updated = api.request("PATCH", path, {"body": body})
            item["status"] = "patched_unverified"
            if updated.get("id") != release["id"] or updated.get("body") != body:
                raise SyncError(f"{item['tag']} 的 PATCH 响应与提交的说明不一致。")
            item["changed"] = True
        else:
            item["changed"] = False
        verified = api.request("GET", path)
        if verified.get("id") != release["id"] or verified.get("tag_name") != item["tag"] or verified.get("body") != body:
            raise SyncError(f"{item['tag']} 的后续 GET 未核实到期望说明。")
        item["status"] = "verified"
        item["body"] = verified["body"]
        item["verified_at"] = datetime.now(timezone.utc).isoformat()
        print(f"{item['tag']}：{'已更新' if item['changed'] else '内容已一致'}；后续 GET 已核实。")


def main() -> int:
    parser = argparse.ArgumentParser(description="仅同步指定历史 Release 的中文说明，随后 GET 核实，不修改附件或其他属性。")
    parser.add_argument("--tag", action="append", choices=HISTORICAL_TAGS, help="可重复；默认处理四个历史正式版")
    parser.add_argument("--changelog", type=Path, default=DEFAULT_CHANGELOG)
    parser.add_argument("--report", type=Path, default=Path("artifacts/release-notes-sync.json"), help="不含凭据的 JSON 操作证据")
    args = parser.parse_args()
    args.tag = args.tag or list(HISTORICAL_TAGS)
    if len(args.tag) != len(set(args.tag)):
        parser.error("同一版本标签不能重复指定。")
    report: Dict[str, Any] = {
        "repository": REPOSITORY,
        "started_at": datetime.now(timezone.utc).isoformat(),
        "requests": [],
        "releases": [],
        "success": False,
    }
    exit_code = 0
    try:
        # 写入前确认审计输出位置可用；不记录任何授权头或原始凭据响应。
        args.report.parent.mkdir(parents=True, exist_ok=True)
        with args.report.open("w", encoding="utf-8", newline="\n") as output:
            json.dump(report, output, ensure_ascii=False, indent=2)
            output.write("\n")
        synchronize(args, report)
        report["success"] = True
    except (OSError, UnicodeError, ValueError, SyncError) as error:
        report["error"] = str(error)
        print(f"同步发布说明失败：{error}", file=sys.stderr)
        exit_code = 1
    report["finished_at"] = datetime.now(timezone.utc).isoformat()
    try:
        with args.report.open("w", encoding="utf-8", newline="\n") as output:
            json.dump(report, output, ensure_ascii=False, indent=2)
            output.write("\n")
    except OSError as error:
        print(f"无法保存操作证据：{type(error).__name__}。", file=sys.stderr)
        return 1
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())

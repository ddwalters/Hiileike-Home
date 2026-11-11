#!/usr/bin/env python3
"""Retain daily, weekly, and quarterly Lore backups."""

import argparse
import json
import re
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from zoneinfo import ZoneInfo


FORMAT = "hiileike-lore-backup-v1"
FILES = {"lore.dump", "pictures.tar", "manifest.json"}
DIRECTORY_NAME = re.compile(r"\d{8}T\d{6}Z-[A-Za-z0-9]{8}")
LOCAL_ZONE = ZoneInfo("America/New_York")


@dataclass(frozen=True)
class Backup:
    path: Path
    completed_at: datetime


def managed_directory(directory: Path, root: Path) -> bool:
    if directory.is_symlink() or not directory.is_dir() or directory.resolve().parent != root:
        return False
    contents = list(directory.iterdir())
    return {file.name for file in contents} == FILES and all(
        not file.is_symlink() and file.is_file() for file in contents
    )


def read_backups(root: Path) -> list[Backup]:
    backups = []
    for directory in root.iterdir():
        if not DIRECTORY_NAME.fullmatch(directory.name) or not managed_directory(directory, root):
            continue
        try:
            manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8"))
            if not isinstance(manifest, dict) or manifest.get("format") != FORMAT:
                continue
            timestamp = manifest.get("completed_at")
            if not isinstance(timestamp, str):
                continue
            completed = datetime.fromisoformat(timestamp.replace("Z", "+00:00"))
            if completed.utcoffset() is None:
                continue
            stamp = completed.astimezone(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
            if not directory.name.startswith(stamp + "-"):
                continue
            backups.append(Backup(directory, completed))
        except (OSError, ValueError, KeyError, TypeError):
            print(f"Skipping unreadable backup metadata in {directory.name}", flush=True)
    return backups


def retained_paths(backups: list[Backup], now: datetime) -> set[Path]:
    local_now = now.astimezone(LOCAL_ZONE)
    current_week = local_now.isocalendar()[:2]
    newest = {}
    keep = set()
    for backup in backups:
        if backup.completed_at > now:
            keep.add(backup.path)
            continue

        local = backup.completed_at.astimezone(LOCAL_ZONE)
        week = local.isocalendar()[:2]
        if week == current_week:
            bucket = ("day", local.date())
        elif local.year == local_now.year:
            bucket = ("week", week)
        else:
            bucket = ("quarter", local.year, (local.month - 1) // 3 + 1)

        previous = newest.get(bucket)
        if previous is None or (backup.completed_at, backup.path.name) > (
            previous.completed_at,
            previous.path.name,
        ):
            newest[bucket] = backup

    keep.update(backup.path for backup in newest.values())
    return keep


def prune(root: Path, now: datetime, apply: bool) -> tuple[int, int]:
    root = root.resolve(strict=True)
    if root == Path(root.anchor):
        raise ValueError("Choose a dedicated backup directory.")
    backups = read_backups(root)
    keep = retained_paths(backups, now)
    remove = sorted(
        (backup for backup in backups if backup.path not in keep),
        key=lambda backup: backup.completed_at,
    )
    for backup in remove:
        if apply:
            if not managed_directory(backup.path, root):
                raise RuntimeError(f"Backup changed during pruning: {backup.path.name}")
            for name in sorted(FILES):
                (backup.path / name).unlink()
            backup.path.rmdir()
        print(f"{'Pruned' if apply else 'Would prune'} {backup.path.name}", flush=True)
    print(f"Retention: {len(keep)} kept, {len(remove)} {'pruned' if apply else 'would be pruned'}.", flush=True)
    return len(keep), len(remove)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("backup_directory", type=Path)
    parser.add_argument("--apply", action="store_true", help="Remove expired managed backups. The default is a preview.")
    args = parser.parse_args()
    prune(args.backup_directory, datetime.now(timezone.utc), args.apply)


if __name__ == "__main__":
    main()

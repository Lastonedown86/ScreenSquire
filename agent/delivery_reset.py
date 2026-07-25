"""Irreversible customer-data reset used immediately before delivery."""

from __future__ import annotations

import json
import shutil
import socket
from collections.abc import Awaitable, Callable
from pathlib import Path
from typing import Any


CommandRunner = Callable[
    [list[str], float],
    Awaitable[tuple[int, str, str]],
]


def _persist_json(path: Path, value: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(value, indent=2))
    temporary.replace(path)


async def _delete_wifi_profiles(run: CommandRunner) -> None:
    rc, out, err = await run(
        ["nmcli", "-t", "-f", "UUID,TYPE", "connection", "show"],
        15.0,
    )
    if rc != 0:
        raise RuntimeError(
            err.strip() or out.strip() or "Could not enumerate Wi-Fi profiles."
        )

    wireless_uuids = []
    for line in out.splitlines():
        if line == "":
            continue
        uuid, separator, connection_type = line.partition(":")
        uuid = uuid.strip()
        connection_type = connection_type.strip()
        if not separator or not uuid or not connection_type:
            raise RuntimeError("NetworkManager returned a malformed connection row.")
        if connection_type == "802-11-wireless":
            wireless_uuids.append(uuid)

    for connection_uuid in wireless_uuids:
        rc, delete_out, delete_err = await run(
            [
                "sudo",
                "nmcli",
                "connection",
                "delete",
                "uuid",
                connection_uuid,
            ],
            15.0,
        )
        if rc != 0:
            raise RuntimeError(
                delete_err.strip()
                or delete_out.strip()
                or f"Could not delete Wi-Fi profile {connection_uuid}."
            )


async def prepare_for_delivery(
    *,
    media_dir: Path,
    playlist_file: Path,
    name_file: Path,
    dashboard_file: Path,
    state: Any,
    empty_playlist: Any,
    set_dashboard: Callable[[dict[str, Any]], None],
    set_device_name: Callable[[str], None],
    run: CommandRunner,
    clear_controller: Callable[[], None],
) -> None:
    """Erase customer state, removing controller trust strictly last.

    Any failure before the final step deliberately leaves the controller paired
    so the builder can repair the Pi and retry the delivery reset.
    """
    for entry in media_dir.iterdir():
        if entry.is_dir() and not entry.is_symlink():
            shutil.rmtree(entry)
        else:
            entry.unlink(missing_ok=True)

    _persist_json(
        playlist_file,
        empty_playlist.model_dump(mode="json"),
    )
    state.playlist = empty_playlist
    state.index = 0
    state.override = None
    state.override_until = None

    empty_dashboard = {
        "view_data": {"boards": {}},
        "timer": {"state": "stopped"},
    }
    _persist_json(dashboard_file, empty_dashboard)
    set_dashboard(empty_dashboard)

    name_file.unlink(missing_ok=True)
    set_device_name(socket.gethostname())

    state.bump()

    await _delete_wifi_profiles(run)

    # Keep this last. Once cleared, the signed controller cannot repair or
    # retry a reset that only partly completed.
    clear_controller()

import asyncio
from urllib.parse import urlencode

import pytest
from fastapi import HTTPException


@pytest.mark.parametrize(
    "name",
    ["../escape.jpg", "/absolute.jpg", r"..\escape.jpg"],
)
def test_upload_rejects_non_plain_media_names(signed, media, name):
    path = f"/api/media?{urlencode({'name': name})}"

    response = signed(
        "POST",
        path,
        files={"file": ("upload.jpg", b"image bytes", "image/jpeg")},
    )

    assert response.status_code == 400
    assert sorted(p.name for p in media.iterdir()) == ["old.jpg"]


def test_delete_rejects_path_components_without_touching_media(
    agent_module,
    media,
):
    with pytest.raises(HTTPException) as caught:
        asyncio.run(agent_module.delete_media("../old.jpg"))

    assert caught.value.status_code == 400
    assert (media / "old.jpg").read_bytes() == b"x"


def test_rename_rejects_path_components_without_touching_media(
    agent_module,
    media,
):
    request = agent_module.RenameMediaRequest(new_name="renamed")

    with pytest.raises(HTTPException) as caught:
        asyncio.run(agent_module.rename_media("../old.jpg", request))

    assert caught.value.status_code == 400
    assert (media / "old.jpg").read_bytes() == b"x"
    assert not (media / "renamed.jpg").exists()


@pytest.mark.parametrize("source", [".", "..", "../old.jpg"])
def test_show_now_rejects_non_plain_media_names(
    agent_module,
    signed,
    media,
    source,
):
    agent_module.state.override = None

    response = signed(
        "POST",
        "/api/show-now",
        json={"type": "image", "source": source},
    )

    assert response.status_code == 400
    assert agent_module.state.override is None


def test_playlist_rejects_dot_as_media_name(agent_module, signed, media):
    response = signed(
        "PUT",
        "/api/playlist",
        json={
            "items": [{"type": "image", "source": "."}],
            "enabled": True,
        },
    )

    assert response.status_code == 400
    assert agent_module.state.playlist.items == []

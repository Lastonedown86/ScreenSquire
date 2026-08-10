"""Custom time-up sounds ride the media pipeline as type "audio"."""
from urllib.parse import urlencode


def test_upload_accepts_wav_as_audio(signed, media, client):
    path = f"/api/media?{urlencode({'name': 'horn.wav'})}"
    response = signed(
        "POST",
        path,
        files={"file": ("horn.wav", b"RIFF fake wave bytes", "audio/wav")},
    )
    assert response.status_code == 200
    assert response.json()["type"] == "audio"

    listed = {f["name"]: f for f in client.get("/api/media").json()["files"]}
    assert listed["horn.wav"]["type"] == "audio"


def test_upload_accepts_mp3_and_ogg(signed, media):
    for name, mime in (("a.mp3", "audio/mpeg"), ("b.ogg", "audio/ogg")):
        path = f"/api/media?{urlencode({'name': name})}"
        response = signed(
            "POST",
            path,
            files={"file": (name, b"audio bytes", mime)},
        )
        assert response.status_code == 200, name
        assert response.json()["type"] == "audio", name

import importlib
import sys
import tempfile
from contextlib import contextmanager
from pathlib import Path

import pytest

EMBED = ("https://www.youtube.com/embed/2B_L3WsMqTc"
         "?autoplay=1&mute=1&controls=0&loop=1&playlist=2B_L3WsMqTc")


@contextmanager
def _isolated_agent(data_dir):
    patch = pytest.MonkeyPatch()
    patch.setenv("SIGNAGE_DATA", str(data_dir))
    patch.syspath_prepend(str(Path(__file__).resolve().parent))
    sys.modules.pop("main", None)
    try:
        module = importlib.import_module("main")
        yield module
    finally:
        sys.modules.pop("main", None)
        patch.undo()


@pytest.fixture(scope="session")
def agent_module(tmp_path_factory):
    with _isolated_agent(tmp_path_factory.mktemp("signage-data-root")) as module:
        yield module


def _assert_normalize_url(agent_module):
    normalize = agent_module._normalize_url
    assert normalize("https://www.youtube.com/watch?v=2B_L3WsMqTc") == EMBED
    assert normalize("https://youtu.be/2B_L3WsMqTc") == EMBED
    assert normalize("https://www.youtube.com/watch?feature=share&v=2B_L3WsMqTc") == EMBED
    assert normalize("https://www.youtube.com/shorts/2B_L3WsMqTc") == EMBED
    assert normalize("https://www.youtube.com/live/2B_L3WsMqTc") == EMBED
    assert normalize("https://m.youtube.com/watch?v=2B_L3WsMqTc") == EMBED
    assert normalize("https://example.com/page") == "https://example.com/page"
    assert normalize("https://www.youtube.com/embed/2B_L3WsMqTc") == "https://www.youtube.com/embed/2B_L3WsMqTc"


def test_normalize_url(agent_module):
    _assert_normalize_url(agent_module)


@pytest.mark.parametrize(
    "source",
    [
        "https://notyoutube.com/watch?v=2B_L3WsMqTc",
        "https://youtube.com.evil.example/watch?v=2B_L3WsMqTc",
        "javascript:youtube.com/watch?v=2B_L3WsMqTc",
        "https://www.youtube.com/watch?v=2B_L3WsMqTcX",
    ],
)
def test_normalize_url_does_not_rewrite_lookalikes(agent_module, source):
    assert agent_module._normalize_url(source) == source


def test_normalize_url_leaves_oversized_youtube_url_unchanged(agent_module):
    source = (
        "https://www.youtube.com/watch?"
        + ("feature=share&" * 400)
        + "v=2B_L3WsMqTc"
    )
    assert len(source) > 4096

    assert agent_module._normalize_url(source) == source


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="pi-signage-normalize-test-") as data_dir:
        with _isolated_agent(data_dir) as module:
            _assert_normalize_url(module)
    print("ok")

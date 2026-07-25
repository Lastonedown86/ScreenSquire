import importlib
import sys
from pathlib import Path

import pytest

EMBED = ("https://www.youtube.com/embed/2B_L3WsMqTc"
         "?autoplay=1&mute=1&controls=0&loop=1&playlist=2B_L3WsMqTc")


@pytest.fixture(scope="session")
def agent_module(tmp_path_factory):
    patch = pytest.MonkeyPatch()
    patch.setenv("SIGNAGE_DATA", str(tmp_path_factory.mktemp("signage-data-root")))
    patch.syspath_prepend(str(Path(__file__).resolve().parent))
    sys.modules.pop("main", None)
    module = importlib.import_module("main")
    try:
        yield module
    finally:
        sys.modules.pop("main", None)
        patch.undo()


def test_normalize_url(agent_module):
    normalize = agent_module._normalize_url
    assert normalize("https://www.youtube.com/watch?v=2B_L3WsMqTc") == EMBED
    assert normalize("https://youtu.be/2B_L3WsMqTc") == EMBED
    assert normalize("https://www.youtube.com/watch?feature=share&v=2B_L3WsMqTc") == EMBED
    assert normalize("https://www.youtube.com/shorts/2B_L3WsMqTc") == EMBED
    assert normalize("https://m.youtube.com/watch?v=2B_L3WsMqTc") == EMBED
    assert normalize("https://example.com/page") == "https://example.com/page"
    assert normalize("https://www.youtube.com/embed/2B_L3WsMqTc") == "https://www.youtube.com/embed/2B_L3WsMqTc"

import pytest

EMBED = ("https://www.youtube.com/embed/2B_L3WsMqTc"
         "?autoplay=1&mute=1&controls=0&loop=1&cc_load_policy=0&playlist=2B_L3WsMqTc")


def test_normalize_url(agent_module):
    normalize = agent_module._normalize_url
    assert normalize("https://www.youtube.com/watch?v=2B_L3WsMqTc") == EMBED
    assert normalize("https://youtu.be/2B_L3WsMqTc") == EMBED
    assert normalize("https://www.youtube.com/watch?feature=share&v=2B_L3WsMqTc") == EMBED
    assert normalize("https://www.youtube.com/shorts/2B_L3WsMqTc") == EMBED
    assert normalize("https://www.youtube.com/live/2B_L3WsMqTc") == EMBED
    assert normalize("https://m.youtube.com/watch?v=2B_L3WsMqTc") == EMBED
    assert normalize("https://example.com/page") == "https://example.com/page"
    assert normalize("https://www.youtube.com/embed/2B_L3WsMqTc") == "https://www.youtube.com/embed/2B_L3WsMqTc"


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

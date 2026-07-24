"""Run: python test_normalize_url.py"""
from main import _normalize_url

EMBED = ("https://www.youtube.com/embed/2B_L3WsMqTc"
         "?autoplay=1&mute=1&controls=0&loop=1&playlist=2B_L3WsMqTc")

assert _normalize_url("https://www.youtube.com/watch?v=2B_L3WsMqTc") == EMBED
assert _normalize_url("https://youtu.be/2B_L3WsMqTc") == EMBED
assert _normalize_url("https://www.youtube.com/watch?feature=share&v=2B_L3WsMqTc") == EMBED
assert _normalize_url("https://www.youtube.com/shorts/2B_L3WsMqTc") == EMBED
assert _normalize_url("https://m.youtube.com/watch?v=2B_L3WsMqTc") == EMBED
assert _normalize_url("https://example.com/page") == "https://example.com/page"
assert _normalize_url("https://www.youtube.com/embed/2B_L3WsMqTc") == "https://www.youtube.com/embed/2B_L3WsMqTc"
print("ok")

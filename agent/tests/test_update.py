import main
from fastapi.testclient import TestClient

client = TestClient(main.app)


def test_status_reports_agent_version():
    body = client.get("/api/status").json()
    assert body["agent_version"] == main.AGENT_VERSION
    assert main.AGENT_VERSION  # non-empty

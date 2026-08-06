#!/usr/bin/env bash
# The "Python agent tests" CI job, run inside the ci-local container.
# Keep in sync with .github/workflows/ci.yml.
set -euo pipefail
cd /src

# venv lives in the container, never in the mounted repo, so the host's
# agent/.venv (Windows) is untouched
python -m venv /tmp/ci-venv
/tmp/ci-venv/bin/pip install -q -r agent/requirements.txt -r agent/requirements-dev.txt

snapshot() {
  if [[ -f agent/data/dashboard.json ]]; then
    sha256sum agent/data/dashboard.json
    stat -c '%Y' agent/data/dashboard.json
  else
    printf 'MISSING\n'
  fi
}
before="$(snapshot)"
/tmp/ci-venv/bin/python -m pytest agent -q
after="$(snapshot)"
if [[ "$before" != "$after" ]]; then
  echo "Runtime dashboard data changed during tests"
  exit 1
fi

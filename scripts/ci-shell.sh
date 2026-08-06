#!/usr/bin/env bash
# The "Shell and repository checks" CI job, run inside the ci-local container.
# Keep in sync with .github/workflows/ci.yml.
set -euo pipefail
cd /src
# the mount is owned by a different uid than the container user
git config --global --add safe.directory /src

echo "-- Validate Raspberry Pi scripts"
for script in pi-setup/*.sh; do
  bash -n "$script"
done

echo "-- Require an AGENT_VERSION bump when shipped agent files change"
base="origin/main"
relevant=$(git diff --name-only "$base...HEAD" -- agent/ | grep -Ev \
  '^agent/(tests/|test_[^/]*\.py$|.*\.md$|requirements(-dev)?\.txt$)' || true)
if [ -z "$relevant" ]; then
  echo "No shipped agent files changed."
else
  echo "Shipped agent files changed:"
  printf '  %s\n' $relevant
  extract() { sed -n 's/^AGENT_VERSION = "\([^"]*\)".*/\1/p'; }
  old=$(git show "$base:agent/main.py" | extract)
  new=$(extract < agent/main.py)
  if [ -z "$new" ]; then
    echo "Could not read AGENT_VERSION from agent/main.py"
    exit 1
  fi
  if [ "$old" = "$new" ]; then
    echo "AGENT_VERSION is still $new."
    echo "Bump it in agent/main.py or these changes will never reach a Pi."
    exit 1
  fi
  echo "AGENT_VERSION $old -> $new"
fi

echo "-- Check repository whitespace"
git log --check --oneline --all

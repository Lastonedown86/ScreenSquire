# Environment for running the Linux CI jobs locally (see ci-local.ps1).
# Matches .github/workflows/ci.yml: Python 3.13 on Debian, plus git for the
# repository checks.
FROM python:3.13-slim
RUN apt-get update \
    && apt-get install -y --no-install-recommends git \
    && rm -rf /var/lib/apt/lists/*

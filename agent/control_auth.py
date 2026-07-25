"""Canonical signing and request verification for controller mutations."""

from __future__ import annotations

import hashlib
import hmac
import re
from dataclasses import dataclass

from fastapi import HTTPException, Request, UploadFile


_HEX_SHA256 = re.compile(r"^[0-9a-fA-F]{64}$")
_DECIMAL_COUNTER = re.compile(r"^[0-9]+$")


def canonical_message(
    controller_id: str,
    counter: int,
    method: str,
    path_and_query: str,
    entity_sha256: str,
) -> str:
    """Return the one unambiguous message shared by controller and agent."""
    return "\n".join(
        (
            controller_id,
            str(counter),
            method.upper(),
            path_and_query,
            entity_sha256,
        )
    )


def sign(secret: bytes, canonical: str) -> str:
    """Return a lowercase SHA-256 HMAC for a canonical control message."""
    return hmac.new(secret, canonical.encode("utf-8"), hashlib.sha256).hexdigest()


@dataclass(frozen=True)
class VerifiedControl:
    entity_sha256: str


def _unauthorized() -> HTTPException:
    return HTTPException(401, "Valid controller signature required")


def _path_and_query(request: Request) -> str:
    raw_path = request.scope.get("raw_path")
    if isinstance(raw_path, bytes):
        path = raw_path.decode("ascii")
    else:
        path = request.url.path
    query = request.scope.get("query_string", b"")
    if query:
        path += "?" + query.decode("ascii")
    return path


async def require_control(request: Request) -> VerifiedControl:
    """Verify a signed mutation and durably consume its monotonic counter."""
    controller_id = request.headers.get("X-PiSignage-Controller")
    counter_text = request.headers.get("X-PiSignage-Counter")
    entity_sha256 = request.headers.get("X-PiSignage-Entity-SHA256")
    supplied_signature = request.headers.get("X-PiSignage-Signature")

    if (
        not controller_id
        or len(controller_id) > 64
        or counter_text is None
        or _DECIMAL_COUNTER.fullmatch(counter_text) is None
        or entity_sha256 is None
        or supplied_signature is None
        or _HEX_SHA256.fullmatch(entity_sha256) is None
        or _HEX_SHA256.fullmatch(supplied_signature) is None
    ):
        raise _unauthorized()
    try:
        counter = int(counter_text, 10)
    except ValueError:
        raise _unauthorized() from None

    content_type = request.headers.get("content-type", "")
    if not content_type.lower().startswith("multipart/form-data"):
        actual_entity_sha256 = hashlib.sha256(await request.body()).hexdigest()
        if not hmac.compare_digest(actual_entity_sha256, entity_sha256.lower()):
            raise HTTPException(400, "Request content hash does not match signature")

    trust_store = request.app.state.trust_store
    secret = trust_store.controller_secret(controller_id)
    if secret is None:
        raise _unauthorized()
    canonical = canonical_message(
        controller_id,
        counter,
        request.method,
        _path_and_query(request),
        entity_sha256,
    )
    expected_signature = sign(secret, canonical)
    if not hmac.compare_digest(expected_signature, supplied_signature.lower()):
        raise _unauthorized()
    if not trust_store.accept_counter(controller_id, counter):
        raise HTTPException(409, "Control counter was already used")
    return VerifiedControl(entity_sha256=entity_sha256.lower())


async def verify_uploaded_entity(file: UploadFile, expected_hex: str) -> None:
    """Hash an uploaded file exactly, then rewind it for the route handler."""
    digest = hashlib.sha256()
    try:
        while chunk := await file.read(1024 * 1024):
            digest.update(chunk)
    finally:
        await file.seek(0)
    if not hmac.compare_digest(digest.hexdigest(), expected_hex.lower()):
        raise HTTPException(400, "Uploaded content hash does not match signature")

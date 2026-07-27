## Summary

Describe the focused change and why it is needed.

## Verification

- [ ] Python agent tests pass, or this change cannot affect Python.
- [ ] `AGENT_VERSION` is bumped, or no file that ships to a Pi changed.
- [ ] .NET tests and Release build pass, or this change cannot affect .NET.
- [ ] Changed Raspberry Pi shell scripts pass `bash -n`.
- [ ] Real-Pi claims are backed by observed hardware evidence.
- [ ] No credentials, Recovery PINs, controller secrets, client/store details,
      or personal machine paths are included.

## Operational impact

Describe provisioning, recovery, compatibility, migration, or rollback impact.
Write `None` when there is no operational impact.

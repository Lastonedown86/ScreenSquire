# ScreenSquire Clean Public Launch Design

**Date:** 2026-07-25
**Status:** Approved

## Objective

Publish the existing project as the public GitHub repository
`Lastonedown86/ScreenSquire` without exposing personal or client information,
while preserving its useful history and establishing guardrails appropriate for
a solo maintainer.

The installed Windows application remains named **Pi Signage Control**. Its
visible name and branding may be changed independently in a future project
without intentionally changing its underlying upgrade identity.

## Public Identity and Privacy

- The repository name is `ScreenSquire`.
- The repository owner is `Lastonedown86`.
- The repository is licensed under the MIT License.
- Existing unpublished commit history is preserved, but rewritten to replace
  the author's personal email address with the account's GitHub-provided
  `users.noreply.github.com` address.
- Machine-specific absolute paths are removed from tracked documentation.
- The complete rewritten history is scanned before publication for:
  - secrets and credential-like values;
  - the original personal email address;
  - local Windows user paths;
  - client names, store details, real Wi-Fi credentials, and real provisioning
    PINs.
- Only the intended `main` branch is published. Actual Git recovery refs and
  local backup artifacts are never pushed; documented recovery procedures and
  legitimate functional backup code are allowed.

## Repository Documentation

The public repository will include:

- a README explaining ScreenSquire, its Raspberry Pi agent and Windows control
  application, the USB Wi-Fi provisioning workflow, setup requirements, and
  verified development commands;
- an MIT `LICENSE`;
- concise `CONTRIBUTING.md`, `SECURITY.md`, and support guidance;
- issue and pull-request templates suitable for a solo-maintained project;
- status badges for required continuous-integration and security checks.

Documentation and examples must use placeholders rather than production
credentials, device PINs, client names, or store information.

## Continuous Integration

GitHub Actions will reproduce the repository's established verification:

1. Run the Python agent tests and prove that the test run does not modify the
   runtime dashboard data file.
2. Run the .NET test suite.
3. Build the .NET solution in Release configuration.
4. Validate the Bash syntax of Raspberry Pi setup scripts.
5. Run basic repository hygiene checks, including `git diff --check`.

Jobs will have stable, descriptive names so the corresponding checks can be
required by the `main` ruleset. Workflows receive only the minimum permissions
needed for their tasks.

## Security Automation

The public repository will use GitHub's public-repository security features:

- Dependabot update pull requests for the package ecosystems present in the
  repository and for GitHub Actions;
- CodeQL analysis for C# and Python;
- dependency review on pull requests;
- secret scanning and push protection;
- private vulnerability reporting.

`SECURITY.md` will direct security reports to private vulnerability reporting
and will not publish personal contact details.

## Main Branch Governance

Normal changes reach `main` through pull requests. The repository ruleset will:

- require a pull request before merging;
- require zero approving reviews because the repository has one maintainer;
- require the launch CI checks to pass;
- require all review conversations to be resolved;
- require linear history;
- block force-pushes;
- block branch deletion.

The repository administrator retains a bypass for genuine recovery situations.
This bypass is emergency-only and is not the normal development path. Merged
feature branches are deleted automatically.

## Launch Sequence

1. Create and verify a local Git bundle containing the exact pre-launch
   repository state.
2. Add the approved public documentation, automation, and repository metadata.
3. Rewrite the unpublished history for privacy.
4. Run all local tests, builds, shell validation, repository checks, and the
   full-history privacy scan.
5. Create `Lastonedown86/ScreenSquire` as a private staging repository.
6. Push only `main` and allow the initial GitHub Actions run to finish.
7. Proceed only if the required checks pass.
8. Change the repository visibility to public.
9. Enable the approved security features and apply the `main` ruleset.
10. Verify repository visibility, default branch, security settings, ruleset,
    check requirements, and automatic branch deletion from GitHub.
11. Set and verify the local `origin` URL.
12. Clone the public repository into a clean temporary location and repeat the
    documented build and test commands.
13. Retain the recovery bundle until the public launch and clean-clone
    verification are complete.

Nothing becomes public before the local privacy scan and verification pass.
Once data has been pushed to a public repository, it is treated as permanently
disclosed; deleting or rewriting it afterward is not considered a privacy
control.

## Failure and Recovery Behavior

- If documentation, tests, builds, shell validation, or privacy scans fail,
  stop before creating or changing public state.
- If history rewriting produces an incorrect result, restore the repository
  from the verified Git bundle and repeat the rewrite.
- If the private staging CI run fails, keep the repository private, correct the
  problem through the normal implementation workflow, and rerun verification.
- If a GitHub security or ruleset setting cannot be enabled, keep the
  repository private until the limitation is understood or the design is
  explicitly revised.
- If post-publication clean-clone verification fails, do not publish a release;
  correct the repository through a pull request while preserving public
  history.

## Success Criteria

The launch is complete when:

- `Lastonedown86/ScreenSquire` is public and uses `main` as its default branch;
- the MIT license and public project documentation render correctly;
- no identified personal, client, credential, PIN, or machine-path data is
  present anywhere in published history;
- all required GitHub Actions checks pass;
- the `main` ruleset and automatic branch deletion are verified;
- CodeQL, Dependabot, secret scanning, push protection, dependency review, and
  private vulnerability reporting are configured as designed;
- a clean clone passes the documented validation commands;
- the local checkout points to the verified public repository.

## Out of Scope

This launch does not:

- rename or rebrand the installed Windows application;
- publish an installer or binary release;
- add code signing;
- redesign USB provisioning, device recovery, or remote support;
- introduce production credentials, shared provisioning PINs, or client
  configuration into the repository.

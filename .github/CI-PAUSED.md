# CI paused

GitHub Actions CI is stopped at the repository owner's request (2026-10-14).

Workflow definitions are preserved unchanged in `disabled-workflows/`, outside GitHub's `.github/workflows/` execution directory. Do not restore them or add new active workflows without an explicit request from the owner. Continue build and regression testing locally; Docker is not required for the local host-analysis path.

This change prevents new workflow runs from this branch. It does not cancel runs that were already queued or started, and historical branches may still contain their own workflow definitions. Repository-wide Actions disable/cancellation is a separate administrative operation.

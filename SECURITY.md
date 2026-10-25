# Reporting security issues

Tenka-chan is a local developer tool that can evaluate MSBuild projects and
launch programs with the **current operating-system user's privileges**.
A path allowlist and user-facing approval gates are not an operating-system
sandbox. Treat analyzed projects, generated experiments and downloaded samples
as executable code.

**Do not post credentials, exploit payloads that contain private data or
unredacted MCP connection descriptors in a public issue or pull request.**
Use GitHub's private vulnerability reporting facility if it has been enabled
for this repository, or contact the repository maintainer through a private
channel before public disclosure. No response SLA is currently promised.

Before submitting a report:
- State the affected commit/version and operating environment.
- Describe a minimal reproduction using non-sensitive fixture code.
- Remove local paths, bearer tokens and private project content from logs.
- State whether an explicitly authorized build/host execution was required.

For operational trust boundaries, read [docs/security.md](docs/security.md).
For public-release history/credential checks, see
[docs/PUBLIC-RELEASE-CHECKLIST.md](docs/PUBLIC-RELEASE-CHECKLIST.md).

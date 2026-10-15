"""Small stdlib-only helpers for local MCP protocol validation. No model client."""
import hashlib
import json
import os
import re
from pathlib import Path
from urllib.parse import urlsplit, urlunsplit


class ProbeError(RuntimeError):
    """Failure safe to print without source bodies or machine credentials."""


def default_paths(root: Path) -> tuple[Path, Path]:
    root = root.resolve()
    override = os.environ.get('TENKA_AGENT_CONNECTION_FILE')
    if override:
        descriptor = Path(override).resolve()
    else:
        local = os.environ.get('LOCALAPPDATA')
        if not local:
            raise ProbeError('Specify --connection-file outside Windows, or start via setup.bat first.')
        key = hashlib.sha256(str(root).lower().encode('utf-8')).hexdigest()[:16].upper()
        descriptor = Path(local) / 'TenkaChan' / 'launcher' / key / 'agent-connection.json'
    adapter = root / 'src/TenkaChan.Mcp/bin/Release/net10.0/TenkaChan.Mcp.dll'
    return descriptor, adapter


def validate_descriptor(path: Path) -> tuple[str, str, str]:
    if not path.is_file():
        raise ProbeError('Connection file not found. Start this checkout with start.bat first.')
    # Read a bounded number of bytes rather than trusting a preceding stat alone.
    with path.open('rb') as stream:
        raw = stream.read(16001)
    if len(raw) > 16000:
        raise ProbeError('Connection file is too large.')
    try:
        saved = json.loads(raw.decode('utf-8-sig'))
        if not isinstance(saved, dict) or type(saved.get('schema')) is not int or saved['schema'] != 1:
            raise ValueError()
        endpoint = saved['endpoint']
        if not isinstance(endpoint, str) or any(ord(c) < 33 or ord(c) == 127 for c in endpoint):
            raise ValueError()
        uri = urlsplit(endpoint)
        token, instance = saved['token'], saved['instanceId']
        if not isinstance(token, str) or not re.fullmatch('[0-9A-Fa-f]{64}', token):
            raise ValueError()
        if not isinstance(instance, str) or not re.fullmatch('[0-9A-Fa-f]{32}', instance):
            raise ValueError()
        if (uri.scheme != 'http' or uri.hostname not in ('localhost', '127.0.0.1', '::1')
                or uri.username is not None or uri.password is not None or uri.path not in ('', '/')
                or uri.query or uri.fragment or uri.port is None or not 1024 <= uri.port <= 65535):
            raise ValueError()
        host = '[::1]' if uri.hostname == '::1' else '127.0.0.1'
        return urlunsplit(('http', f'{host}:{uri.port}', '', '', '')), token, instance
    except (ValueError, KeyError, TypeError, UnicodeError):
        # Never include saved values/token in validation exceptions.
        raise ProbeError('Invalid connection descriptor; only a loopback origin and valid local credential are allowed.') from None


def expected_tool_names(root: Path) -> set[str]:
    """Read only tool classes explicitly registered by the current MCP entry point."""
    program = (root / 'src/TenkaChan.Mcp/Program.cs').read_text(encoding='utf-8-sig')
    classes = re.findall(r'\.WithTools<([A-Za-z_][A-Za-z0-9_]*)>\(\)', program)
    if not classes or len(classes) != len(set(classes)):
        raise ProbeError('Could not uniquely read registered MCP tool classes from this checkout.')
    names: set[str] = set()
    for name in classes:
        file = root / 'src/TenkaChan.Mcp/Agent' / (name + '.cs')
        if not file.is_file():
            raise ProbeError('A registered tool class is missing. Update the checkout before testing.')
        found = re.findall(r'McpServerTool\(Name\s*=\s*"([^"]+)"', file.read_text(encoding='utf-8-sig'))
        if not found or len(found) != len(set(found)) or names.intersection(found):
            raise ProbeError('Registered tool names are missing or duplicated.')
        names.update(found)
    return names


def list_tools(send) -> dict[str, dict]:
    """Follow MCP tools/list pagination without unbounded cursor loops."""
    tools: dict[str, dict] = {}
    cursor, seen = None, set()
    for _ in range(40):
        data = send('tools/list', {} if cursor is None else {'cursor': cursor})
        if not isinstance(data, dict) or not isinstance(data.get('tools'), list):
            raise ProbeError('Invalid tools/list response.')
        for tool in data['tools']:
            if not isinstance(tool, dict) or not isinstance(tool.get('name'), str) or not tool['name'] or tool['name'] in tools:
                raise ProbeError('Duplicate or invalid MCP tool name.')
            if len(tools) >= 400:
                raise ProbeError('Tool catalogue exceeded the probe limit.')
            tools[tool['name']] = tool
        cursor = data.get('nextCursor')
        if cursor is None:
            return tools
        if not isinstance(cursor, str) or not cursor or len(cursor) > 4096 or cursor in seen:
            raise ProbeError('Invalid or repeated tools/list cursor.')
        seen.add(cursor)
    raise ProbeError('Tool pagination exceeded the probe limit.')


def validate_tools(tools: dict[str, dict], expected: set[str]) -> None:
    if set(tools) != expected:
        raise ProbeError('MCP tools do not match this checkout; stop the adapter and rebuild. '
                         f'Missing: {sorted(expected - set(tools))}; extra: {sorted(set(tools) - expected)}')
    # Same tool count can still be an OLD binary: also check newly connected parameters.
    for name, fields in {
        'tenka_prepare_experiment': ('projectId', 'targetFramework', 'runtimeFramework'),
        'tenka_review_queue': ('snapshotId', 'cursor', 'trackableOnly'),
        'tenka_mark_review': ('expectedRevision', 'sourceHash', 'contextHash'),
    }.items():
        schema = tools[name].get('inputSchema', {})
        properties = schema.get('properties', {}) if isinstance(schema, dict) else {}
        if not isinstance(properties, dict) or any(f not in properties for f in fields):
            raise ProbeError(f'{name} has an old/missing input schema; stop and rebuild the adapter.')

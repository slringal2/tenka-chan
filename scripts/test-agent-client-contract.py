#!/usr/bin/env python3
"""Offline SOURCE guards only; these do not compile or execute C#/MCP.

Run the authored DashboardAgentClientFailureTests with dotnet test separately.
No network, target execution, new dependency, or GitHub workflow is involved.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
CLIENT = (ROOT / 'src/TenkaChan.Mcp/Agent/DashboardAgentClient.cs').read_text(encoding='utf-8')
FAILURE = (ROOT / 'src/TenkaChan.Mcp/Agent/AgentClientFailure.cs').read_text(encoding='utf-8')
TESTS = (ROOT / 'tests/TenkaChan.Tests/DashboardAgentClientFailureTests.cs').read_text(encoding='utf-8')


class AgentClientSourceContract(unittest.TestCase):
    def test_only_explicit_client_safe_failures_use_mcp_exception(self):
        self.assertIn('AgentClientFailure : McpException', FAILURE)
        self.assertIn('using ModelContextProtocol;', FAILURE)
        self.assertNotIn('ex.Message', CLIENT)
        self.assertNotIn('ex.ToString()', CLIENT)

    def test_local_transport_policy_remains(self):
        for fragment in ('AllowAutoRedirect = false', 'UseProxy = false',
                         'ConnectTimeout = TimeSpan.FromSeconds(5)',
                         'uri.Scheme != "http"', 'uri.AbsolutePath != "/"',
                         'saved.Token.Length != 64', 'endpoint = ValidateConnection(saved)'):
            self.assertIn(fragment, CLIENT)

    def test_deadline_covers_headers_and_body(self):
        for fragment in ('CancellationTokenSource.CreateLinkedTokenSource(ct)',
                         'deadline.CancelAfter(_requestTimeout)', 'Timeout = Timeout.InfiniteTimeSpan',
                         'HttpCompletionOption.ResponseHeadersRead, deadline.Token',
                         '12_000_000 : 16_000, deadline.Token'):
            self.assertIn(fragment, CLIENT)

    def test_caller_cancellation_is_not_mislabelled(self):
        self.assertIn('catch (OperationCanceledException) when (!ct.IsCancellationRequested)', CLIENT)
        self.assertIn('CallerCancellationIsNotConvertedToTimeoutAndGateIsReleased', TESTS)

    def test_call_and_failure_keep_request_correlation(self):
        self.assertIn('SendAsync("/agent/v1/call", request, ct, request.RequestId)', CLIENT)
        self.assertIn('request.RequestId, reply.CallId', CLIENT)
        self.assertIn('automaticRetry = false', FAILURE)

    def test_duplicate_requests_are_not_queued(self):
        self.assertEqual(3, CLIENT.count('_gate.WaitAsync(0, ct)'))
        self.assertEqual(3, CLIENT.count('finally { _gate.Release(); }'))
        self.assertEqual(1, CLIENT.count('SendAsync("/agent/v1/call"'))
        self.assertIn('ConcurrentCallIsRejectedWithoutQueueing', TESTS)

    def test_safe_error_dto_is_allowlisted(self):
        for fragment in ('400 or 403 or 408 or 409 or 413', '"application/json"',
                         'TryGetProperty("error"', 'error.ValueKind == JsonValueKind.String'):
            self.assertIn(fragment, FAILURE)

    def test_body_limits_also_apply_without_content_length(self):
        for fragment in ('content.Headers.ContentLength > limit',
                         'limit + 1L - buffer.Length', 'buffer.Length + read > limit'):
            self.assertIn(fragment, CLIENT)

    def test_redaction_precedes_truncation(self):
        self.assertLess(FAILURE.index('value.Replace(token,'), FAILURE.index('.Take(limit)'))
        self.assertLess(FAILURE.index('Bearer.Replace('), FAILURE.index('.Take(limit)'))
        self.assertLess(FAILURE.index('HexCredential.Replace('), FAILURE.index('.Take(limit)'))
        self.assertIn('StringComparison.OrdinalIgnoreCase', FAILURE)

    def test_success_and_session_ids_are_validated(self):
        self.assertIn('Guid.TryParseExact(reply.CallId, "N"', CLIENT)
        self.assertIn('reply.Outcome is not ("returned" or "failed" or "cancelled")', CLIENT)
        self.assertIn('Guid.TryParseExact(id.GetString(), "N"', CLIENT)

    def test_descriptor_errors_have_safe_recovery(self):
        self.assertIn('"connection_file_missing"', CLIENT)
        self.assertIn('"connection_file_invalid"', CLIENT)
        self.assertIn('IOException or InvalidDataException or UnauthorizedAccessException or JsonException', CLIENT)

    def test_live_contract_cases_are_authored_not_claimed_executed(self):
        for name in ('BodyTimeoutRetainsRequestIdAndDoesNotReplay',
                     'TransportFailureDoesNotLeakExceptionOrReplay',
                     'ExplicitReconnectCanRecoverWhenOldEndIsRejected',
                     'SuccessEnvelopeAndFinishRemainCompatible'):
            self.assertIn(name, TESTS)


if __name__ == '__main__':
    unittest.main(verbosity=2)

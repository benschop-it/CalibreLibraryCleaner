import errno
import copy
import os
from pathlib import Path
import tempfile
import types
import unittest
from unittest.mock import Mock, patch


WORKER_PATH = Path(__file__).resolve().parents[1] / "src" / \
    "CalibreLibraryCleaner.Infrastructure" / "Calibre" / "calibre_mutation_worker.py"


class ManagedNameWorkerChecks(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="clc-worker-checks-")
        self.addCleanup(self.directory.cleanup)
        self.worker = types.ModuleType("managed_name_worker_checks")
        with patch.dict(os.environ, {"CLC_WORKER_TEMP_DIRECTORY": self.directory.name}):
            exec(compile(WORKER_PATH.read_text(encoding="utf-8"), str(WORKER_PATH), "exec"), self.worker.__dict__)
        self.sharing_probe = patch.object(self.worker, "probe_managed_name_sharing")
        self.sharing_probe.start()
        self.addCleanup(self.sharing_probe.stop)
        self.worker.LIBRARY_ROOT = self.directory.name
        self.cache = Mock()
        self.cache.field_for.side_effect = lambda field, book_id: "Writer/Title ({})".format(book_id)
        self.state = {
            "metadata": {}, "local": {}, "cover": None, "formats": [],
            "managedPath": "Writer/Title (1)", "identity": {}, "authorSortValues": [],
            "extraFiles": [], "composites": {},
        }

    @staticmethod
    def operation(record_id):
        return {
            "operationId": "refresh:{}".format(record_id),
            "kind": "refreshManagedName",
            "recordId": record_id,
            **dict.fromkeys((
                "canonicalFormat", "targetRecordId", "expectedSizeInBytes", "expectedSha256",
                "metadataField", "metadataValues", "metadataAuthorSortValues", "metadataSource",
                "stagedCoverFileName", "stagedCoverSizeInBytes", "stagedCoverSha256",
                "quarantineTitle", "quarantineAuthors", "quarantineAuthorSorts", "quarantineFormats",
            )),
        }

    def execute(self, count=1):
        return self.worker.execute_chunk(self.cache, None, None, None, {
            "protocolVersion": self.worker.PROTOCOL_VERSION,
            "kind": "executeChunk",
            "chunkId": "generated-managed-names",
            "operations": [self.operation(index) for index in range(1, count + 1)],
        })

    def test_api_errors_identify_phase_without_payload(self):
        for phase in ("before_readback", "update_path", "clear_caches", "after_readback", "verification"):
            with self.subTest(phase=phase):
                self.cache.reset_mock(side_effect=True)
                self.cache.field_for.side_effect = lambda field, book_id: "Writer/Title ({})".format(book_id)
                exception = OSError(errno.EACCES, "PRIVATE TITLE AND PATH", r"C:\PRIVATE\BOOK")
                exception.winerror = 32
                states = [self.state, self.state]
                if phase == "before_readback":
                    states[0] = exception
                elif phase == "after_readback":
                    states[1] = exception
                elif phase == "verification":
                    self.cache.field_for.side_effect = exception
                else:
                    getattr(self.cache, phase).side_effect = exception
                    if phase == "update_path":
                        states[1] = dict(self.state, metadata={"changed": True})
                with patch.object(self.worker, "managed_record_state", side_effect=states), \
                        patch.object(self.worker, "managed_format_state", return_value=[]), \
                        patch.object(self.worker.os.path, "isdir", return_value=True):
                    response = self.execute()
                code = "managed_name_{}_os_error_errno_13_winerror_32".format(phase)
                self.assertEqual(code, response.get("failureCode"))
                self.assertTrue(response["mutationStarted"])
                self.assertFalse(response["operationResults"][0]["isSuccess"])
                self.assertEqual(code, response["operationResults"][0]["failureCode"])
                self.assertNotIn("PRIVATE", str(response))

    def test_error_categories_and_numeric_bounds_do_not_echo_external_values(self):
        exceptions = [
            (RuntimeError("PRIVATE"), "runtime_error"),
            (ValueError("PRIVATE"), "value_error"),
            (KeyError("PRIVATE"), "lookup_error"),
            (TypeError("PRIVATE"), "type_error"),
            (OverflowError("PRIVATE"), "arithmetic_error"),
            (Exception("PRIVATE"), "unexpected_error"),
        ]
        os_error = OSError(-1, "PRIVATE")
        os_error.winerror = 10 ** 30
        exceptions.append((os_error, "os_error"))
        for exception, category in exceptions:
            with self.subTest(category=category):
                self.cache.update_path.side_effect = exception
                with patch.object(self.worker, "managed_record_state",
                                  side_effect=[self.state, dict(self.state, metadata={"changed": True})]):
                    response = self.execute()
                self.assertEqual("managed_name_update_path_" + category, response.get("failureCode"))
                self.assertNotIn("PRIVATE", str(response))
                self.assertLessEqual(len(response["failureCode"]), 128)

    def test_existing_worker_validation_codes_remain_unchanged(self):
        with patch.object(self.worker, "managed_record_state",
                          side_effect=ValueError("managed_name_layout_invalid")):
            response = self.execute()
        self.assertEqual("managed_name_layout_invalid", response["failureCode"])
        self.assertFalse(response["operationResults"][0]["isSuccess"])

    def test_failure_at_operation_59_stops_without_a_successful_tail(self):
        changed = False
        def update(book_ids):
            nonlocal changed
            if book_ids == {59}:
                changed = True
                raise PermissionError(errno.EACCES, "PRIVATE")
        self.cache.update_path.side_effect = update
        def readback(cache, book_id):
            return dict(self.state, metadata={"changed": changed and book_id == 59})
        with patch.object(self.worker, "managed_record_state", side_effect=readback), \
                patch.object(self.worker, "managed_format_state", return_value=[]), \
                patch.object(self.worker.os.path, "isdir", return_value=True):
            response = self.execute(count=100)
        results = response["operationResults"]
        self.assertTrue(response["mutationStarted"])
        self.assertEqual(100, len(results))
        self.assertEqual(58, sum(result["isSuccess"] for result in results))
        self.assertTrue(all(not result["isSuccess"] for result in results[58:]))
        self.assertEqual("managed_name_update_path_os_error_errno_13", response["failureCode"])
        self.assertEqual(59, self.cache.update_path.call_count)
        self.assertEqual({59}, self.cache.update_path.call_args.args[0])

    def test_unchanged_rejection_at_operation_59_continues_without_retry(self):
        def update(book_ids):
            if book_ids == {59}:
                raise PermissionError(errno.EACCES, "PRIVATE")
        self.cache.update_path.side_effect = update
        with patch.object(self.worker, "managed_record_state", return_value=self.state), \
                patch.object(self.worker, "managed_format_state", return_value=[]), \
                patch.object(self.worker.os.path, "isdir", return_value=True):
            response = self.execute(count=100)
        self.assertIsNone(response.get("failureCode"))
        results = response["operationResults"]
        self.assertTrue(all(result["isSuccess"] for result in results))
        self.assertEqual([58], [index for index, result in enumerate(results) if result["isSkipped"]])
        self.assertEqual("managed_name_update_path_os_error_errno_13", results[58]["skipCode"])
        self.assertNotIn("verifiedManagedPath", results[58])
        self.assertNotIn("verifiedFormats", results[58])
        self.assertNotIn("PRIVATE", str(response))
        self.assertEqual(100, self.cache.update_path.call_count)
        self.assertEqual(1, sum(call.args == ({59},) for call in self.cache.update_path.call_args_list))

    def test_every_protected_state_difference_prevents_a_skip(self):
        for component in self.state:
            with self.subTest(component=component):
                changed = copy.deepcopy(self.state)
                changed[component] = "changed"
                self.cache.update_path.side_effect = PermissionError(errno.EACCES, "PRIVATE")
                with patch.object(self.worker, "managed_record_state", side_effect=[self.state, changed]):
                    response = self.execute()
                self.assertIn("failureCode", response)
                self.assertFalse(response["operationResults"][0]["isSuccess"])
                self.assertFalse(response["operationResults"][0]["isSkipped"])

    def test_unreadable_rejection_post_state_still_stops(self):
        self.cache.update_path.side_effect = PermissionError(errno.EACCES, "PRIVATE")
        with patch.object(self.worker, "managed_record_state",
                          side_effect=[self.state, OSError(errno.EIO, "PRIVATE")]):
            response = self.execute()
        self.assertIn("failureCode", response)
        self.assertFalse(response["operationResults"][0]["isSuccess"])
        self.assertNotIn("PRIVATE", str(response))

    def test_preflight_sharing_rejection_never_calls_update_path(self):
        error = PermissionError(errno.EACCES, "PRIVATE")
        error.winerror = 32
        self.worker.probe_managed_name_sharing.side_effect = error
        with patch.object(self.worker, "managed_record_state", return_value=self.state):
            response = self.execute()
        self.assertIsNone(response.get("failureCode"))
        result = response["operationResults"][0]
        self.assertTrue(result["isSkipped"])
        self.assertEqual("managed_name_preflight_os_error_errno_13_winerror_32", result["skipCode"])
        self.cache.update_path.assert_not_called()

    def test_in_place_state_mutation_cannot_pass_the_unchanged_proof(self):
        def update(book_ids):
            self.state["metadata"]["title"] = "changed"
            raise PermissionError(errno.EACCES, "PRIVATE")
        self.cache.update_path.side_effect = update
        with patch.object(self.worker, "managed_record_state", return_value=self.state):
            response = self.execute()
        self.assertIsNotNone(response.get("failureCode"))
        self.assertFalse(response["operationResults"][0]["isSkipped"])

    def test_preflight_failure_also_requires_fresh_unchanged_readback(self):
        for after in (dict(self.state, managedPath="changed"), OSError(errno.EIO, "PRIVATE")):
            with self.subTest(unreadable=isinstance(after, Exception)):
                self.worker.probe_managed_name_sharing.side_effect = PermissionError(errno.EACCES, "PRIVATE")
                with patch.object(self.worker, "managed_record_state", side_effect=[self.state, after]):
                    response = self.execute()
                self.assertIsNotNone(response.get("failureCode"))
                self.assertFalse(response["operationResults"][0]["isSkipped"])
        self.cache.update_path.assert_not_called()

    def test_quarantine_comparison_keeps_record_identity_separate_from_copied_content(self):
        destination = copy.deepcopy(self.state)
        destination["identity"] = {"uuid": "generated-destination", "last_modified": "different"}
        destination["managedPath"] = "Copied Writer/Copied Title (2)"

        self.assertEqual(self.worker.comparable_record_state(self.state),
                         self.worker.comparable_record_state(destination))

    def test_successful_naming_reuses_the_complete_post_update_format_readback(self):
        state = copy.deepcopy(self.state)
        state["formats"] = [{
            "format": "EPUB", "storedFileName": "book.epub",
            "relativePath": "Writer/Title (1)/book.epub", "sizeInBytes": 10,
            "sha256": "a" * 64, "observation": {"length": 10},
        }]
        with patch.object(self.worker, "managed_record_state", return_value=state) as snapshots, \
                patch.object(self.worker, "managed_format_state", return_value=state["formats"]) as extra_format_read, \
                patch.object(self.worker.os.path, "isdir", return_value=True):
            response = self.execute()
        self.assertIsNone(response.get("failureCode"))
        self.assertTrue(response["operationResults"][0]["isSuccess"])
        self.assertEqual(2, snapshots.call_count)
        extra_format_read.assert_not_called()
        self.assertEqual("a" * 64, response["operationResults"][0]["verifiedFormats"][0]["sha256"])


if __name__ == "__main__":
    unittest.main()

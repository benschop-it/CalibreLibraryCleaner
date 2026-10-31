import argparse
import hashlib
import io
import json
import os
import shutil
import sqlite3
import subprocess
import sys


EXPECTED_MARKER = "I_CONFIRM_THE_QUARANTINE_WORKER_ROOT_IS_DISPOSABLE"
PROTOCOL_VERSION = "calibre-mutation-worker-protocol/1.7"


def require(condition, code):
    if not condition:
        raise RuntimeError(code)


def write_message(process, value):
    process.stdin.write(json.dumps(value, ensure_ascii=True, separators=(",", ":")) + "\n")
    process.stdin.flush()


def read_message(process):
    line = process.stdout.readline()
    require(bool(line), "worker_response_missing")
    return json.loads(line)


def operation(operation_id, kind, record_id, expectation):
    return {
        "operationId": operation_id,
        "kind": kind,
        "recordId": record_id,
        "canonicalFormat": None,
        "targetRecordId": None,
        "expectedSizeInBytes": None,
        "expectedSha256": None,
        "metadataField": None,
        "metadataValues": None,
        "metadataAuthorSortValues": None,
        "metadataSource": None,
        "stagedCoverFileName": None,
        "stagedCoverSizeInBytes": None,
        "stagedCoverSha256": None,
        "quarantineTitle": expectation["title"],
        "quarantineAuthors": expectation["authors"],
        "quarantineAuthorSorts": expectation["authorSorts"],
        "quarantineFormats": expectation["formats"],
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("calibre_debug")
    parser.add_argument("worker_script")
    parser.add_argument("probe_root")
    parser.add_argument("confirmation")
    arguments = parser.parse_args()

    require(arguments.confirmation == EXPECTED_MARKER, "disposable_confirmation_missing")
    calibre_debug = os.path.realpath(arguments.calibre_debug)
    worker_script = os.path.realpath(arguments.worker_script)
    probe_root = os.path.realpath(arguments.probe_root)
    require(os.path.isfile(calibre_debug), "calibre_debug_missing")
    require(os.path.isfile(worker_script), "worker_script_missing")
    require(not os.path.exists(probe_root), "probe_root_must_be_absent")

    from calibre.ebooks.metadata.book.base import Metadata
    from calibre.library import db
    from calibre.utils.img import create_canvas, image_to_data

    source_root = os.path.join(probe_root, "S")
    destination_root = os.path.join(probe_root, "S - Unresolvable")
    worker_temp = os.path.join(probe_root, "worker-temp")
    config_root = os.path.join(probe_root, "config")
    require(len(destination_root) < 75, "destination_library_path_too_long")
    process = None
    source_legacy = None
    source = None
    verify_source_legacy = None
    verify_source = None
    verify_destination_legacy = None
    verify_destination = None
    try:
        os.makedirs(worker_temp)
        os.makedirs(config_root)
        source_legacy = db(source_root, read_only=False)
        source = source_legacy.new_api
        source.init()
        source.create_custom_column(
            "clc_worker_probe",
            "CLC Worker Probe",
            "text",
            False,
            True,
            {},
        )
        source.close()
        source = None
        source_legacy.close()
        source_legacy = db(source_root, read_only=False)
        source = source_legacy.new_api
        source.init()

        metadata = Metadata("Worker Quarantine Probe", ["Probe, Author", "Other, Writer"])
        metadata.tags = ["Probe Tag"]
        metadata.identifiers = {"clc-probe": "worker"}
        source_id = source.create_book_entry(metadata, add_duplicates=True)
        source.set_field("#clc_worker_probe", {source_id: "Custom Value"})
        author_ids = tuple(source.field_ids_for("authors", source_id))
        require(len(author_ids) == 2, "source_author_order_missing")
        source.set_sort_for_authors(dict(zip(author_ids, ("Probe, Author", "Other, Writer"))))
        format_bytes = b"Fixed worker quarantine acceptance\n"
        require(
            source.add_format(source_id, "TXT", io.BytesIO(format_bytes), replace=False, run_hooks=False),
            "source_format_add_failed",
        )
        source.set_cover({
            source_id: image_to_data(create_canvas(32, 32, "#336699"), fmt="JPEG"),
        })
        source_uuid = str(source.library_id)
        format_path = source.format_abspath(source_id, "TXT")
        expectation = {
            "title": source.field_for("title", source_id),
            "authors": list(source.field_for("authors", source_id)),
            "authorSorts": [source.author_data(author_ids)[author_id]["sort"] for author_id in author_ids],
            "formats": [{
                "format": "TXT",
                "expectedRelativePath": os.path.relpath(format_path, source_root).replace("\\", "/"),
                "expectedSizeInBytes": len(format_bytes),
                "expectedSha256": hashlib.sha256(format_bytes).hexdigest(),
            }],
        }
        source.close()
        source = None
        source_legacy.close()
        source_legacy = None
        from pathlib import Path
        with sqlite3.connect(Path(os.path.join(source_root, "metadata.db")).as_uri() + "?mode=ro", uri=True) as connection:
            connection.execute("PRAGMA query_only=ON")
            stored = [row[0] for row in connection.execute(
                "SELECT a.name FROM books_authors_link l JOIN authors a ON a.id=l.author "
                "WHERE l.book=? ORDER BY l.id", (source_id,))]
        require(any("|" in name for name in stored), "comma_author_storage_fixture_missing")
        decoded = [name.replace("|", ",") for name in stored]
        require(decoded == expectation["authors"], "decoded_catalog_authors_differ_from_api")
        expectation["authors"] = decoded

        environment = dict(os.environ)
        environment["CLC_WORKER_TEMP_DIRECTORY"] = worker_temp
        environment["CLC_WORKER_COVER_DIRECTORY"] = ""
        environment["CALIBRE_CONFIG_DIRECTORY"] = config_root
        process = subprocess.Popen(
            [
                calibre_debug,
                "-e",
                worker_script,
                "--",
                source_root,
                source_uuid,
                destination_root,
                "",
            ],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            env=environment,
        )
        ready = read_message(process)
        require(ready.get("protocolVersion") == PROTOCOL_VERSION, "worker_protocol_mismatch")
        require(ready.get("kind") == "ready" and not ready.get("failureCode"), "worker_not_ready")
        require(ready.get("quarantineLibraryExisted") is False, "destination_not_created")
        require(bool(ready.get("quarantineLibraryUuid")), "destination_uuid_missing")

        write_message(process, {
            "protocolVersion": PROTOCOL_VERSION,
            "kind": "executeChunk",
            "chunkId": "copy",
            "operations": [operation("copy:1", "copyRecordToQuarantine", source_id, expectation)],
        })
        copied = read_message(process)
        require(copied.get("failureCode") is None, "worker_copy_failed")
        copy_result = copied["operationResults"][0]
        require(copy_result.get("isSuccess") is True, "worker_copy_failed")
        destination_id = copy_result.get("quarantineRecordId")
        require(isinstance(destination_id, int) and destination_id > 0, "destination_id_missing")
        require(copy_result.get("verifiedFormats", [])[0]["sha256"]
                == expectation["formats"][0]["expectedSha256"], "worker_copy_hash_mismatch")

        write_message(process, {
            "protocolVersion": PROTOCOL_VERSION,
            "kind": "executeChunk",
            "chunkId": "delete",
            "operations": [operation(
                "delete:1", "removeRecordWithContent", source_id, expectation)],
        })
        removed = read_message(process)
        require(removed.get("failureCode") is None, "worker_delete_failed")
        require(removed["operationResults"][0].get("isSuccess") is True, "worker_delete_failed")

        write_message(process, {
            "protocolVersion": PROTOCOL_VERSION,
            "kind": "shutdown",
            "chunkId": None,
            "operations": None,
        })
        process.stdin.close()
        exit_code = process.wait(timeout=30)
        require(exit_code == 0, "worker_exit_failed:" + process.stderr.read())
        process = None

        verify_source_legacy = db(source_root, read_only=False)
        verify_source = verify_source_legacy.new_api
        verify_source.init()
        require(not verify_source.has_id(source_id), "source_record_still_exists")
        verify_destination_legacy = db(destination_root, read_only=False)
        verify_destination = verify_destination_legacy.new_api
        verify_destination.init()
        require(verify_destination.has_id(destination_id), "destination_record_missing")
        require(
            verify_destination.field_for("#clc_worker_probe", destination_id) == "Custom Value",
            "destination_custom_value_mismatch",
        )
        require(
            verify_destination.format_hash(destination_id, "TXT").lower()
            == expectation["formats"][0]["expectedSha256"],
            "destination_format_hash_mismatch",
        )
        print(json.dumps({
            "destination_created": True,
            "destination_record_verified": True,
            "duplicate_mode_used": True,
            "source_deleted_after_copy": True,
            "worker_protocol": PROTOCOL_VERSION,
            "decoded_comma_authors_verified": True,
        }, sort_keys=True))
    finally:
        for value in (
                verify_destination, verify_destination_legacy,
                verify_source, verify_source_legacy, source, source_legacy):
            if value is not None:
                try:
                    value.close()
                except Exception:
                    pass
        if process is not None:
            try:
                process.kill()
                process.wait(timeout=10)
            except Exception:
                pass
        shutil.rmtree(probe_root, ignore_errors=True)


if __name__ == "__main__":
    try:
        main()
    except Exception as exception:
        print(json.dumps({"error": str(exception)}, sort_keys=True), file=sys.stderr)
        raise
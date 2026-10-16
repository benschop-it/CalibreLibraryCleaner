import argparse
import hashlib
import io
import json
import os
import shutil
import subprocess
import sys
import tempfile


EXPECTED_MARKER = "I_CONFIRM_THIS_CALIBRE_LIBRARY_IS_DISPOSABLE"
PROTOCOL_VERSION = "calibre-mutation-worker-protocol/1.3"


def require(condition, code):
    if not condition:
        raise RuntimeError(code)


def write_message(stream, value):
    stream.write(json.dumps(value, separators=(",", ":")) + "\n")
    stream.flush()


def read_message(stream):
    line = stream.readline()
    require(line, "worker_ended")
    return json.loads(line)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("worker_script")
    parser.add_argument("confirmation")
    arguments = parser.parse_args()
    require(arguments.confirmation == EXPECTED_MARKER, "disposable_confirmation_missing")
    worker_script = os.path.realpath(arguments.worker_script)
    require(os.path.isfile(worker_script), "worker_script_missing")

    root = tempfile.mkdtemp(prefix="clc-managed-worker-")
    library_root = os.path.join(root, "library")
    config_root = os.path.join(root, "config")
    worker_temp = os.path.join(root, "worker")
    os.makedirs(library_root)
    os.makedirs(config_root)
    os.makedirs(worker_temp)
    process = None
    from calibre.ebooks.metadata.book.base import Metadata
    from calibre.library import db

    try:
        cache = db(library_root).new_api
        cache.init()
        book_id = cache.create_book_entry(
            Metadata("Initial Title", ["Initial Author"]), add_duplicates=True
        )
        original_bytes = b"synthetic worker acceptance bytes\n"
        cache.add_format(book_id, "TXT", io.BytesIO(original_bytes))
        cache.set_field("tags", {book_id: ["Preserved Tag"]}, do_path_update=False)
        cache.set_field("comments", {book_id: "Preserved Comment"}, do_path_update=False)
        cache.set_field("title", {book_id: "Final Title"}, do_path_update=False)
        cache.set_field("authors", {book_id: ["Final Author"]}, do_path_update=False)
        stale_path = cache.field_for("path", book_id)
        author_book_id = cache.create_book_entry(
            Metadata("Typography Title", ["J.K. Rowling"]), add_duplicates=True
        )
        author_bytes = b"synthetic author acceptance bytes\n"
        cache.add_format(author_book_id, "TXT", io.BytesIO(author_bytes))
        author_ids = tuple(cache.field_ids_for("authors", author_book_id) or ())
        require(len(author_ids) == 1, "author_fixture_ids")
        cache.set_sort_for_authors({author_ids[0]: "Rowling, J.K."}, update_books=True)
        cache.clear_caches()
        library_uuid = str(cache.library_id)
        cache.close()

        environment = os.environ.copy()
        environment["CALIBRE_CONFIG_DIRECTORY"] = config_root
        environment["CLC_WORKER_TEMP_DIRECTORY"] = worker_temp
        environment["CLC_WORKER_COVER_DIRECTORY"] = ""
        process = subprocess.Popen(
            [sys.executable, "-e", worker_script, "--", library_root, library_uuid],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            env=environment,
        )
        ready = read_message(process.stdout)
        require(ready.get("protocolVersion") == PROTOCOL_VERSION, "ready_version")
        require("refreshManagedName" in ready.get("capabilities", []), "refresh_capability")
        author_operation = {
            "operationId": "final-author-review/{}".format(author_book_id),
            "kind": "setMetadata",
            "recordId": author_book_id,
            "canonicalFormat": None,
            "targetRecordId": None,
            "expectedSizeInBytes": None,
            "expectedSha256": None,
            "metadataField": "authors",
            "metadataValues": ["J. K. Rowling"],
            "metadataAuthorSortValues": ["Rowling, J. K."],
            "metadataSource": {
            "providerId": "calibre-final-author-review",
            "providerVersion": "calibre-final-author-review/1.0.0",
            "editionId": str(author_book_id),
            "proposalPolicyVersion": "author-source-preservation/1.3.0",
            },
            "stagedCoverFileName": None,
            "stagedCoverSizeInBytes": None,
            "stagedCoverSha256": None,
        }
        write_message(process.stdin, {
            "protocolVersion": PROTOCOL_VERSION,
            "kind": "executeChunk",
            "chunkId": "author-chunk",
            "operations": [author_operation],
        })
        author_response = read_message(process.stdout)
        require(author_response.get("failureCode") is None, "author_failure")
        author_result = author_response["operationResults"][0]
        require(author_result.get("isSuccess") is True, "author_operation_failure")
        require(author_result.get("isSkipped") is False, "author_operation_skipped")
        require(author_result.get("verifiedMetadataValues") == ["J. K. Rowling"],
            "author_display_readback")
        require(author_result.get("verifiedAuthorSortValues") == ["Rowling, J. K."],
            "author_sort_readback")
        require(author_result.get("verifiedAuthorSort") == "Rowling, J. K.",
            "author_book_sort_readback")
        operation = {
            "operationId": "refresh-managed-name/1",
            "kind": "refreshManagedName",
            "recordId": book_id,
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
        }
        write_message(process.stdin, {
            "protocolVersion": PROTOCOL_VERSION,
            "kind": "executeChunk",
            "chunkId": "refresh-chunk",
            "operations": [operation],
        })
        response = read_message(process.stdout)
        require(response.get("failureCode") is None, "refresh_failure")
        result = response["operationResults"][0]
        require(result.get("isSuccess") is True, "refresh_operation_failure")
        require(result.get("verifiedManagedPath") == "Final Author/Final Title ({})".format(book_id),
                "managed_path_readback")
        formats = result.get("verifiedFormats") or []
        require(len(formats) == 1 and formats[0]["format"] == "TXT", "format_readback")
        require(formats[0]["storedFileName"] == "Final Title - Final Author.txt", "basename_readback")
        require(formats[0]["sha256"] == hashlib.sha256(original_bytes).hexdigest(), "hash_readback")
        write_message(process.stdin, {
            "protocolVersion": PROTOCOL_VERSION,
            "kind": "shutdown",
            "chunkId": None,
            "operations": None,
        })
        stopped = read_message(process.stdout)
        require(stopped.get("kind") == "stopped", "worker_stopped_response")
        process.stdin.close()
        return_code = process.wait(timeout=20)
        stderr = process.stderr.read()
        require(return_code == 0, "worker_exit_{}_{}".format(return_code, stderr[-512:]))

        cache = db(library_root).new_api
        cache.init()
        try:
            final_path = cache.field_for("path", book_id)
            final_format = cache.format_abspath(book_id, "TXT")
            with open(final_format, "rb") as stream:
                final_bytes = stream.read()
            require(cache.field_for("title", book_id) == "Final Title", "title_changed")
            require(list(cache.field_for("authors", book_id)) == ["Final Author"], "authors_changed")
            require(cache.field_for("tags", book_id) == ("Preserved Tag",), "tags_changed")
            require(cache.field_for("comments", book_id) == "Preserved Comment", "comments_changed")
            require(list(cache.field_for("authors", author_book_id)) == ["J. K. Rowling"],
                    "author_display_not_applied")
            final_author_ids = tuple(cache.field_ids_for("authors", author_book_id) or ())
            final_author_data = cache.author_data(final_author_ids)
            require([final_author_data[value]["sort"] for value in final_author_ids]
                    == ["Rowling, J. K."], "author_sort_not_applied")
            require(cache.field_for("author_sort", author_book_id) == "Rowling, J. K.",
                    "author_book_sort_not_applied")
            author_format = cache.format_abspath(author_book_id, "TXT")
            with open(author_format, "rb") as stream:
                require(stream.read() == author_bytes, "author_format_bytes_changed")
            require(len(cache.all_book_ids()) == 2, "record_count_changed")
            require(len(cache.formats(book_id)) == 1
                    and len(cache.formats(author_book_id)) == 1, "format_count_changed")
            require(final_bytes == original_bytes, "format_bytes_changed")
        finally:
            cache.close()
        print(json.dumps({
            "calibre_version": __import__("calibre.constants", fromlist=["__version__"]).__version__,
            "stale_path": stale_path,
            "verified_path": result["verifiedManagedPath"],
            "verified_basename": formats[0]["storedFileName"],
            "author_display": "J. K. Rowling",
            "author_sort": "Rowling, J. K.",
            "author_write_applied": True,
            "record_count": 2,
            "format_count": 2,
            "bytes_unchanged": True,
        }, indent=2, sort_keys=True))
    finally:
        if process is not None and process.poll() is None:
            process.kill()
            process.wait()
        shutil.rmtree(root, ignore_errors=True)


if __name__ == "__main__":
    main()
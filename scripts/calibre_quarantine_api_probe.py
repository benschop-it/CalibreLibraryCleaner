import argparse
import hashlib
import io
import json
import os
import shutil
import sys
from datetime import datetime, timezone


EXPECTED_MARKER = "I_CONFIRM_THE_QUARANTINE_PROBE_ROOT_IS_DISPOSABLE"


def require(condition, code):
    if not condition:
        raise RuntimeError(code)


def sha256(value):
    return hashlib.sha256(value).hexdigest()


def close_quietly(value):
    if value is None:
        return
    try:
        value.close()
    except Exception:
        pass


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("probe_root")
    parser.add_argument("confirmation")
    arguments = parser.parse_args()

    require(arguments.confirmation == EXPECTED_MARKER, "disposable_confirmation_missing")
    probe_root = os.path.realpath(arguments.probe_root)
    require(not os.path.exists(probe_root), "probe_root_must_be_absent")
    parent = os.path.dirname(probe_root)
    require(os.path.isdir(parent), "probe_parent_missing")

    from calibre.ebooks.metadata.book.base import Metadata
    from calibre.library import db
    from calibre.utils.img import create_canvas, image_to_data

    source_root = os.path.join(probe_root, "Source")
    destination_root = os.path.join(probe_root, "Source - Unresolvable")
    require(len(source_root) < 75, "source_library_path_too_long")
    require(len(destination_root) < 75, "destination_library_path_too_long")
    source_legacy = None
    source = None
    destination_legacy = None
    destination = None
    reopened_legacy = None
    reopened = None
    result = {}
    try:
        os.makedirs(probe_root)
        source_legacy = db(source_root, read_only=False)
        source = source_legacy.new_api
        source.init()
        source.create_custom_column(
            "clc_probe_text",
            "CLC Probe Text",
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

        metadata = Metadata("Quarantine API Probe", ["Probe Author"])
        metadata.identifiers = {"isbn": "9780306406157", "clc-probe": "value"}
        metadata.tags = ["Probe Tag"]
        metadata.publisher = "Probe Publisher"
        metadata.languages = ["eng", "nld"]
        metadata.series = "Probe Series"
        metadata.series_index = 2.5
        metadata.pubdate = datetime(2001, 2, 3, tzinfo=timezone.utc)
        metadata.comments = "Probe Comment"
        source_id = source.create_book_entry(metadata, add_duplicates=True)
        source.set_field("#clc_probe_text", {source_id: "Probe Custom Value"})
        author_id = source.field_ids_for("authors", source_id)[0]
        source.set_sort_for_authors({author_id: "Author, Probe"})

        format_bytes = b"Calibre Library Cleaner quarantine API probe\n"
        require(
            source.add_format(source_id, "TXT", io.BytesIO(format_bytes), replace=False, run_hooks=False),
            "source_format_add_failed",
        )
        cover_bytes = image_to_data(create_canvas(32, 32, "#336699"), fmt="JPEG")
        source.set_cover({source_id: cover_bytes})

        destination_legacy = db(destination_root, read_only=False)
        destination = destination_legacy.new_api
        destination.init()
        require(destination.all_book_ids() == frozenset(), "destination_not_empty")
        destination.create_custom_column(
            "clc_probe_text",
            "CLC Probe Text",
            "text",
            False,
            True,
            {},
        )
        destination.close()
        destination = None
        destination_legacy.close()
        destination_legacy = db(destination_root, read_only=False)
        destination = destination_legacy.new_api
        destination.init()

        copied_metadata = source.get_metadata(source_id, get_cover=True, cover_as_data=True)
        copied_cover = copied_metadata.cover_data[1]
        first_destination_id = destination.create_book_entry(
            copied_metadata,
            cover=copied_cover,
            add_duplicates=True,
        )
        second_destination_id = destination.create_book_entry(
            copied_metadata,
            cover=copied_cover,
            add_duplicates=True,
        )
        require(first_destination_id != second_destination_id, "duplicate_record_not_created")
        destination.set_field(
            "#clc_probe_text",
            {first_destination_id: source.field_for("#clc_probe_text", source_id)},
        )

        staged_format = io.BytesIO()
        source.copy_format_to(source_id, "TXT", staged_format)
        transferred_bytes = staged_format.getvalue()
        require(sha256(transferred_bytes) == sha256(format_bytes), "source_format_hash_mismatch")
        staged_format.seek(0)
        require(
            destination.add_format(
                first_destination_id,
                "TXT",
                staged_format,
                replace=False,
                run_hooks=False,
            ),
            "destination_format_add_failed",
        )
        destination_format = io.BytesIO()
        destination.copy_format_to(first_destination_id, "TXT", destination_format)

        protected_fields = (
            "title",
            "authors",
            "author_sort",
            "identifiers",
            "tags",
            "publisher",
            "languages",
            "series",
            "series_index",
            "pubdate",
            "comments",
            "#clc_probe_text",
        )
        for field in protected_fields:
            require(
                destination.field_for(field, first_destination_id)
                == source.field_for(field, source_id),
                "metadata_readback_mismatch:" + field,
            )
        require(
            sha256(destination_format.getvalue()) == sha256(format_bytes),
            "destination_format_hash_mismatch",
        )
        require(
            sha256(destination.cover(first_destination_id)) == sha256(source.cover(source_id)),
            "destination_cover_hash_mismatch",
        )
        require(source.has_id(source_id), "source_removed_before_verification")

        source.remove_books([source_id], permanent=False)
        require(not source.has_id(source_id), "source_delete_failed")
        source.close()
        source = None
        source_legacy.close()
        source_legacy = None
        destination_library_id = str(destination.library_id)
        destination.close()
        destination = None
        destination_legacy.close()
        destination_legacy = None

        reopened_legacy = db(destination_root, read_only=False)
        reopened = reopened_legacy.new_api
        reopened.init()
        require(str(reopened.library_id) == destination_library_id, "destination_uuid_changed")
        require(
            reopened.all_book_ids() == frozenset({first_destination_id, second_destination_id}),
            "destination_reopen_ids_mismatch",
        )
        require(
            reopened.field_for("#clc_probe_text", first_destination_id) == "Probe Custom Value",
            "destination_reopen_custom_value_mismatch",
        )
        result = {
            "cover_hash_verified": True,
            "custom_column_verified": True,
            "destination_created": True,
            "destination_reopened": True,
            "duplicate_record_created": True,
            "format_hash_verified": True,
            "metadata_verified": True,
            "source_deleted_after_verification": True,
        }
    finally:
        close_quietly(reopened)
        close_quietly(reopened_legacy)
        close_quietly(destination)
        close_quietly(destination_legacy)
        close_quietly(source)
        close_quietly(source_legacy)
        shutil.rmtree(probe_root, ignore_errors=True)

    result["probe_root_removed"] = not os.path.exists(probe_root)
    print(json.dumps(result, sort_keys=True))


if __name__ == "__main__":
    try:
        main()
    except Exception as exception:
        print(json.dumps({"error": str(exception)}, sort_keys=True), file=sys.stderr)
        raise
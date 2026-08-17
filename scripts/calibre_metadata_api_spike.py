import argparse
import json
import math
import os
import uuid
from datetime import datetime, timezone


EXPECTED_MARKER = "I_CONFIRM_THIS_CALIBRE_LIBRARY_IS_DISPOSABLE"
SENTINEL_PREFIX = "CLC API Spike"


def require(condition, code):
    if not condition:
        raise RuntimeError(code)


def json_value(value):
    if isinstance(value, datetime):
        return value.isoformat()
    if isinstance(value, (set, frozenset)):
        return sorted(json_value(item) for item in value)
    if isinstance(value, (list, tuple)):
        return [json_value(item) for item in value]
    if isinstance(value, dict):
        return {str(key): json_value(item) for key, item in sorted(value.items())}
    return value


def equal_value(actual, expected):
    if isinstance(expected, datetime):
        return actual is not None and actual.astimezone(timezone.utc) == expected
    if isinstance(expected, float):
        return actual is not None and math.isclose(float(actual), expected)
    return json_value(actual) == json_value(expected)


def writable_custom_value(metadata):
    datatype = metadata.get("datatype")
    if metadata.get("is_multiple"):
        return ["CLC API Spike Local Value"]
    if datatype in {"text", "comments", "enumeration"}:
        values = metadata.get("display", {}).get("enum_values") or []
        return values[0] if values else "CLC API Spike Local Value"
    if datatype in {"int", "float", "rating"}:
        return 2
    if datatype == "bool":
        return True
    if datatype == "datetime":
        return datetime(2001, 2, 3, 4, 5, 6, tzinfo=timezone.utc)
    return None


def field_snapshot(cache, book_id, field_names):
    return {name: json_value(cache.field_for(name, book_id)) for name in field_names}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("library_root")
    parser.add_argument("confirmation")
    arguments = parser.parse_args()

    require(arguments.confirmation == EXPECTED_MARKER, "disposable_confirmation_missing")
    library_root = os.path.realpath(arguments.library_root)
    require(os.path.isfile(os.path.join(library_root, "metadata.db")), "metadata_db_missing")

    from calibre.ebooks.metadata.book.base import Metadata
    from calibre.library import db
    from calibre.utils.img import create_canvas, image_to_data

    cache = db(library_root).new_api
    cache.init()
    created_ids = []
    try:
        original_ids = cache.all_book_ids()
        token = uuid.uuid4().hex
        keeper_metadata = Metadata(f"{SENTINEL_PREFIX} Initial {token}", ["Spike Author Initial"])
        source_metadata = Metadata(f"{SENTINEL_PREFIX} Source {token}", ["Spike Source Author"])
        keeper_id = cache.create_book_entry(keeper_metadata, add_duplicates=True)
        source_id = cache.create_book_entry(source_metadata, add_duplicates=True)
        created_ids.extend([keeper_id, source_id])
        require(keeper_id not in original_ids and source_id not in original_ids, "synthetic_create_failed")

        preserved_values = {
            "tags": ["CLC API Spike Preserved Tag"],
            "rating": 8,
            "comments": "CLC API Spike Preserved Comment",
        }
        for name, value in preserved_values.items():
            cache.set_field(name, {keeper_id: value}, do_path_update=False)

        custom_values = {}
        custom_metadata = cache.field_metadata.custom_field_metadata()
        for name, metadata in custom_metadata.items():
            value = writable_custom_value(metadata)
            if value is None:
                continue
            try:
                cache.set_field(name, {keeper_id: value}, do_path_update=False)
                custom_values[name] = json_value(cache.field_for(name, keeper_id))
            except Exception:
                continue

        initial_identifiers = {
            "clc-spike-preserved": token,
            "isbn": "9780306406157",
        }
        cache.set_field("identifiers", {keeper_id: initial_identifiers}, do_path_update=False)
        preserved_fields = list(preserved_values) + list(custom_values)
        preserved_before = field_snapshot(cache, keeper_id, preserved_fields)

        cover_bytes = image_to_data(create_canvas(32, 32, "#336699"), fmt="JPEG")
        cover_result = cache.set_cover({keeper_id: cover_bytes, source_id: cover_bytes})
        source_path = cache.field_for("path", source_id)
        source_directory = os.path.join(library_root, source_path)
        require(os.path.isfile(os.path.join(source_directory, "cover.jpg")), "source_cover_missing")

        expected = {
            "title": f"{SENTINEL_PREFIX} Updated {token}",
            "authors": ["Spike Author Updated", "Second Spike Author"],
            "author_sort": "Updated, Spike & Author, Second Spike",
            "identifiers": {
                "clc-spike-preserved": token,
                "isbn": "9780140328721",
                "doi": "10.1000/clc-spike",
            },
            "publisher": "CLC API Spike Publisher",
            "pubdate": datetime(2004, 5, 6, 0, 0, 0, tzinfo=timezone.utc),
            "languages": ["eng", "nld"],
            "series": "CLC API Spike Series",
            "series_index": 3.5,
        }

        path_before = cache.field_for("path", keeper_id)
        field_results = {}
        path_effects = {}
        physical_path_effects = {}
        for name, value in expected.items():
            before = cache.field_for("path", keeper_id)
            before_directory = os.path.join(library_root, before)
            changed_ids = cache.set_field(name, {keeper_id: value}, do_path_update=True)
            after = cache.field_for("path", keeper_id)
            after_directory = os.path.join(library_root, after)
            actual = cache.field_for(name, keeper_id)
            field_results[name] = {
                "changed": keeper_id in changed_ids,
                "round_trip": equal_value(actual, value),
            }
            path_effects[name] = before != after
            physical_path_effects[name] = {
                "old_path_retired_or_unchanged": before == after or not os.path.exists(before_directory),
                "new_directory_present": os.path.isdir(after_directory),
                "cover_followed_path": os.path.isfile(os.path.join(after_directory, "cover.jpg")),
            }

        cover_metadata = cache.get_metadata(keeper_id, get_cover=True, cover_as_data=True)
        cover_data = cover_metadata.cover_data[1]

        preserved_after = field_snapshot(cache, keeper_id, preserved_fields)
        identifiers_after = cache.field_for("identifiers", keeper_id)
        cache.set_field(
            "identifiers",
            {keeper_id: {"isbn": expected["identifiers"]["isbn"]}},
            do_path_update=True,
        )
        identifier_omission_removes_existing = "clc-spike-preserved" not in cache.field_for(
            "identifiers", keeper_id
        )
        cache.set_field("identifiers", {keeper_id: expected["identifiers"]}, do_path_update=True)
        source_present_before = cache.has_id(source_id)
        cache.remove_books({source_id}, permanent=False)
        source_present_after = cache.has_id(source_id)
        source_trash_present = any(
            entry.book_id == source_id for entry in cache.list_trash_entries()[0]
        )
        cache.delete_trash_entry(source_id, "b")
        source_trash_deleted = not any(
            entry.book_id == source_id for entry in cache.list_trash_entries()[0]
        )
        created_ids.remove(source_id)

        result = {
            "calibre_version": __import__("calibre.constants", fromlist=["__version__"]).__version__,
            "library_identity_present": bool(str(cache.library_id)),
            "synthetic_records_only": original_ids.isdisjoint({keeper_id, source_id}),
            "field_results": field_results,
            "cover": {
                "changed": keeper_id in cover_result,
                "round_trip": cover_data == cover_bytes,
            },
            "preservation": {
                "tags": preserved_before["tags"] == preserved_after["tags"],
                "rating": preserved_before["rating"] == preserved_after["rating"],
                "comments": preserved_before["comments"] == preserved_after["comments"],
                "custom_columns_available": len(custom_metadata),
                "custom_columns_tested": len(custom_values),
                "custom_columns_preserved": all(
                    preserved_before[name] == preserved_after[name] for name in custom_values
                ),
                "unrelated_identifier": identifiers_after.get("clc-spike-preserved") == token,
                "identifier_omission_removes_existing": identifier_omission_removes_existing,
            },
            "path_effects": {
                "initial_to_final": path_before != cache.field_for("path", keeper_id),
                "by_field": path_effects,
                "physical_by_field": physical_path_effects,
            },
            "single_session_ordering": {
                "metadata_visible_before_removal": all(
                    item["round_trip"] for item in field_results.values()
                ),
                "source_present_before": source_present_before,
                "source_absent_after_nonpermanent_removal": not source_present_after,
                "source_managed_directory_removed": not os.path.exists(source_directory),
                "source_trash_present_after_removal": source_trash_present,
                "source_trash_deleted_by_id": source_trash_deleted,
                "keeper_still_present": cache.has_id(keeper_id),
                "metadata_visible_after_removal": cache.field_for("title", keeper_id) == expected["title"],
            },
        }
        print(json.dumps(result, indent=2, sort_keys=True))
    finally:
        if created_ids:
            cache.remove_books(set(created_ids), permanent=True)
        cache.close()


if __name__ == "__main__":
    try:
        main()
    except Exception as exception:
        print(json.dumps({"error": str(exception)}))
        raise
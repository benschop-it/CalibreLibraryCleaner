import hashlib
import json
import os
import sys
import tempfile
import unicodedata
from datetime import datetime, timezone
from decimal import Decimal, InvalidOperation


PROTOCOL_VERSION = "calibre-mutation-worker-protocol/1.6"
MAXIMUM_MESSAGE_BYTES = 1048576
MAXIMUM_OPERATIONS = 100
CAPABILITIES = ["setMetadata", "refreshManagedName", "transferFormat", "removeFormat", "removeRecord",
                "copyRecordToQuarantine", "removeRecordWithContent"]
LIBRARY_ROOT = ""
QUARANTINE_ROOT = ""
COPIED_TO_QUARANTINE = {}
WORKER_TEMP_DIRECTORY = os.environ["CLC_WORKER_TEMP_DIRECTORY"]
COVER_DIRECTORY = os.environ.get("CLC_WORKER_COVER_DIRECTORY", "")
MAXIMUM_COVER_BYTES = 524288
METADATA_FIELDS = {
    "title", "authors", "authorSort", "publisher", "publicationDate",
    "languages", "identifiers", "series", "seriesIndex", "cover",
}


def write_message(message):
    encoded = json.dumps(message, ensure_ascii=True, separators=(",", ":")).encode("utf-8")
    if len(encoded) > MAXIMUM_MESSAGE_BYTES:
        raise ValueError("response_too_large")
    sys.stdout.buffer.write(encoded + b"\n")
    sys.stdout.buffer.flush()


def read_message():
    line = sys.stdin.buffer.readline(MAXIMUM_MESSAGE_BYTES + 1)
    if not line:
        return None
    if len(line) > MAXIMUM_MESSAGE_BYTES or not line.endswith(b"\n"):
        raise ValueError("request_too_large")
    try:
        return json.loads(line, object_pairs_hook=unique_object)
    except (json.JSONDecodeError, UnicodeDecodeError) as exception:
        raise ValueError("request_json_invalid") from exception


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("request_property_duplicate")
        result[key] = value
    return result


def operation_result(operation, success, failure_code=None, metadata=None, managed_name=None,
                     skipped=False, skip_code=None, quarantine_record_id=None):
    result = {
        "operationId": operation["operationId"],
        "kind": operation["kind"],
        "isSuccess": success,
        "isSkipped": skipped,
    }
    if failure_code is not None:
        result["failureCode"] = failure_code
    if metadata is not None:
        result["verifiedMetadataValues"] = metadata["values"]
        result["verifiedManagedPath"] = metadata["managedPath"]
        result["verifiedAuthorSort"] = metadata["authorSort"]
        if metadata.get("formats") is not None:
            result["verifiedFormats"] = metadata["formats"]
        if metadata.get("authorSortValues") is not None:
            result["verifiedAuthorSortValues"] = metadata["authorSortValues"]
    if managed_name is not None:
        result["verifiedManagedPath"] = managed_name["managedPath"]
        result["verifiedFormats"] = managed_name["formats"]
    if quarantine_record_id is not None:
        result["quarantineRecordId"] = quarantine_record_id
    if skip_code is not None:
        result["skipCode"] = skip_code
    return result


def validate_operation(operation):
    required = {"operationId", "kind", "recordId", "canonicalFormat", "targetRecordId",
                "expectedSizeInBytes", "expectedSha256", "metadataField", "metadataValues",
                "metadataAuthorSortValues", "metadataSource", "stagedCoverFileName", "stagedCoverSizeInBytes",
                "stagedCoverSha256", "quarantineTitle", "quarantineAuthors",
                "quarantineAuthorSorts", "quarantineFormats"}
    if not isinstance(operation, dict) or set(operation) != required:
        raise ValueError("operation_invalid")
    if not isinstance(operation["operationId"], str) or not operation["operationId"] \
            or len(operation["operationId"]) > 160:
        raise ValueError("operation_id_invalid")
    kind = operation["kind"]
    if kind not in CAPABILITIES:
        raise ValueError("operation_kind_invalid")
    if not isinstance(operation["recordId"], int) or operation["recordId"] <= 0:
        raise ValueError("record_id_invalid")
    metadata_names = ("metadataField", "metadataValues", "metadataAuthorSortValues", "metadataSource",
                      "stagedCoverFileName", "stagedCoverSizeInBytes", "stagedCoverSha256")
    quarantine_names = ("quarantineTitle", "quarantineAuthors", "quarantineAuthorSorts",
                        "quarantineFormats")
    if kind == "setMetadata":
        if any(operation[name] is not None for name in
               ("canonicalFormat", "targetRecordId", "expectedSizeInBytes", "expectedSha256")):
            raise ValueError("metadata_operation_invalid")
        validate_metadata(operation)
        return
    if kind == "refreshManagedName":
        if any(operation[name] is not None for name in
               ("canonicalFormat", "targetRecordId", "expectedSizeInBytes", "expectedSha256")):
            raise ValueError("refresh_managed_name_invalid")
        if any(operation[name] is not None for name in metadata_names):
            raise ValueError("refresh_managed_name_invalid")
        if any(operation[name] is not None for name in quarantine_names):
            raise ValueError("refresh_managed_name_invalid")
        return
    if kind in {"copyRecordToQuarantine", "removeRecordWithContent"}:
        if any(operation[name] is not None for name in
               ("canonicalFormat", "targetRecordId", "expectedSizeInBytes", "expectedSha256")) \
                or any(operation[name] is not None for name in metadata_names):
            raise ValueError("quarantine_operation_invalid")
        validate_quarantine_expectation(operation)
        return
    if any(operation[name] is not None for name in quarantine_names):
        raise ValueError("quarantine_operation_invalid")
    if any(operation[name] is not None for name in metadata_names):
        raise ValueError("metadata_operation_invalid")
    if kind == "removeRecord":
        if any(operation[name] is not None for name in
               ("canonicalFormat", "targetRecordId", "expectedSizeInBytes", "expectedSha256")):
            raise ValueError("remove_record_invalid")
        return
    fmt = operation["canonicalFormat"]
    if not isinstance(fmt, str) or not fmt or len(fmt) > 16 or not fmt.isascii() or not fmt.isalnum():
        raise ValueError("format_invalid")
    if kind == "transferFormat":
        if not isinstance(operation["targetRecordId"], int) or operation["targetRecordId"] <= 0 \
                or operation["targetRecordId"] == operation["recordId"]:
            raise ValueError("target_record_id_invalid")
        if not isinstance(operation["expectedSizeInBytes"], int) \
                or operation["expectedSizeInBytes"] < 0:
            raise ValueError("expected_size_invalid")
        digest = operation["expectedSha256"]
        if not isinstance(digest, str) or len(digest) != 64 \
                or any(character not in "0123456789abcdef" for character in digest):
            raise ValueError("expected_sha256_invalid")
    elif any(operation[name] is not None for name in
             ("targetRecordId", "expectedSizeInBytes", "expectedSha256")):
        raise ValueError("remove_format_invalid")


def validate_quarantine_expectation(operation):
    title = operation["quarantineTitle"]
    authors = operation["quarantineAuthors"]
    author_sorts = operation["quarantineAuthorSorts"]
    formats = operation["quarantineFormats"]
    if not isinstance(title, str) or not title or len(title) > 2048 \
            or any(ord(character) < 32 for character in title) \
            or not isinstance(authors, list) or not 1 <= len(authors) <= 32 \
            or not isinstance(author_sorts, list) or len(author_sorts) != len(authors) \
            or any(not isinstance(value, str) or not value or len(value) > 512
                   or any(ord(character) < 32 for character in value)
                   for value in authors + author_sorts) \
            or not isinstance(formats, list) or len(formats) > 32:
        raise ValueError("quarantine_expectation_invalid")
    seen = set()
    for value in formats:
        if not isinstance(value, dict) or set(value) != {
                "format", "expectedRelativePath", "expectedSizeInBytes", "expectedSha256"}:
            raise ValueError("quarantine_format_invalid")
        fmt = value["format"]
        path = value["expectedRelativePath"]
        size = value["expectedSizeInBytes"]
        digest = value["expectedSha256"]
        if not isinstance(fmt, str) or not fmt or len(fmt) > 16 or not fmt.isascii() \
                or not fmt.isalnum() or fmt in seen or not isinstance(path, str) or not path \
                or len(path) > 1024 or any(ord(character) < 32 for character in path) \
                or not isinstance(size, int) or size < 0 or not isinstance(digest, str) \
                or len(digest) != 64 \
                or any(character not in "0123456789abcdef" for character in digest):
            raise ValueError("quarantine_format_invalid")
        seen.add(fmt)


def validate_metadata(operation):
    field = operation["metadataField"]
    values = operation["metadataValues"]
    author_sorts = operation["metadataAuthorSortValues"]
    source = operation["metadataSource"]
    if field not in METADATA_FIELDS or not isinstance(values, list) or len(values) > 32 \
            or any(not isinstance(value, str) or len(value) > 2048 for value in values) \
            or sum(len(value) for value in values) > 16384:
        raise ValueError("metadata_values_invalid")
    if not isinstance(source, dict) or set(source) != {
            "providerId", "providerVersion", "editionId", "proposalPolicyVersion"}:
        raise ValueError("metadata_source_invalid")
    bounds = {"providerId": 64, "providerVersion": 128,
              "editionId": 160, "proposalPolicyVersion": 128}
    if any(not isinstance(source[name], str) or not source[name]
           or len(source[name]) > maximum or any(ord(character) < 32 for character in source[name])
           for name, maximum in bounds.items()):
        raise ValueError("metadata_source_invalid")
    cover_names = ("stagedCoverFileName", "stagedCoverSizeInBytes", "stagedCoverSha256")
    if field == "cover":
        filename = operation["stagedCoverFileName"]
        size = operation["stagedCoverSizeInBytes"]
        digest = operation["stagedCoverSha256"]
        if values or not isinstance(filename, str) or not filename or len(filename) > 128 \
                or os.path.basename(filename) != filename or not isinstance(size, int) \
                or size < 1 or size > MAXIMUM_COVER_BYTES or not isinstance(digest, str) \
                or len(digest) != 64 \
                or any(character not in "0123456789abcdef" for character in digest):
            raise ValueError("metadata_cover_invalid")
    elif any(operation[name] is not None for name in cover_names) or not values:
        raise ValueError("metadata_values_invalid")
    if field in {"title", "authorSort", "publisher", "publicationDate", "series", "seriesIndex"} \
            and len(values) != 1:
        raise ValueError("metadata_values_invalid")
    if field == "authors" and not 1 <= len(values) <= 16 \
            or field == "languages" and not 1 <= len(values) <= 8 \
            or field == "identifiers" and not 1 <= len(values) <= 32:
        raise ValueError("metadata_values_invalid")
    if author_sorts is not None and (field != "authors" or not isinstance(author_sorts, list)
            or len(author_sorts) != len(values)
            or any(not isinstance(value, str) or not value or len(value) > 512
                   for value in author_sorts)):
        raise ValueError("metadata_author_sorts_invalid")


def validate_chunk(message):
    if not isinstance(message, dict) or set(message) != {"protocolVersion", "kind", "chunkId", "operations"}:
        raise ValueError("request_invalid")
    if message["protocolVersion"] != PROTOCOL_VERSION or message["kind"] != "executeChunk":
        raise ValueError("request_invalid")
    if not isinstance(message["chunkId"], str) or not message["chunkId"] \
            or len(message["chunkId"]) > 100:
        raise ValueError("chunk_id_invalid")
    operations = message["operations"]
    if not isinstance(operations, list) or not 1 <= len(operations) <= MAXIMUM_OPERATIONS:
        raise ValueError("operation_count_invalid")
    for operation in operations:
        validate_operation(operation)
    operation_ids = [operation["operationId"] for operation in operations]
    if len(set(operation_ids)) != len(operation_ids):
        raise ValueError("operation_id_duplicate")
    phases = {"setMetadata": 0, "refreshManagedName": 1,
              "transferFormat": 2, "copyRecordToQuarantine": 2,
              "removeFormat": 3, "removeRecord": 4, "removeRecordWithContent": 5}
    if [phases[operation["kind"]] for operation in operations] != sorted(
            phases[operation["kind"]] for operation in operations):
        raise ValueError("operation_order_invalid")
    return operations


def transfer_format(cache, operation):
    source = operation["recordId"]
    target = operation["targetRecordId"]
    fmt = operation["canonicalFormat"].upper()
    if not cache.has_id(source) or not cache.has_id(target) or not cache.has_format(source, fmt) \
            or cache.has_format(target, fmt):
        raise ValueError("transfer_precondition_failed")
    with tempfile.TemporaryFile(dir=WORKER_TEMP_DIRECTORY) as stream:
        cache.copy_format_to(source, fmt, stream)
        if stream.tell() != operation["expectedSizeInBytes"]:
            raise ValueError("transfer_source_changed")
        stream.seek(0)
        digest = hashlib.sha256()
        while True:
            content = stream.read(1024 * 1024)
            if not content:
                break
            digest.update(content)
        if digest.hexdigest() != operation["expectedSha256"]:
            raise ValueError("transfer_source_changed")
        stream.seek(0)
        cache.add_format(target, fmt, stream, replace=False, run_hooks=True)
    if not cache.has_format(target, fmt) \
            or cache.format_hash(target, fmt).lower() != operation["expectedSha256"]:
        raise ValueError("transfer_postcondition_failed")


def local_values(cache, book_id):
    values = {name: cache.field_for(name, book_id) for name in ("tags", "rating", "comments")}
    for name, metadata in cache.field_metadata.custom_field_metadata().items():
        if metadata.get("datatype") != "composite":
            values[name] = cache.field_for(name, book_id)
    return values


def hash_file(path):
    digest = hashlib.sha256()
    size = 0
    with open(path, "rb") as stream:
        while True:
            content = stream.read(1024 * 1024)
            if not content:
                break
            size += len(content)
            digest.update(content)
    return size, digest.hexdigest()


def managed_format_state(cache, book_id, library_root=None):
    library_root = LIBRARY_ROOT if library_root is None else library_root
    result = []
    for fmt in sorted(cache.formats(book_id, verify_formats=True) or ()):
        canonical = fmt.upper()
        path = os.path.realpath(cache.format_abspath(book_id, canonical))
        if os.path.commonpath((library_root, path)) != library_root or not os.path.isfile(path):
            raise ValueError("managed_format_unavailable")
        before = os.stat(path, follow_symlinks=False)
        size, digest = hash_file(path)
        after = os.stat(path, follow_symlinks=False)
        if file_observation(before) != file_observation(after) or size != after.st_size \
                or (before.st_dev, before.st_ino) != (after.st_dev, after.st_ino):
            raise ValueError("managed_format_changed_while_hashing")
        result.append({
            "format": canonical,
            "storedFileName": os.path.basename(path),
            "relativePath": os.path.relpath(path, library_root).replace("\\", "/"),
            "sizeInBytes": size,
            "sha256": digest,
            "observation": file_observation(after),
        })
    return result


def file_observation(value):
    def timestamp(nanoseconds):
        seconds, remainder = divmod(nanoseconds, 1000000000)
        return datetime.fromtimestamp(seconds, timezone.utc).strftime("%Y-%m-%dT%H:%M:%S") \
            + ".{:07d}Z".format(remainder // 100)

    attributes = getattr(value, "st_file_attributes", 0)
    if attributes & (16 | 1024):
        raise ValueError("managed_format_not_a_regular_file")
    return {
        "length": value.st_size,
        "creationTimeUtc": timestamp(getattr(value, "st_birthtime_ns", value.st_ctime_ns)),
        "lastWriteTimeUtc": timestamp(value.st_mtime_ns),
        "attributes": attributes,
    }


def managed_record_state(cache, book_id, library_root=None):
    library_root = LIBRARY_ROOT if library_root is None else library_root
    if not cache.has_id(book_id):
        raise ValueError("managed_name_target_missing")
    fields = ("title", "authors", "author_sort", "identifiers", "publisher", "pubdate",
              "languages", "series", "series_index")
    author_ids = tuple(cache.field_ids_for("authors", book_id) or ())
    author_data = cache.author_data(author_ids)
    extra_files = []
    for value in cache.list_extra_files(book_id):
        path = os.path.realpath(value.file_path)
        if os.path.commonpath((library_root, path)) != library_root or not os.path.isfile(path):
            raise ValueError("managed_extra_file_unavailable")
        size, digest = hash_file(path)
        extra_files.append((value.relpath, size, digest))
    composites = {
        name: cache.field_for(name, book_id)
        for name, metadata in cache.field_metadata.custom_field_metadata().items()
        if metadata.get("datatype") == "composite"
    }
    return {
        "metadata": {name: cache.field_for(name, book_id) for name in fields},
        "local": local_values(cache, book_id),
        "cover": cache.cover(book_id),
        "formats": managed_format_state(cache, book_id, library_root),
        "authorSortValues": [author_data[author_id]["sort"] for author_id in author_ids],
        "extraFiles": sorted(extra_files),
        "composites": composites,
    }


def refresh_managed_name(cache, operation):
    book_id = operation["recordId"]
    before = managed_record_state(cache, book_id)
    cache.update_path({book_id})
    cache.clear_caches(book_ids={book_id})
    after = managed_record_state(cache, book_id)
    before_formats = [(value["format"], value["sizeInBytes"], value["sha256"])
                      for value in before["formats"]]
    after_formats = [(value["format"], value["sizeInBytes"], value["sha256"])
                     for value in after["formats"]]
    if before["metadata"] != after["metadata"] or before["local"] != after["local"] \
            or before["cover"] != after["cover"] or before_formats != after_formats:
        raise ValueError("managed_name_record_changed")
    managed_path = cache.field_for("path", book_id)
    if not isinstance(managed_path, str) or len(managed_path) > 1024 \
            or any(ord(character) < 32 for character in managed_path):
        raise ValueError("managed_name_path_invalid")
    normalized = managed_path.replace("\\", "/").strip("/")
    segments = normalized.split("/")
    if len(segments) != 2 or not all(segments) \
            or not segments[1].endswith(" ({})".format(book_id)):
        raise ValueError("managed_name_layout_invalid")
    directory = os.path.realpath(os.path.join(LIBRARY_ROOT, *segments))
    if os.path.commonpath((LIBRARY_ROOT, directory)) != LIBRARY_ROOT \
            or not os.path.isdir(directory):
        raise ValueError("managed_name_directory_unavailable")
    formats = managed_format_state(cache, book_id, LIBRARY_ROOT)
    for value in formats:
        if os.path.dirname(os.path.realpath(os.path.join(directory, value["storedFileName"]))) != directory:
            raise ValueError("managed_name_format_containment_failed")
    return {"managedPath": normalized, "formats": protocol_format_state(formats)}


def protocol_format_state(formats):
    return [{
        "format": value["format"],
        "storedFileName": value["storedFileName"],
        "sizeInBytes": value["sizeInBytes"],
        "sha256": value["sha256"],
        "observation": value["observation"],
    } for value in formats]


def custom_column_schema(cache):
    result = {}
    metadata = cache.field_metadata.custom_field_metadata()
    if len(metadata) > 256:
        raise ValueError("quarantine_custom_column_count_exceeded")
    for key, value in metadata.items():
        result[key] = {
            "label": value.get("label"),
            "name": value.get("name"),
            "datatype": value.get("datatype"),
            "is_multiple": bool(value.get("is_multiple")),
            "is_editable": bool(value.get("is_editable", True)),
            "display": value.get("display") or {},
        }
    return result


def reconcile_custom_columns(source_cache, destination_cache, allow_create):
    source_schema = custom_column_schema(source_cache)
    destination_schema = custom_column_schema(destination_cache)
    created = False
    for key in sorted(source_schema, key=lambda value: (
            source_schema[value]["datatype"] == "composite", value)):
        expected = source_schema[key]
        actual = destination_schema.get(key)
        if actual is not None:
            if actual != expected:
                raise ValueError("quarantine_custom_column_incompatible")
            continue
        if not allow_create:
            raise ValueError("quarantine_custom_column_missing")
        destination_cache.create_custom_column(
            expected["label"], expected["name"], expected["datatype"],
            expected["is_multiple"], expected["is_editable"], expected["display"])
        created = True
    return created


def validate_quarantine_source(cache, operation):
    book_id = operation["recordId"]
    if not cache.has_id(book_id) \
            or cache.field_for("title", book_id) != operation["quarantineTitle"] \
            or list(cache.field_for("authors", book_id) or ()) != operation["quarantineAuthors"]:
        raise ValueError("quarantine_source_metadata_changed")
    state = managed_record_state(cache, book_id, LIBRARY_ROOT)
    if state["authorSortValues"] != operation["quarantineAuthorSorts"]:
        raise ValueError("quarantine_source_metadata_changed")
    actual_formats = [(value["format"], value["relativePath"], value["sizeInBytes"], value["sha256"])
                      for value in state["formats"]]
    expected_formats = [(value["format"], value["expectedRelativePath"].replace("\\", "/"),
                         value["expectedSizeInBytes"], value["expectedSha256"])
                        for value in operation["quarantineFormats"]]
    if actual_formats != expected_formats:
        raise ValueError("quarantine_source_format_changed")
    return state


def comparable_record_state(state):
    return {
        "metadata": state["metadata"],
        "local": state["local"],
        "cover": state["cover"],
        "formats": [(value["format"], value["sizeInBytes"], value["sha256"])
                    for value in state["formats"]],
        "authorSortValues": state["authorSortValues"],
        "extraFiles": state["extraFiles"],
        "composites": state["composites"],
    }


def copy_record_to_quarantine(source_cache, source_legacy, destination_cache,
                              destination_legacy, operation):
    if destination_cache is None or operation["recordId"] in COPIED_TO_QUARANTINE:
        raise ValueError("quarantine_copy_precondition_failed")
    before = validate_quarantine_source(source_cache, operation)
    from calibre.db.copy_to_library import copy_one_book
    copied = copy_one_book(
        operation["recordId"], source_legacy, destination_legacy,
        duplicate_action="add", preserve_date=True, preserve_uuid=True)
    destination_id = copied.get("new_book_id")
    if copied.get("action") != "add" or not isinstance(destination_id, int) \
            or destination_id <= 0 or not destination_cache.has_id(destination_id):
        raise ValueError("quarantine_duplicate_create_failed")
    after_source = validate_quarantine_source(source_cache, operation)
    after_destination = managed_record_state(destination_cache, destination_id, QUARANTINE_ROOT)
    if comparable_record_state(before) != comparable_record_state(after_source) \
            or comparable_record_state(before) != comparable_record_state(after_destination):
        raise ValueError("quarantine_copy_readback_failed")
    COPIED_TO_QUARANTINE[operation["recordId"]] = destination_id
    managed_path = destination_cache.field_for("path", destination_id)
    if not isinstance(managed_path, str) or not managed_path or len(managed_path) > 1024:
        raise ValueError("quarantine_destination_path_invalid")
    return destination_id, {
        "managedPath": managed_path,
        "formats": protocol_format_state(after_destination["formats"]),
    }


def remove_record_with_content(cache, operation):
    book_id = operation["recordId"]
    if book_id not in COPIED_TO_QUARANTINE:
        raise ValueError("quarantine_source_delete_without_verified_copy")
    validate_quarantine_source(cache, operation)
    cache.remove_books([book_id], permanent=False)
    if cache.has_id(book_id):
        raise ValueError("quarantine_source_delete_failed")


def clear_metadata_caches(cache, field, book_id):
    if field == "authors":
        cache.clear_caches()
    else:
        cache.clear_caches(book_ids={book_id})


def metadata_state(cache, operation):
    book_id = operation["recordId"]
    field = operation["metadataField"]
    clear_metadata_caches(cache, field, book_id)
    calibre_field = {"authorSort": "author_sort", "publicationDate": "pubdate",
                     "seriesIndex": "series_index"}.get(field, field)
    value = cache.cover(book_id) if field == "cover" else cache.field_for(calibre_field, book_id)
    return {
        "field": value,
        "local": local_values(cache, book_id),
        "path": cache.field_for("path", book_id),
        "authorSort": cache.field_for("author_sort", book_id),
        "formats": managed_format_state(cache, book_id) if field in {"title", "authors"} else None,
    }


def staged_cover(operation):
    if not COVER_DIRECTORY:
        raise ValueError("metadata_cover_staging_unavailable")
    root = os.path.realpath(COVER_DIRECTORY)
    path = os.path.realpath(os.path.join(root, operation["stagedCoverFileName"]))
    if os.path.commonpath((root, path)) != root or not os.path.isfile(path):
        raise ValueError("metadata_cover_staging_invalid")
    size = operation["stagedCoverSizeInBytes"]
    with open(path, "rb") as stream:
        content = stream.read(MAXIMUM_COVER_BYTES + 1)
    if len(content) != size or hashlib.sha256(content).hexdigest() != operation["stagedCoverSha256"]:
        raise ValueError("metadata_cover_changed")
    from calibre.utils.img import image_from_data, image_to_data
    return image_to_data(image_from_data(content), fmt="JPEG")


def parse_identifiers(values):
    result = {}
    for value in values:
        separator = value.find(":")
        if separator <= 0 or separator == len(value) - 1:
            raise ValueError("metadata_identifiers_invalid")
        name = value[:separator].lower()
        identifier = value[separator + 1:]
        if len(name) > 64 or len(identifier) > 1024 or not name.isascii() \
                or any(not (character.isalnum() or character in "_-") for character in name):
            raise ValueError("metadata_identifiers_invalid")
        if name in result:
            raise ValueError("metadata_identifiers_ambiguous")
        result[name] = identifier
    return result


def canonical_datetime(value):
    try:
        return datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as exception:
        raise ValueError("metadata_publication_date_invalid") from exception


def canonical_text(value):
    return " ".join(value.split())


def canonical_author_sort(value):
    if not isinstance(value, str) or value.count(",") != 1:
        return None
    family, given = (canonical_text(unicodedata.normalize("NFC", part))
                     for part in value.split(",", 1))
    return family + ", " + given if family and given else None


def set_metadata(cache, operation):
    book_id = operation["recordId"]
    field = operation["metadataField"]
    values = operation["metadataValues"]
    if not cache.has_id(book_id):
        raise ValueError("metadata_target_missing")
    preserved = local_values(cache, book_id)
    before_formats = managed_format_state(cache, book_id) if field in {"title", "authors"} else None
    expected = values
    requested_author_sorts = operation["metadataAuthorSortValues"]
    if field == "cover":
        normalized = staged_cover(operation)
        cache.set_cover({book_id: normalized})
        cache.clear_caches(book_ids={book_id})
        if cache.cover(book_id) != normalized:
            raise ValueError("metadata_cover_readback_failed")
        verified = ["true"]
    else:
        calibre_field = {"authorSort": "author_sort", "publicationDate": "pubdate",
                 "seriesIndex": "series_index"}.get(field, field)
        value = values
        if field in {"title", "authorSort", "publisher", "series"}:
            value = values[0]
        elif field == "publicationDate":
            value = canonical_datetime(values[0])
        elif field == "seriesIndex":
            try:
                value = float(Decimal(values[0]))
            except (InvalidOperation, ValueError) as exception:
                raise ValueError("metadata_series_index_invalid") from exception
        elif field == "languages":
            from calibre.utils.localization import canonicalize_lang
            value = [canonicalize_lang(item) for item in values]
            if any(item is None for item in value):
                raise ValueError("metadata_languages_invalid")
            expected = value
        elif field == "identifiers":
            current = dict(cache.field_for("identifiers", book_id) or {})
            proposed = parse_identifiers(values)
            for name, identifier in proposed.items():
                for current_name in tuple(current):
                    if str(current_name).lower() == name:
                        del current[current_name]
                current[name] = identifier
            value = current
            expected = current
        elif field == "authors" and requested_author_sorts is not None:
            current_names = list(cache.field_for("authors", book_id) or [])
            current_ids = tuple(cache.field_ids_for("authors", book_id) or ())
            final_review = operation["metadataSource"]["providerId"] == "calibre-final-author-review"
            if len(current_ids) != len(requested_author_sorts) or len(current_names) != len(values):
                raise ValueError("metadata_author_normalization_precondition_failed")
            if not final_review:
                current_data = cache.author_data(current_ids)
                current_sorts = [current_data[author_id]["sort"] for author_id in current_ids]
                if [canonical_author_sort(value) for value in current_names] != requested_author_sorts \
                        or [canonical_author_sort(value) for value in current_sorts] != requested_author_sorts:
                    raise ValueError("metadata_author_normalization_precondition_failed")
        cache.set_field(calibre_field, {book_id: value}, do_path_update=True)
        clear_metadata_caches(cache, field, book_id)
        if field == "authors" and requested_author_sorts is not None:
            author_ids = tuple(cache.field_ids_for("authors", book_id) or ())
            if len(author_ids) != len(requested_author_sorts):
                raise ValueError("metadata_author_sort_readback_failed")
            author_data = cache.author_data(author_ids)
            if [author_data[author_id]["sort"] for author_id in author_ids] \
                    != requested_author_sorts:
                cache.set_sort_for_authors(dict(zip(author_ids, requested_author_sorts)), update_books=True)
                clear_metadata_caches(cache, field, book_id)
        actual = cache.field_for(calibre_field, book_id)
        if field == "publicationDate":
            matches = actual is not None and actual == value
            verified = [actual.isoformat()] if actual is not None else []
        elif field == "seriesIndex":
            matches = actual is not None and Decimal(str(actual)) == Decimal(values[0])
            verified = [str(actual)] if actual is not None else []
        elif field == "identifiers":
            actual = dict(actual or {})
            matches = actual == expected
            verified = [f"{name}:{actual[name]}" for name in sorted(actual)]
        elif field == "authors":
            verified = list(actual or [])
            if [canonical_text(item) for item in verified] != [
                    canonical_text(item) for item in expected]:
                raise ValueError("metadata_authors_display_readback_failed")
            matches = True
            if requested_author_sorts is not None:
                author_ids = tuple(cache.field_ids_for("authors", book_id) or ())
                if len(author_ids) != len(requested_author_sorts):
                    raise ValueError("metadata_authors_id_count_readback_failed")
                author_data = cache.author_data(author_ids)
                verified_author_sorts = [author_data[author_id]["sort"] for author_id in author_ids]
                if verified_author_sorts != requested_author_sorts:
                    raise ValueError("metadata_authors_sort_readback_failed")
                if cache.field_for("author_sort", book_id) != " & ".join(requested_author_sorts):
                    raise ValueError("metadata_authors_book_sort_readback_failed")
        elif field == "languages":
            verified = list(actual or [])
            matches = verified == expected
        else:
            verified = [actual] if actual is not None else []
            matches = actual == value
        if not matches:
            raise ValueError("metadata_{}_readback_failed".format(field))
    if local_values(cache, book_id) != preserved:
        raise ValueError("metadata_local_fields_changed")
    managed_path = cache.field_for("path", book_id)
    author_sort = cache.field_for("author_sort", book_id)
    if not isinstance(managed_path, str) or not managed_path or len(managed_path) > 1024 \
            or not isinstance(author_sort, str) or not author_sort or len(author_sort) > 512:
        raise ValueError("metadata_context_readback_failed")
    after_formats = managed_format_state(cache, book_id) if before_formats is not None else None
    if before_formats is not None and \
            [(item["format"], item["sizeInBytes"], item["sha256"]) for item in before_formats] \
            != [(item["format"], item["sizeInBytes"], item["sha256"]) for item in after_formats]:
        raise ValueError("metadata_format_bytes_changed")
    return {"values": verified, "managedPath": managed_path, "authorSort": author_sort,
            "formats": protocol_format_state(after_formats) if after_formats is not None else None,
            "authorSortValues": verified_author_sorts if field == "authors"
            and requested_author_sorts is not None else None}


def execute_chunk(cache, source_legacy, destination_cache, destination_legacy, message):
    operations = validate_chunk(message)
    results = []
    mutation_started = False
    completed_ids = set()
    failure_code = None
    try:
        for operation in (value for value in operations if value["kind"] == "setMetadata"):
            before = metadata_state(cache, operation)
            mutation_started = True
            try:
                metadata = set_metadata(cache, operation)
            except Exception as exception:
                if metadata_state(cache, operation) == before:
                    skip_code = str(exception) if isinstance(exception, ValueError) \
                        else "calibre_api_failure"
                    results.append(operation_result(
                        operation, True, skipped=True, skip_code=skip_code))
                    completed_ids.add(operation["operationId"])
                    continue
                raise
            results.append(operation_result(operation, True, metadata=metadata))
            completed_ids.add(operation["operationId"])

        for operation in (value for value in operations if value["kind"] == "refreshManagedName"):
            mutation_started = True
            managed_name = refresh_managed_name(cache, operation)
            results.append(operation_result(operation, True, managed_name=managed_name))
            completed_ids.add(operation["operationId"])

        for operation in (value for value in operations if value["kind"] == "transferFormat"):
            mutation_started = True
            transfer_format(cache, operation)
            results.append(operation_result(operation, True))
            completed_ids.add(operation["operationId"])

        for operation in (value for value in operations if value["kind"] == "copyRecordToQuarantine"):
            mutation_started = True
            destination_id, readback = copy_record_to_quarantine(
                cache, source_legacy, destination_cache, destination_legacy, operation)
            results.append(operation_result(
                operation, True, managed_name=readback, quarantine_record_id=destination_id))
            completed_ids.add(operation["operationId"])

        format_operations = [value for value in operations if value["kind"] == "removeFormat"]
        if format_operations:
            formats_map = {}
            for operation in format_operations:
                if not cache.has_id(operation["recordId"]) or not cache.has_format(
                        operation["recordId"], operation["canonicalFormat"].upper()):
                    raise ValueError("remove_format_precondition_failed")
                formats_map.setdefault(operation["recordId"], []).append(operation["canonicalFormat"].upper())
            mutation_started = True
            cache.remove_formats(formats_map, db_only=False)
            for operation in format_operations:
                if cache.has_format(operation["recordId"], operation["canonicalFormat"].upper()):
                    raise ValueError("remove_format_postcondition_failed")
                results.append(operation_result(operation, True))
                completed_ids.add(operation["operationId"])

        for operation in (value for value in operations if value["kind"] == "removeRecordWithContent"):
            mutation_started = True
            remove_record_with_content(cache, operation)
            results.append(operation_result(operation, True))
            completed_ids.add(operation["operationId"])

        record_operations = [value for value in operations if value["kind"] == "removeRecord"]
        if record_operations:
            for operation in record_operations:
                if not cache.has_id(operation["recordId"]) or cache.formats(
                        operation["recordId"], verify_formats=True):
                    raise ValueError("remove_record_precondition_failed")
            record_ids = [operation["recordId"] for operation in record_operations]
            mutation_started = True
            cache.remove_books(record_ids, permanent=False)
            for operation in record_operations:
                if cache.has_id(operation["recordId"]):
                    raise ValueError("remove_record_postcondition_failed")
                results.append(operation_result(operation, True))
                completed_ids.add(operation["operationId"])
    except Exception as exception:
        failure_code = str(exception) if isinstance(exception, ValueError) else "calibre_api_failure"

    for operation in operations:
        if operation["operationId"] not in completed_ids:
            results.append(operation_result(operation, False, failure_code or "not_executed"))
    results_by_id = {result["operationId"]: result for result in results}
    response = {
        "protocolVersion": PROTOCOL_VERSION,
        "kind": "chunkResult",
        "chunkId": message["chunkId"],
        "mutationStarted": mutation_started,
        "operationResults": [results_by_id[operation["operationId"]] for operation in operations],
    }
    if failure_code is not None:
        response["failureCode"] = failure_code
    return response


def run():
    global LIBRARY_ROOT, QUARANTINE_ROOT
    if len(sys.argv) != 5:
        raise ValueError("worker_arguments_invalid")
    library_root = os.path.abspath(sys.argv[1])
    LIBRARY_ROOT = os.path.realpath(library_root)
    expected_uuid = sys.argv[2]
    quarantine_root = sys.argv[3]
    expected_quarantine_uuid = sys.argv[4]
    from calibre.library import db
    source_legacy = db(library_root)
    cache = source_legacy.new_api
    cache.init()
    destination_legacy = None
    destination_cache = None
    try:
        library_uuid = str(cache.library_id)
        if library_uuid != expected_uuid:
            write_message({
                "protocolVersion": PROTOCOL_VERSION,
                "kind": "ready",
                "libraryUuid": library_uuid,
                "capabilities": CAPABILITIES,
                "failureCode": "library_identity_mismatch",
            })
            return
        quarantine_uuid = None
        quarantine_existed = None
        if quarantine_root:
            QUARANTINE_ROOT = os.path.realpath(quarantine_root)
            expected_root = os.path.join(
                os.path.dirname(LIBRARY_ROOT), os.path.basename(LIBRARY_ROOT) + " - Unresolvable")
            if os.path.normcase(QUARANTINE_ROOT) != os.path.normcase(os.path.realpath(expected_root)) \
                    or len(QUARANTINE_ROOT) >= 75:
                raise ValueError("quarantine_destination_invalid")
            quarantine_existed = os.path.isfile(os.path.join(QUARANTINE_ROOT, "metadata.db"))
            if os.path.exists(QUARANTINE_ROOT) and not quarantine_existed:
                raise ValueError("quarantine_destination_invalid")
            destination_legacy = db(QUARANTINE_ROOT)
            destination_cache = destination_legacy.new_api
            destination_cache.init()
            if reconcile_custom_columns(cache, destination_cache, not quarantine_existed):
                destination_cache.close()
                destination_legacy.close()
                destination_legacy = db(QUARANTINE_ROOT)
                destination_cache = destination_legacy.new_api
                destination_cache.init()
            quarantine_uuid = str(destination_cache.library_id)
            if expected_quarantine_uuid and quarantine_uuid != expected_quarantine_uuid:
                raise ValueError("quarantine_identity_mismatch")
        write_message({
            "protocolVersion": PROTOCOL_VERSION,
            "kind": "ready",
            "libraryUuid": library_uuid,
            "capabilities": CAPABILITIES,
            "quarantineLibraryUuid": quarantine_uuid,
            "quarantineLibraryExisted": quarantine_existed,
        })
        while True:
            message = read_message()
            if message is None:
                return
            if message == {"protocolVersion": PROTOCOL_VERSION, "kind": "shutdown",
                           "chunkId": None, "operations": None}:
                write_message({"protocolVersion": PROTOCOL_VERSION, "kind": "stopped",
                               "mutationStarted": False})
                return
            write_message(execute_chunk(
                cache, source_legacy, destination_cache, destination_legacy, message))
    finally:
        if destination_cache is not None:
            destination_cache.close()
        if destination_legacy is not None:
            destination_legacy.close()
        cache.close()
        source_legacy.close()


if __name__ == "__main__":
    try:
        run()
    except Exception:
        try:
            write_message({
                "protocolVersion": PROTOCOL_VERSION,
                "kind": "ready",
                "libraryUuid": "",
                "capabilities": [],
                "failureCode": "worker_initialization_failed",
            })
        except Exception:
            pass
        raise
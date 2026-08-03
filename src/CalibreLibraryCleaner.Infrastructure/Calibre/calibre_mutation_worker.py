import hashlib
import json
import os
import sys
import tempfile


PROTOCOL_VERSION = "calibre-mutation-worker-protocol/1.0"
MAXIMUM_MESSAGE_BYTES = 1048576
MAXIMUM_OPERATIONS = 100
CAPABILITIES = ["transferFormat", "removeFormat", "removeRecord"]
WORKER_TEMP_DIRECTORY = os.environ["CLC_WORKER_TEMP_DIRECTORY"]


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


def operation_result(operation, success, failure_code=None):
    result = {
        "operationId": operation["operationId"],
        "kind": operation["kind"],
        "isSuccess": success,
    }
    if failure_code is not None:
        result["failureCode"] = failure_code
    return result


def validate_operation(operation):
    required = {"operationId", "kind", "recordId", "canonicalFormat", "targetRecordId",
                "expectedSizeInBytes", "expectedSha256"}
    if not isinstance(operation, dict) or set(operation) != required:
        raise ValueError("operation_invalid")
    if not isinstance(operation["operationId"], str) or not operation["operationId"] \
            or len(operation["operationId"]) > 160:
        raise ValueError("operation_id_invalid")
    if operation["kind"] not in CAPABILITIES:
        raise ValueError("operation_kind_invalid")
    if not isinstance(operation["recordId"], int) or operation["recordId"] <= 0:
        raise ValueError("record_id_invalid")
    if operation["kind"] == "removeRecord":
        if any(operation[name] is not None for name in
               ("canonicalFormat", "targetRecordId", "expectedSizeInBytes", "expectedSha256")):
            raise ValueError("remove_record_invalid")
        return
    fmt = operation["canonicalFormat"]
    if not isinstance(fmt, str) or not fmt or len(fmt) > 16 or not fmt.isascii() or not fmt.isalnum():
        raise ValueError("format_invalid")
    if operation["kind"] == "transferFormat":
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
    phases = {"transferFormat": 0, "removeFormat": 1, "removeRecord": 2}
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


def execute_chunk(cache, message):
    operations = validate_chunk(message)
    results = []
    mutation_started = False
    completed_ids = set()
    failure_code = None
    try:
        for operation in (value for value in operations if value["kind"] == "transferFormat"):
            mutation_started = True
            transfer_format(cache, operation)
            results.append(operation_result(operation, True))
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
    if len(sys.argv) != 3:
        raise ValueError("worker_arguments_invalid")
    library_root = os.path.abspath(sys.argv[1])
    expected_uuid = sys.argv[2]
    from calibre.library import db
    cache = db(library_root).new_api
    cache.init()
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
        write_message({
            "protocolVersion": PROTOCOL_VERSION,
            "kind": "ready",
            "libraryUuid": library_uuid,
            "capabilities": CAPABILITIES,
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
            write_message(execute_chunk(cache, message))
    finally:
        cache.close()


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
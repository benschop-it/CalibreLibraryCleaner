import argparse
import io
import json
import os
import shutil


EXPECTED_MARKER = "I_CONFIRM_THIS_CALIBRE_LIBRARY_IS_DISPOSABLE"


def require(condition, code):
    if not condition:
        raise RuntimeError(code)


def snapshot(cache, library_root, book_id):
    relative_path = cache.field_for("path", book_id)
    format_path = cache.format_abspath(book_id, "TXT")
    return {
        "relative_path": relative_path,
        "directory_present": os.path.isdir(os.path.join(library_root, relative_path)),
        "format_basename": os.path.basename(format_path),
        "format_present": os.path.isfile(format_path),
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("library_root")
    parser.add_argument("confirmation")
    arguments = parser.parse_args()

    require(arguments.confirmation == EXPECTED_MARKER, "disposable_confirmation_missing")
    library_root = os.path.realpath(arguments.library_root)
    require(not os.path.exists(library_root), "probe_root_must_not_exist")
    os.makedirs(library_root)

    from calibre.ebooks.metadata.book.base import Metadata
    from calibre.library import db

    cache = db(library_root).new_api
    cache.init()
    try:
        book_id = cache.create_book_entry(
            Metadata("Initial Title", ["Initial Author"]), add_duplicates=True
        )
        cache.add_format(book_id, "TXT", io.BytesIO(b"synthetic fixture\n"))
        initial = snapshot(cache, library_root, book_id)

        cache.set_field("title", {book_id: "Final Title"}, do_path_update=False)
        cache.set_field("authors", {book_id: ["Final Author"]}, do_path_update=False)
        stale = snapshot(cache, library_root, book_id)

        title_changed = cache.set_field(
            "title", {book_id: "Final Title"}, do_path_update=True
        )
        author_changed = cache.set_field(
            "authors", {book_id: ["Final Author"]}, do_path_update=True
        )
        after_no_op = snapshot(cache, library_root, book_id)

        update_result = cache.update_path({book_id})
        refreshed = snapshot(cache, library_root, book_id)

        print(
            json.dumps(
                {
                    "calibre_version": __import__(
                        "calibre.constants", fromlist=["__version__"]
                    ).__version__,
                    "initial": initial,
                    "stale": stale,
                    "no_op_set_field": {
                        "title_changed": book_id in title_changed,
                        "authors_changed": book_id in author_changed,
                        "state": after_no_op,
                    },
                    "update_path": {
                        "return_value": update_result,
                        "state": refreshed,
                    },
                    "no_op_refreshed": after_no_op != stale,
                    "update_path_refreshed": refreshed != after_no_op,
                    "record_count": len(cache.all_book_ids()),
                    "format_count": len(cache.formats(book_id)),
                },
                indent=2,
                sort_keys=True,
                default=str,
            )
        )
    finally:
        cache.close()
        shutil.rmtree(library_root, ignore_errors=True)


if __name__ == "__main__":
    main()
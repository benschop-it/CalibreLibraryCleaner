import argparse
import hashlib
import html
import io
import json
import os
import shutil
import zipfile


MARKER = "I_CONFIRM_THIS_IS_GENERATED_ACCEPTANCE_DATA"


def require(condition, code):
    if not condition:
        raise RuntimeError(code)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def epub(title, author, case_id, cover, image_only=False):
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, "w") as archive:
        archive.writestr("mimetype", "application/epub+zip")
        archive.writestr("META-INF/container.xml",
                        '<container xmlns="urn:oasis:names:tc:opendocument:xmlns:container">'
                        '<rootfiles><rootfile full-path="content.opf"/></rootfiles></container>')
        archive.writestr("content.opf",
                        '<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">'
                        '<metadata xmlns:dc="http://purl.org/dc/elements/1.1/">'
                        f'<dc:identifier id="id">urn:quality-fixture:{case_id}</dc:identifier>'
                        f'<dc:title>{html.escape(title)}</dc:title><dc:creator>{html.escape(author)}</dc:creator>'
                        '<dc:language>en</dc:language></metadata><manifest>'
                        '<item id="page" href="page.xhtml" media-type="application/xhtml+xml"/>'
                        '<item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>'
                        '<item id="cover" href="cover.jpg" media-type="image/jpeg" properties="cover-image"/>'
                        '</manifest><spine><itemref idref="page"/></spine></package>')
        page = ('<img src="cover.jpg" alt="Generated image-only publication"/>'
                if image_only else
                f'<h1>{html.escape(title)}</h1><p>By {html.escape(author)}</p>'
                + "".join(f"<p>Generated observation {case_id}-{index}: a field, a river, a measured line.</p>"
                          for index in range(80)))
        archive.writestr("page.xhtml",
                        '<html xmlns="http://www.w3.org/1999/xhtml" '
                        'xmlns:epub="http://www.idpf.org/2007/ops"><body epub:type="titlepage">'
                        + page + "</body></html>")
        archive.writestr("nav.xhtml",
                        '<html xmlns="http://www.w3.org/1999/xhtml" '
                        'xmlns:epub="http://www.idpf.org/2007/ops"><body><nav epub:type="toc">'
                        '<ol><li><a href="page.xhtml">Opening</a></li></ol></nav></body></html>')
        archive.writestr("cover.jpg", cover)
    return stream.getvalue()


def pdf(title, author, cover, scan=False):
    def literal(text):
        return text.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)")

    content = (b"q 400 0 0 600 40 40 cm /Im0 Do Q" if scan else
               ("BT /F1 14 Tf 36 760 Td "
                f"(Title: {literal(title)}) Tj 0 -24 Td (By {literal(author)}) Tj "
                "0 -24 Td (Generated original text for bounded fixture inspection.) Tj ET").encode("ascii"))
    objects = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        (b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R "
         b"/Resources << /Font << /F1 5 0 R >> /XObject << /Im0 7 0 R >> >> >>"),
        f"<< /Length {len(content)} >>\nstream\n".encode("ascii") + content + b"\nendstream",
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        f"<< /Title ({literal(title)}) /Author ({literal(author)}) >>".encode("ascii"),
        (f"<< /Type /XObject /Subtype /Image /Width 300 /Height 300 /ColorSpace /DeviceRGB "
         f"/BitsPerComponent 8 /Filter /DCTDecode /Length {len(cover)} >>\nstream\n").encode("ascii")
        + cover + b"\nendstream",
    ]
    data = bytearray(b"%PDF-1.4\n%\xe2\xe3\xcf\xd3\n")
    offsets = [0]
    for index, value in enumerate(objects, 1):
        offsets.append(len(data))
        data.extend(f"{index} 0 obj\n".encode("ascii") + value + b"\nendobj\n")
    xref = len(data)
    data.extend(f"xref\n0 {len(offsets)}\n0000000000 65535 f \n".encode("ascii"))
    for offset in offsets[1:]:
        data.extend(f"{offset:010d} 00000 n \n".encode("ascii"))
    data.extend((f"trailer\n<< /Size {len(offsets)} /Root 1 0 R /Info 6 0 R >>\n"
                 f"startxref\n{xref}\n%%EOF\n").encode("ascii"))
    return bytes(data)


def close(legacy):
    legacy.new_api.close()
    legacy.close()


def snapshot(root):
    from calibre.library import db

    legacy = db(root, read_only=True)
    cache = legacy.new_api
    cache.init()
    try:
        records = {}
        record_ids = {}
        for book_id in cache.all_book_ids():
            metadata = cache.get_metadata(book_id, get_cover=False)
            formats = {}
            for extension in cache.formats(book_id) or ():
                with open(cache.format_abspath(book_id, extension), "rb") as stream:
                    formats[extension] = digest(stream.read())
            extras = {}
            for extra in cache.list_extra_files(book_id):
                with open(extra.file_path, "rb") as stream:
                    extras[extra.relpath] = digest(stream.read())
            cover = cache.cover(book_id)
            records[str(metadata.identifiers["quality-case"])] = {
                "title": metadata.title,
                "authors": list(metadata.authors),
                "tags": list(metadata.tags),
                "rating": metadata.rating,
                "comments": metadata.comments,
                "custom": cache.field_for("#qa_marker", book_id),
                "formats": formats,
                "extras": extras,
                "cover": digest(cover) if cover else None,
                "path": cache.field_for("path", book_id),
                "authorSort": cache.field_for("author_sort", book_id),
                "authorSortValues": [
                    cache.author_data((author_id,))[author_id]["sort"]
                    for author_id in cache.field_ids_for("authors", book_id)],
                "identifiers": dict(metadata.identifiers),
                "publication": {
                    "publisher": metadata.publisher,
                    "pubdate": metadata.pubdate.isoformat() if metadata.pubdate else None,
                    "languages": list(metadata.languages),
                    "series": metadata.series,
                    "seriesIndex": metadata.series_index,
                },
            }
            record_ids[str(metadata.identifiers["quality-case"])] = book_id
        return {"uuid": str(cache.library_id), "records": records, "recordIds": record_ids}
    finally:
        close(legacy)


def create(root):
    from calibre.ebooks.metadata.book.base import Metadata
    from calibre.library import db
    from calibre.utils.img import create_canvas, image_to_data

    require(not os.path.exists(root), "fixture_root_must_be_absent")
    os.makedirs(root)
    with open(os.path.join(root, "generated-fixture.marker"), "w", encoding="ascii") as stream:
        stream.write(MARKER)
    cover = image_to_data(create_canvas(300, 300, "#336699"), fmt="JPEG")
    cases = [
        ("1", "Harbor Geometry", "Case Writer Alpha", "keep"),
        ("2", "2017 Broken Label(", "Case Writer Beta", "keep"),
        ("3", "Unidentified Ledger", "Unknown", "move"),
        ("4", "Delta Atlas", "Case Writer Gamma", "move"),
        ("5", "Illustrated Coordinates", "Case Writer Theta", "keep"),
        ("6", "\u5ddd\u306e\u8a18\u9332", "\u5c71\u7530\u592a\u90ce", "keep"),
        ("7", "Laboratory Notes", "Institute of Field Research", "keep"),
        ("8", "Orbital Manual", "2000 AD", "keep"),
        ("9", "Catalog Shell", "Case Writer Iota", "move"),
        ("10", "Marsh Design", "(2015 Case Writer Epsilon", "keep"),
        ("11", "Unknown", "Case Writer Zeta", "keep"),
        ("12", "Unreadable Coordinates", "Case Writer Kappa", "move"),
    ]
    for name in ("S", "S - Unresolvable"):
        library = os.path.join(root, name)
        legacy = db(library, read_only=False)
        legacy.new_api.init()
        legacy.new_api.create_custom_column("qa_marker", "Quality fixture", "text", False, True, {})
        close(legacy)
        legacy = db(library, read_only=False)
        cache = legacy.new_api
        cache.init()
        entries = cases if name == "S" else [("existing", "Delta Atlas", "Case Writer Gamma", "existing")]
        try:
            for case_id, title, author, _ in entries:
                metadata = Metadata(title, [author])
                metadata.identifiers = {"quality-case": case_id}
                metadata.tags = ["generated-quality-fixture"]
                metadata.rating = 6
                metadata.comments = "Generated original fixture notes."
                book_id = cache.create_book_entry(metadata, add_duplicates=True)
                cache.set_field("#qa_marker", {book_id: "custom-" + case_id})
                cache.set_cover({book_id: cover})
                cache.add_extra_files(book_id, {"data/qa.txt": io.BytesIO(("extra-" + case_id).encode("ascii"))})
                formats = {}
                if case_id == "existing":
                    formats["TXT"] = b"Existing destination record must remain unchanged."
                elif case_id == "5":
                    formats["PDF"] = pdf(title, author, cover, scan=True)
                elif case_id == "12":
                    formats["PDF"] = b"%PDF-1.4\nGenerated malformed fixture, not a valid document."
                elif case_id != "9":
                    local_title = "Estuary Logic" if case_id == "2" else title
                    local_author = "Case Writer Epsilon" if case_id == "10" else author
                    formats["EPUB"] = epub(local_title, local_author, case_id, cover, image_only=case_id == "3")
                    if case_id in ("1", "4"):
                        formats["PDF"] = pdf(
                            "Other Structure" if case_id == "4" else title,
                            "Case Writer Delta" if case_id == "4" else author, cover)
                for extension, data in formats.items():
                    require(cache.add_format(book_id, extension, io.BytesIO(data),
                                             replace=False, run_hooks=False), "fixture_format_failed")
        finally:
            close(legacy)
    baseline = {
        "source": snapshot(os.path.join(root, "S")),
        "destination": snapshot(os.path.join(root, "S - Unresolvable")),
        "keep": [case_id for case_id, _, _, action in cases if action == "keep"],
        "move": [case_id for case_id, _, _, action in cases if action == "move"],
    }
    backup = os.path.join(root, "external-fixture-backup")
    os.makedirs(backup)
    for name in ("S", "S - Unresolvable"):
        shutil.copytree(os.path.join(root, name), os.path.join(backup, name))
    require(snapshot(os.path.join(backup, "S")) == baseline["source"], "source_backup_mismatch")
    require(snapshot(os.path.join(backup, "S - Unresolvable")) == baseline["destination"],
            "destination_backup_mismatch")
    with open(os.path.join(root, "baseline.json"), "w", encoding="utf-8") as stream:
        json.dump(baseline, stream, ensure_ascii=True, sort_keys=True, indent=2)
    print(json.dumps({"created": True, "source_records": len(cases), "destination_records": 1,
                      "fixture_backups_verified": True}, sort_keys=True))


def verify(root):
    from calibre.ebooks.metadata import author_to_author_sort

    with open(os.path.join(root, "generated-fixture.marker"), encoding="ascii") as stream:
        require(stream.read() == MARKER, "not_a_generated_fixture")
    with open(os.path.join(root, "baseline.json"), encoding="utf-8") as stream:
        before = json.load(stream)
    for key, name in (("source", "S"), ("destination", "S - Unresolvable")):
        original = snapshot(os.path.join(root, "external-fixture-backup", name))
        require(original["uuid"] == before[key]["uuid"], "fixture_backup_uuid_changed")
        for case_id, saved in before[key]["records"].items():
            require(all(original["records"][case_id][field] == value for field, value in saved.items()),
                    "fixture_backup_baseline_changed")
        before[key] = original
    source = snapshot(os.path.join(root, "S"))
    destination = snapshot(os.path.join(root, "S - Unresolvable"))
    require(source["uuid"] == before["source"]["uuid"], "source_uuid_changed")
    require(destination["uuid"] == before["destination"]["uuid"], "destination_uuid_changed")
    require(set(source["records"]) == set(before["keep"]), "source_record_set_mismatch")
    require(set(destination["records"]) == set(before["move"]) | {"existing"}, "destination_record_set_mismatch")
    require(destination["records"]["existing"] == before["destination"]["records"]["existing"],
            "existing_destination_record_changed")
    for case_id, original in before["source"]["records"].items():
        actual = (source if case_id in before["keep"] else destination)["records"][case_id]
        for field in ("tags", "rating", "comments", "custom", "formats", "extras", "cover",
                      "identifiers", "publication"):
            require(actual[field] == original[field], "preservation_mismatch:" + field + ":" + case_id)
        expected_title = "Estuary Logic" if case_id == "2" else original["title"]
        expected_authors = ["Case Writer Epsilon"] if case_id == "10" else original["authors"]
        require(actual["title"] == expected_title, "title_mismatch:" + case_id)
        require(actual["authors"] == expected_authors, "authors_mismatch:" + case_id)
        expected_sorts = ([author_to_author_sort("Case Writer Epsilon")] if case_id == "10"
                          else original["authorSortValues"])
        require(actual["authorSortValues"] == expected_sorts, "author_sorts_mismatch:" + case_id)
        require(actual["authorSort"] == (" & ".join(expected_sorts) if case_id == "10"
                                        else original["authorSort"]), "book_author_sort_mismatch:" + case_id)
        require(len(actual["path"].replace("\\", "/").split("/")) == 2, "managed_layout_mismatch:" + case_id)
        library = source if case_id in before["keep"] else destination
        require(actual["path"].endswith(" ({})".format(library["recordIds"][case_id])),
                "managed_record_suffix_mismatch:" + case_id)
    print(json.dumps({"verified": True, "retained_input": 12, "qualified_remaining": 8,
                      "verified_moved": 4, "existing_destination_records": 1,
                      "formats_covers_extras_custom_values_preserved": True}, sort_keys=True))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("action", choices=("create", "verify"))
    parser.add_argument("root")
    parser.add_argument("confirmation")
    arguments = parser.parse_args()
    require(arguments.confirmation == MARKER, "generated_fixture_confirmation_missing")
    root = os.path.realpath(arguments.root)
    require(len(os.path.join(root, "S - Unresolvable")) < 75, "fixture_destination_path_too_long")
    (create if arguments.action == "create" else verify)(root)


if __name__ == "__main__":
    main()

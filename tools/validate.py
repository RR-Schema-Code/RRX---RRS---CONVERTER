"""Check an RRS-to-RRX pilot against source row counts and RRX relationships."""

import hashlib
import sqlite3
import sys
import uuid
import xml.etree.ElementTree as ET
from pathlib import Path


def count(db, table):
    return db.execute(f'SELECT COUNT(*) FROM "{table}"').fetchone()[0]


def stable_id(text):
    return str(uuid.UUID(bytes_le=hashlib.md5(text.encode("utf-8")).digest()))


def is_guid(text):
    try:
        uuid.UUID(text)
        return True
    except ValueError:
        return False


def value(row, column):
    entry = row[column]
    return "" if entry is None else str(entry)


def main(source, output):
    source, output = Path(source), Path(output)
    expected = {"components": 0, "datapoints": 0, "wires": 0,
                "articles": 0, "additionalarticles": 0, "pages": 0,
                "covers": 0, "index_folders": 0, "page_frames": 0}
    mapping = {"Betriebsmittel": "components", "Datenpunkt": "datapoints",
               "Kabel": "wires", "Artikel": "articles",
               "Zusatzartikel": "additionalarticles"}
    isps = list(source.glob("*/Datenbank.db3"))
    isp_dirs = [folder for folder in source.iterdir()
                if folder.is_dir() and is_guid(folder.name)]
    project_xml = source / "Projekt.xml"
    expected_isps = []
    if project_xml.exists():
        project_root = ET.parse(project_xml).getroot()
        expected_isps = [(e.get("ID", ""), e.get("Name", ""),
                          e.get("Beschreibung", ""), e.get("BMK", ""),
                          e.get("VorBMK", ""))
                         for e in project_root.iter("ISP")]
    for folder in isp_dirs:
        old_path = folder / "Datenbank.db3"
        if old_path.exists():
            with sqlite3.connect(old_path) as old:
                for old_table, new_table in mapping.items():
                    expected[new_table] += count(old, old_table)
        pages_file = folder / "Seiten.xml"
        if pages_file.exists():
            root = ET.parse(pages_file).getroot()
            for page in root.iter():
                if page.tag == "Verzeichnis":
                    expected["index_folders"] += 1
                if page.tag in {"BlattD", "BlattA"}:
                    expected["pages"] += 1
                if page.tag == "BlattD":
                    expected["covers"] += 1
        else:
            expected["pages"] += sum(is_guid(path.stem) and
                                      path.stem.lower() != "11111111-1111-1111-1111-111111111111"
                                      for path in folder.glob("*.xml"))

    errors = []
    with sqlite3.connect(output) as new:
        integrity = new.execute("PRAGMA integrity_check").fetchone()[0]
        if integrity != "ok":
            errors.append(f"SQLite integrity: {integrity}")
        actual = {name: count(new, name) for name in mapping.values()}
        actual["pages"] = new.execute(
            "SELECT COUNT(*) FROM items WHERE type IN "
            "('xCover','xIndexPage','xSystemPage')").fetchone()[0]
        actual["covers"] = new.execute(
            "SELECT COUNT(*) FROM items WHERE type='xCover'").fetchone()[0]
        actual["index_folders"] = new.execute(
            "SELECT COUNT(*) FROM items WHERE type='xIndexFolder'").fetchone()[0]
        actual["page_frames"] = new.execute(
            "SELECT COUNT(*) FROM elements_normal WHERE "
            "type='xImage' AND name='RRS Blattvorlage'").fetchone()[0]
        for name, expected_count in expected.items():
            if actual[name] != expected_count:
                errors.append(f"{name}: source {expected_count}, output {actual[name]}")

        if expected_isps:
            actual_isps = new.execute(
                "SELECT uid,name,description,x00,x01 FROM items "
                "WHERE type='xAsp' ORDER BY rowid").fetchall()
            declared = actual_isps[:len(expected_isps)]
            extra_ids = {row[0] for row in actual_isps[len(expected_isps):]}
            expected_extra_ids = {folder.name for folder in isp_dirs} - {row[0] for row in expected_isps}
            if declared != expected_isps or extra_ids != expected_extra_ids:
                errors.append("ISP order or header data differs from Projekt.xml")

        for table in ["items", *mapping.values()]:
            duplicates = new.execute(
                f'SELECT COUNT(*) - COUNT(DISTINCT uid) FROM "{table}"'
            ).fetchone()[0]
            if duplicates:
                errors.append(f"{table}: {duplicates} duplicate UIDs")

        checks = {
            "index pages": "SELECT COUNT(*) FROM items WHERE type='xIndexPage'",
            "drawn covers": "SELECT COUNT(*) FROM elements_normal e JOIN items i ON i.uid=e.pid "
                            "WHERE i.type='xCover'",
            "orphan items": "SELECT COUNT(*) FROM items i WHERE i.type!='xProject' "
                            "AND NOT EXISTS (SELECT 1 FROM items p WHERE p.uid=i.pid)",
            "orphan elements": "SELECT COUNT(*) FROM elements_normal e WHERE "
                               "NOT EXISTS (SELECT 1 FROM items p WHERE p.uid=e.pid)",
            "orphan component data": "SELECT COUNT(*) FROM components c WHERE "
                                     "NOT EXISTS (SELECT 1 FROM items p WHERE p.uid=c.pid)",
            "orphan datapoints": "SELECT COUNT(*) FROM datapoints d WHERE "
                                 "NOT EXISTS (SELECT 1 FROM components c WHERE c.uid=d.cid)",
            "datapoints on wrong page": "SELECT COUNT(*) FROM datapoints d JOIN components c "
                                        "ON c.uid=d.cid WHERE d.pid!=c.pid",
            "wires on wrong page": "SELECT COUNT(*) FROM wires w JOIN components c "
                                   "ON c.uid=w.cid WHERE w.pid!=c.pid",
            "articles on wrong page": "SELECT COUNT(*) FROM articles a JOIN components c "
                                      "ON c.uid=a.cid WHERE a.pid!=c.pid",
            "additional articles on wrong page": "SELECT COUNT(*) FROM additionalarticles a "
                                                 "JOIN components c ON c.uid=a.cid "
                                                 "WHERE a.pid!=c.pid",
            "orphan symbols": "SELECT COUNT(*) FROM elements_normal e WHERE "
                              "e.type='xComponent' AND NOT EXISTS "
                              "(SELECT 1 FROM components c WHERE c.uid=e.uid)",
            "components without symbols": "SELECT COUNT(*) FROM components c WHERE "
                                          "NOT EXISTS (SELECT 1 FROM elements_normal e "
                                          "WHERE e.type='xComponent' AND e.uid=c.uid)",
            "zero component factors": "SELECT COUNT(*) FROM components WHERE x73!='1'",
        }
        for label, sql in checks.items():
            n = new.execute(sql).fetchone()[0]
            if n:
                errors.append(f"{label}: {n}")

        # RRX paints in row order; converted rows must follow RRS Canvas.ZIndex.
        last_z = {}
        for page_id, sequence in new.execute(
                "SELECT pid,sequence FROM elements_normal WHERE type!='xPage' "
                "ORDER BY rowid"):
            z = int(sequence)
            if page_id in last_z and z < last_z[page_id]:
                errors.append(f"z-index order differs on page {page_id}")
                break
            last_z[page_id] = z

        # Verify the important RRS fields record by record, not only row counts.
        for old_path in isps:
            isp_id = old_path.parent.name
            with sqlite3.connect(old_path) as old:
                old.row_factory = sqlite3.Row
                for row in old.execute("SELECT * FROM Betriebsmittel"):
                    old_page = value(row, "ExportID")
                    component_id = stable_id(
                        f"component|{isp_id}|{old_page}|{value(row, 'GUID')}")
                    actual_row = new.execute(
                        "SELECT pid,x03,x04,x06,x07,x08,x12,x16,x17,x73 "
                        "FROM components WHERE uid=?", (component_id,)).fetchone()
                    expected_row = (
                        stable_id(f"page|{isp_id}|{old_page}"),
                        *(value(row, field) for field in (
                            "Vorzeichen", "Zähler", "Funktionstext", "Montageort",
                            "TechnischeAngabe", "Graviertext", "Anlage",
                            "AnlagenBeschreibung")), "1")
                    if actual_row != expected_row:
                        errors.append(f"Betriebsmittel field mismatch: {component_id}")
                        break

                for point_index, row in enumerate(old.execute("SELECT * FROM Datenpunkt")):
                    old_page = value(row, "ExportID")
                    point_id = stable_id(
                        f"datapoint|{isp_id}|{old_page}|"
                        f"{value(row, 'RelationID')}|{point_index}")
                    actual_row = new.execute(
                        "SELECT cid,x02,x03,x05,x06,x07,x11,x12,x13,x17,x20,x21,x31,x32 "
                        "FROM datapoints WHERE uid=?", (point_id,)).fetchone()
                    expected_row = (
                        stable_id(f"component|{isp_id}|{old_page}|{value(row, 'GUID')}"),
                        *(value(row, field) for field in (
                            "Vorzeichen", "Zähler", "Art", "Kommunikation", "Signal",
                            "Sonstiges", "Funktion", "FunktionKurz")), "",
                        *(value(row, field) for field in (
                            "ZF_1", "ZF_2", "Bemerkung", "Typ")))
                    if actual_row != expected_row:
                        errors.append(f"Datenpunkt field mismatch: {point_id}")
                        break

            # RRS stores Kamm/Info relative to the component, but the border
            # dock is a separate Connection to the center of a DaP anchor.
            for drawing in old_path.parent.glob("*.xml"):
                if drawing.name == "Seiten.xml":
                    continue
                root = ET.parse(drawing).getroot()
                # RRS can store the same anchor ID more than once. Its canvas
                # resolves the first matching item, as does the converter.
                anchors = {}
                for entry in root.findall("./DaP/DaPi"):
                    anchors.setdefault(entry.findtext("ID", ""), entry)
                docks = {}
                orientation_codes = {"Top": "1", "Left": "2", "Right": "3", "Bottom": "4"}
                for link in root.findall("./Connections/Connection"):
                    source_id = link.findtext("SourceID", "")
                    anchor = anchors.get(link.findtext("SinkID", ""))
                    orientation = orientation_codes.get(link.findtext("SourceConnectorName", ""))
                    if anchor is not None and orientation is not None:
                        dock_x = float(anchor.findtext("Left", "0")) + float(anchor.findtext("Width", "0")) / 2
                        dock_y = float(anchor.findtext("Top", "0")) + float(anchor.findtext("Height", "0")) / 2
                        docks[source_id] = (orientation, dock_x, dock_y)
                for section in root:
                    if section.tag != "D":
                        continue
                    for element in section:
                        if element.get("Funktion") != "Betriebsmittel":
                            continue
                        kamm = element.find("Kamm")
                        if kamm is None:
                            errors.append(f"missing RRS info point: {drawing.name}")
                            continue
                        old_id = element.findtext("ID", "")
                        component_id = stable_id(
                            f"component|{isp_id}|{drawing.stem}|{old_id}")
                        current = new.execute(
                            "SELECT x21,x22,x23,x24,x25,x26 FROM elements_normal "
                            "WHERE type='xComponent' AND uid=?", (component_id,)
                        ).fetchone()
                        expected_x = float(element.findtext("Left", "0")) + float(kamm.get("X", "0"))
                        expected_y = float(element.findtext("Top", "0")) + float(kamm.get("Y", "0"))
                        if current is None or current[3:5] != ("1", "0"):
                            errors.append(f"missing RRX component info: {component_id}")
                            continue
                        point = tuple(map(float, current[5].split(";")))
                        if abs(point[0] - expected_x) > 1e-7 or abs(point[1] - expected_y) > 1e-7:
                            errors.append(f"info point mismatch: {component_id}")
                        dock = docks.get(old_id)
                        if dock is None:
                            if current[0] == "1":
                                errors.append(f"unexpected RRX component dock: {component_id}")
                        elif current[0:2] != ("1", dock[0]):
                            errors.append(f"missing RRX component dock: {component_id}")
                        else:
                            dock_point = tuple(map(float, current[2].split(";")))
                            if abs(dock_point[0] - dock[1]) > 1e-7 or abs(dock_point[1] - dock[2]) > 1e-7:
                                errors.append(f"dock point mismatch: {component_id}")

    print(f"ISPs: {len(isp_dirs)}")
    for name, amount in actual.items():
        print(f"{name}: {amount}")
    print("Validation:", "OK" if not errors else "; ".join(errors))
    return 0 if not errors else 1


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit("usage: python validate.py <RRS folder> <output.rrxp>")
    sys.exit(main(*sys.argv[1:]))

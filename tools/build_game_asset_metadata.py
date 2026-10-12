#!/usr/bin/env python3
"""Build Arkive's searchable in-game-name to UE asset-reference index."""

from __future__ import annotations

import argparse
import collections
import html
import json
import re
import sqlite3
from datetime import datetime, timezone
from pathlib import Path


# These are high-confidence, direct presentation-resource fields. Shared world
# drop models and numeric tuning fields are intentionally excluded.
TABLES: tuple[tuple[str, str, tuple[str, ...]], ...] = (
    # PC.LookInfo is the authoritative playable-class archetype reference. It is
    # intentionally separate from NPC rows that may reuse a playable class look.
    ("EFTable_PC.db", "PC", ("LookInfo",)),
    ("EFTable_Npc.db", "Npc", ("Model", "Icon", "MapIcon")),
    ("EFTable_Item.db", "Item", ("Model", "InstrumentModel", "Icon")),
    ("EFTable_Vehicle.db", "Vehicle", ("Model", "RealmTemporaryModel", "Icon")),
    ("EFTable_Polymorph.db", "Polymorph", ("Model",)),
    ("EFTable_Prop.db", "Prop", ("Model",)),
    ("EFTable_Tower.db", "Tower", ("Model",)),
    ("EFTable_TownBuilding.db", "TownBuilding", ("Model",)),
    ("EFTable_TownNpcDress.db", "TownNpcDress", ("Model", "IconName")),
    ("EFTable_Transit.db", "Transit", ("Model",)),
    ("EFTable_Trap.db", "Trap", ("Model",)),
    ("EFTable_VoyageShip.db", "VoyageShip", ("Model",)),
    ("EFTable_VoyageShipAvatar.db", "VoyageShipAvatar", ("Model", "Icon")),
    ("EFTable_DropEther.db", "DropEther", ("Model",)),
    ("EFTable_Money.db", "Money", ("DropModel", "Icon")),
    ("EFTable_Emoticon.db", "Emoticon", ("IconPath", "ResourcePath")),
    ("EFTable_Pet.db", "Pet", ("Icon",)),
    ("EFTable_Skill.db", "Skill", ("Icon",)),
    ("EFTable_SkillBuff.db", "SkillBuff", ("Icon",)),
    ("EFTable_Ability.db", "Ability", ("Icon",)),
    ("EFTable_QuestItem.db", "QuestItem", ("Icon",)),
)

WRAPPED_REFERENCE = re.compile(r"^[A-Za-z0-9_]+['\"](?P<value>[^'\"]+)['\"]$")
MARKUP = re.compile(r"<[^>]+>|\$\([^)]*\)|\{[^}]*\}")
MAX_OBJECT_ONLY_ASSOCIATIONS = 250


def resolve_display_name(messages: dict[str, str], name_key: object) -> tuple[str, str]:
    raw_name = str(name_key or "")
    display_name = messages.get(raw_name)
    if not display_name and not raw_name.startswith(("tip.", "ui.", "$")):
        display_name = clean_display_name(raw_name)
    return raw_name, display_name or ""


def clean_display_name(value: str) -> str:
    value = html.unescape(MARKUP.sub("", value)).replace("\\n", " ")
    return " ".join(value.split()).strip()


def split_reference(reference: str) -> tuple[str, str, str]:
    reference = reference.strip()
    wrapped = WRAPPED_REFERENCE.match(reference)
    if wrapped:
        reference = wrapped.group("value")
    reference = reference.replace("\\", "/").rsplit("/", 1)[-1]
    if "." in reference:
        package_name, object_name = reference.rsplit(".", 1)
    else:
        package_name, object_name = "", reference
    return reference, package_name, object_name


def load_messages(path: Path, language: str) -> dict[str, str]:
    table = f"GameMsg_{language}"
    with sqlite3.connect(path) as connection:
        return {
            key: cleaned
            for key, message in connection.execute(f'SELECT "KEY", "MSG" FROM "{table}"')
            if key and message and (cleaned := clean_display_name(str(message)))
        }


def build_index(root: Path, language: str) -> dict[str, object]:
    messages = load_messages(root / "EFTable_GameMsg.db", language)
    associations: list[dict[str, object]] = []
    seen: set[tuple[object, ...]] = set()

    for database_name, table, requested_fields in TABLES:
        database = root / database_name
        if not database.exists():
            continue
        with sqlite3.connect(database) as connection:
            columns = [row[1] for row in connection.execute(f'PRAGMA table_info("{table}")')]
            if "PrimaryKey" not in columns or "Name" not in columns:
                continue
            fields = tuple(field for field in requested_fields if field in columns)
            if not fields:
                continue
            selected = ("PrimaryKey", "Name", *fields)
            sql = "SELECT " + ", ".join(f'"{column}"' for column in selected) + f' FROM "{table}"'
            for row in connection.execute(sql):
                entity_id, name_key, *values = row
                if not name_key:
                    continue
                raw_name, display_name = resolve_display_name(messages, name_key)
                if table == "PC":
                    display_name = messages.get(
                        f"tip.name.enum_playerclass_{raw_name.casefold()}", display_name
                    )
                if not display_name:
                    continue
                for field, value in zip(fields, values):
                    if not isinstance(value, str) or not value.strip() or value == "0":
                        continue
                    reference, package_name, object_name = split_reference(value)
                    if not object_name or object_name.replace("_", "").isdigit():
                        continue
                    identity = (display_name, table, entity_id, field, reference)
                    if identity in seen:
                        continue
                    seen.add(identity)
                    associations.append(
                        {
                            "displayName": display_name,
                            "nameKey": raw_name,
                            "entityType": table,
                            "entityId": int(entity_id),
                            "field": field,
                            "assetReference": reference,
                            "packageName": package_name,
                            "objectName": object_name,
                        }
                    )

    object_only_counts = collections.Counter(
        str(item["objectName"]).casefold()
        for item in associations
        if not item["packageName"]
    )
    unambiguous = [
        item
        for item in associations
        if item["packageName"]
        or object_only_counts[str(item["objectName"]).casefold()]
        <= MAX_OBJECT_ONLY_ASSOCIATIONS
    ]
    skipped_ambiguous = len(associations) - len(unambiguous)
    unambiguous.sort(
        key=lambda item: (
            str(item["packageName"]).casefold(),
            str(item["objectName"]).casefold(),
            str(item["displayName"]).casefold(),
            str(item["entityType"]),
            int(item["entityId"]),
        )
    )
    entities = build_entities(root, messages)
    customizations = build_customizations(root)
    return {
        "version": 2,
        "language": language,
        "sourceRoot": str(root.resolve()),
        "generatedAt": datetime.now(timezone.utc).isoformat(),
        "skippedAmbiguousAssociations": skipped_ambiguous,
        "associations": unambiguous,
        "entities": entities,
        "customizations": customizations,
    }


def build_entities(root: Path, messages: dict[str, str]) -> list[dict[str, object]]:
    entities: list[dict[str, object]] = []
    pc_path = root / "EFTable_PC.db"
    if pc_path.exists():
        with sqlite3.connect(pc_path) as connection:
            connection.row_factory = sqlite3.Row
            for row in connection.execute(
                'SELECT "PrimaryKey", "Name", "BaseClass", "Creatable", "Released", '
                '"GenderType", "LookInfo" FROM "PC"'
            ):
                name_key, display_name = resolve_display_name(messages, row["Name"])
                display_name = messages.get(
                    f"tip.name.enum_playerclass_{name_key.casefold()}", display_name
                )
                if not display_name:
                    continue
                base_id = int(row["BaseClass"] or 0)
                entity_id = int(row["PrimaryKey"])
                detail = (
                    f"base={base_id or '-'}; creatable={int(row['Creatable'] or 0)}; "
                    f"released={int(row['Released'] or 0)}; gender={int(row['GenderType'] or 0)}"
                )
                entities.append(
                    {
                        "displayName": display_name,
                        "nameKey": name_key,
                        "entityType": "PC",
                        "entityId": entity_id,
                        "category": "Playable class" if row["Released"] else "Player archetype",
                        "detail": detail,
                        "baseEntityId": base_id if base_id and base_id != entity_id else None,
                        "primaryAssetReference": str(row["LookInfo"] or "") or None,
                    }
                )

    npc_path = root / "EFTable_Npc.db"
    if npc_path.exists():
        with sqlite3.connect(npc_path) as connection:
            connection.row_factory = sqlite3.Row
            for row in connection.execute(
                'SELECT "PrimaryKey", "Name", "Model", "Grade", "Type", "Function", '
                '"FunctionType", "TitleName" FROM "Npc"'
            ):
                name_key, display_name = resolve_display_name(messages, row["Name"])
                if not display_name:
                    continue
                function = int(row["Function"] or 0)
                function_type = int(row["FunctionType"] or 0)
                grade = int(row["Grade"] or 0)
                npc_type = int(row["Type"] or 0)
                if function or function_type:
                    category = "Functional NPC / vendor"
                elif grade:
                    category = "Combat NPC"
                else:
                    category = "NPC"
                title_key = str(row["TitleName"] or "")
                title = messages.get(title_key, clean_display_name(title_key))
                detail_parts = [f"grade={grade}", f"type={npc_type}"]
                if function or function_type:
                    detail_parts.append(f"function={function}/{function_type}")
                if title:
                    detail_parts.append(f"title={title}")
                entities.append(
                    {
                        "displayName": display_name,
                        "nameKey": name_key,
                        "entityType": "Npc",
                        "entityId": int(row["PrimaryKey"]),
                        "category": category,
                        "detail": "; ".join(detail_parts),
                        "baseEntityId": None,
                        "primaryAssetReference": str(row["Model"] or "") or None,
                    }
                )

    entities.sort(
        key=lambda item: (
            0 if item["entityType"] == "PC" else 1,
            str(item["displayName"]).casefold(),
            int(item["entityId"]),
        )
    )
    return entities


def build_customizations(root: Path) -> list[dict[str, object]]:
    path = root / "EFTable_CharacterCustomizing.db"
    if not path.exists():
        return []
    customizations: list[dict[str, object]] = []
    with sqlite3.connect(path) as connection:
        for row in connection.execute(
            'SELECT "PrimaryKey", "SecondaryKey", "SortOrder", "Object_Unit", '
            '"Object_String", "Icon", "IconIndex", "MenuName" '
            'FROM "CharacterCustomizing" ORDER BY "PrimaryKey", "SecondaryKey", "SortOrder"'
        ):
            (
                player_id,
                category_id,
                ui_position,
                object_unit,
                object_reference,
                icon_reference,
                icon_index,
                menu_name,
            ) = row
            customizations.append(
                {
                    "playerClassId": int(player_id),
                    "categoryId": int(category_id),
                    "uiPosition": int(ui_position),
                    "objectUnit": int(object_unit),
                    "objectReference": str(object_reference or ""),
                    "iconReference": str(icon_reference or ""),
                    "iconIndex": int(icon_index),
                    "menuName": str(menu_name or ""),
                }
            )
    return customizations


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("database_root", type=Path, help="folder containing EFTable_*.db")
    parser.add_argument("output", type=Path, help="Arkive metadata JSON output")
    parser.add_argument("--language", default="English")
    args = parser.parse_args()
    if not (args.database_root / "EFTable_GameMsg.db").exists():
        parser.error("database_root must contain EFTable_GameMsg.db")
    document = build_index(args.database_root, args.language)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    temporary = args.output.with_suffix(args.output.suffix + ".tmp")
    temporary.write_text(
        json.dumps(document, ensure_ascii=False, separators=(",", ":")),
        encoding="utf-8",
    )
    temporary.replace(args.output)
    print(
        f"Wrote {len(document['associations']):,} associations, "
        f"{len(document['entities']):,} entities and "
        f"{len(document['customizations']):,} customization options to {args.output}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

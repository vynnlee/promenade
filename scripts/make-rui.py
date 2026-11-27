#!/usr/bin/env python3
"""Generates ui/Promenade.rui (toolbar + macros + icons) from ui/icons/*.svg and the table below.

The GUIDs are fixed on purpose: Rhino remembers toolbars, buttons and their placement by GUID,
so never regenerate them. A new command gets new GUIDs; a removed one takes its GUIDs with it.
Usage: scripts/make-rui.py [--check]   (--check exits 1 if ui/Promenade.rui is out of date)
"""
import pathlib
import re
import sys
import uuid
from xml.sax.saxutils import escape

ROOT = pathlib.Path(__file__).resolve().parent.parent
ICONS = ROOT / "ui" / "icons"
OUT = ROOT / "ui" / "Promenade.rui"

PLUG_IN_GUID = "ea7baea5-2d20-4842-a9c5-1b12a6cf4e3a"  # [assembly: Guid] in src/WalkCommand.cs
RUI_GUID = "bc8e8c36-212f-40b8-8961-5918932bb4c3"
# Group and dock-bar GUIDs were replaced once in 0.2.0: Rhino 9 stores each converted container under
# dock_bar_guid64 in its window layout, and test installs had saved the old ones with an empty placement.
GROUP_GUID = "20066968-e9c5-46c5-8329-16645777bd92"
DOCK_BAR_GUID32 = "4bb43680-72d6-4519-89e7-3c9ccb0653c3"
DOCK_BAR_GUID64 = "f24103b3-0532-4579-9db6-8f099f840a3a"
GROUP_ITEM_GUID = "f17e1451-daa5-4d20-806b-cf42de5d8a2a"
TOOLBAR_GUID = "5f3b7eea-7202-48e5-bbec-036463bca868"
TOOLBAR_ICON_GUID = "b4160536-b936-4971-a2ce-dbd49d9f0b59"  # ui/icons/Promenade.svg
TITLE = "Promenade"
# Where the floating container first appears (points). 412x70 fits the 12 buttons + 2 spacers.
FLOAT_POINT, FLOAT_SIZE = "200,200", "412,70"

# (command, button text, tooltip, toolbar item GUID, macro GUID = icon GUID). None = spacer (its GUID).
# Tooltips name the in-walk hotkey, so the toolbar doubles as the cheat sheet.
COMMANDS = [
    ("Walk", "Walk", "Walk: first-person walkthrough (WASD, Space jump, mouse look, right-click to teleport, left-click for cursor, M measure, Esc to exit)",
     "d79c4638-bbe4-4f68-be89-5873c11ad391", "394d49fa-76a4-4f84-9a4f-fa9eb1f6ec1d"),
    (None, "ae642325-9f0a-4ea0-bcb6-c99cbcb00a07"),
    ("WalkEyeHeight", "Eye Height", "Eye height: Standing, Camera, Wheelchair or Child (H while walking)",
     "8786c6c9-9dec-44be-9c3a-fbef37f0887a", "b4e5c79c-09d0-45b2-9a03-67c0d9a0289c"),
    ("WalkLens", "Lens", "Lens: 14, 18, 24, 35 or 50 mm (1-5 and -/= while walking)",
     "4a3a3d2d-8167-4f67-823e-55ef6ef3b49c", "3dd4cfec-7ae5-4cfc-ba86-d9008afcb0dd"),
    ("WalkTwoPoint", "Two-Point", "Two-point perspective: keep verticals vertical (T while walking)",
     "7c732bd9-fc7a-4350-a6c7-8779006350a9", "455d377a-f866-4db1-825b-e5070479f19d"),
    ("WalkDisplayMode", "Display Mode", "Display mode: cycle viewport display modes (V while walking)",
     "16988002-89c8-44c2-9823-c5391adde4ef", "2b7ca373-2a6b-45c3-b33d-3810cea4985a"),
    ("WalkFrame", "Frame", "Frame: aspect frame for shots, 16:9, 3:2, 4:5, 1:1, A, 2.39:1 or off (F while walking)",
     "fd1b25eb-f7e6-4246-81bf-200f555d9b9f", "6df96659-090e-499b-a4aa-7cc9e496a21a"),
    ("WalkGuides", "Guides", "Guides: rule-of-thirds grid and eye-level horizon (G while walking)",
     "03664e8d-f64e-4eb4-8abb-0b2d926cb91e", "cfcb2264-b244-4998-8c29-df2a33054318"),
    ("WalkScreenshot", "Screenshot", "Screenshot: save the view as a PNG and a named view (P while walking)",
     "f47cd404-eca8-4f39-84f0-f094cf0b0d8e", "92d9be10-596f-48d1-9c1c-f67bcc7ff999"),
    (None, "8c9f4970-3431-4cf4-9de1-830e4e67b246"),
    ("WalkSaveView", "Save View", 'Save view: store the current view as named view "Walk NN" (N while walking)',
     "4242af23-aa54-4ad1-82de-b3541cec4458", "1c912ed7-9005-4d70-a89d-ad8a689b6c01"),
    ("WalkTour", "Tour", "Tour: animate through the saved Walk views (, and . step views while walking)",
     "ad004a31-974c-477b-a67a-20d525ba36e5", "1c8660de-64c8-4738-86a6-7c9171b564e0"),
    ("WalkSun", "Sun", "Sun: set the sun time ([ and ] change the hour while walking)",
     "0fd402cd-b6e6-4974-9b22-b29f30e1327f", "63072a8b-5231-4384-8bec-1da8c12d39dd"),
    ("WalkBlocks", "Blocks", "Blocks: block mode easter egg — right-click place, hold left-click break (B while walking)",
     "8e580b26-0a5c-46c1-88da-58d33aac962d", "59c0a3ac-393f-4d52-9de1-ffcce26506a0"),
]


def text(tag, value, indent):
    return f"{indent}<{tag}><locale_1033>{escape(value)}</locale_1033></{tag}>"


def svg(name):
    s = (ICONS / f"{name}.svg").read_text(encoding="utf-8").strip()
    if s.startswith("<?xml"):
        s = s[s.index("?>") + 2:].strip()
    if not s.startswith("<svg"):
        sys.exit(f"{name}.svg: expected a bare <svg> element")
    return s


def icon(guid, name):
    return (f'    <icon guid="{guid}">\n'
            f"      <light>{svg(name)}</light>\n"
            f"      <dark>{svg(name + '_dark')}</dark>\n"
            f"    </icon>")


def build():
    guids = re.findall(r'"([0-9a-f-]{36})"', pathlib.Path(__file__).read_text())
    assert len(guids) == len(set(guids)) == len({str(uuid.UUID(g)) for g in guids}), "GUIDs must be unique and valid"
    items, macros, icons = [], [], [icon(TOOLBAR_ICON_GUID, "Promenade")]
    for row in COMMANDS:
        if row[0] is None:
            items.append(f'      <tool_bar_item guid="{row[1]}" button_style="spacer" />')
            continue
        cmd, button, tip, item_guid, macro_guid = row
        items.append("\n".join([
            f'      <tool_bar_item guid="{item_guid}" button_display_mode="control_only" button_style="normal">',
            text("text", button, "        "),
            f"        <left_macro_id>{macro_guid}</left_macro_id>",
            "      </tool_bar_item>"]))
        macros.append("\n".join([
            f'    <macro_item guid="{macro_guid}" bitmap_id="{macro_guid}">',
            text("text", f"Macro.{cmd}", "      "),
            text("tooltip", tip, "      "),
            text("help_text", tip, "      "),
            text("button_text", button, "      "),
            text("menu_text", button, "      "),
            f"      <script>! _{cmd}</script>",
            "    </macro_item>"]))
        icons.append(icon(macro_guid, cmd))

    return "\n".join([
        '<?xml version="1.0" encoding="utf-8"?>',
        f'<RhinoUI major_ver="5" minor_ver="0" guid="{RUI_GUID}" plug_in_guid="{PLUG_IN_GUID}" localize="False" default_language_id="1033">',
        "  <extend_rhino_menus />",
        "  <menus />",
        "  <tool_bar_groups>",
        f'    <tool_bar_group guid="{GROUP_GUID}" dock_bar_guid32="{DOCK_BAR_GUID32}" dock_bar_guid64="{DOCK_BAR_GUID64}" '
        f'active_tool_bar_group="{TOOLBAR_GUID}" single_file="False" hide_single_tab="True" point_floating="{FLOAT_POINT}">',
        text("text", TITLE, "      "),
        # Rhino 9 turns dock_bar_info into the container's placement; with only visible/floating set it
        # wrote an empty <placement /> and the container never showed. Same attribute set as Rhino's
        # own floating groups in default.rui (bar_id left out: Rhino 9 placements don't keep it).
        '      <dock_bar_info dpi_scale="100" dock_bar="False" docking="True" horz="False" visible="True" floating="True" '
        f'mru_float_style="8192" mru_width="1" point_pos="-1,-1" float_point="{FLOAT_POINT}" dock_location="top" float_size="{FLOAT_SIZE}" />',
        f'      <tool_bar_group_item guid="{GROUP_ITEM_GUID}" major_version="1" minor_version="1">',
        text("text", TITLE, "        "),
        f"        <tool_bar_id>{TOOLBAR_GUID}</tool_bar_id>",
        "      </tool_bar_group_item>",
        "    </tool_bar_group>",
        "  </tool_bar_groups>",
        "  <tool_bars>",
        f'    <tool_bar guid="{TOOLBAR_GUID}" bitmap_id="{TOOLBAR_ICON_GUID}" item_display_style="control_only">',
        text("text", TITLE, "      "),
        *items,
        "    </tool_bar>",
        "  </tool_bars>",
        "  <macros>",
        *macros,
        "  </macros>",
        "  <icons>",
        *icons,
        "  </icons>",
        "  <bitmaps />",
        "</RhinoUI>",
        ""])


if __name__ == "__main__":
    rui = build()
    if "--check" in sys.argv[1:]:
        if not OUT.exists() or OUT.read_text(encoding="utf-8") != rui:
            sys.exit(f"{OUT.relative_to(ROOT)} is out of date: run scripts/make-rui.py")
    else:
        OUT.write_text(rui, encoding="utf-8")
        print(f"Wrote {OUT.relative_to(ROOT)}")

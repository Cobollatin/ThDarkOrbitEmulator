"""Print only public news identifiers, countdowns, and matching layer metadata.

This deliberately ignores links, account data, request fields, and the HAR.
"""

import json
from pathlib import Path
import xml.etree.ElementTree as ET


SESSION = Path(__file__).resolve().parents[2] / "_reversing/Requests/sessions/20261021-224636-login/decoded"
news = json.loads((SESSION / "0086.json").read_text(encoding="utf-8"))["data"]
layers = {layer.attrib["name"]: layer for layer in ET.parse(SESSION / "0084.xml").getroot().findall("layer")}

for item in news:
    news_id = item["newsId"]
    assert news_id.startswith("BE_") and isinstance(item["seconds"], int)
    layer = layers[news_id[3:]]
    background = layer.find("./assignments/background")
    assert background is not None
    print(json.dumps({
        "newsId": news_id,
        "seconds": item["seconds"],
        "priority": layer.attrib.get("prio", ""),
        "category": layer.attrib.get("category", ""),
        "background": background.attrib.get("assetID", ""),
    }, ensure_ascii=False))

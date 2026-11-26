"""画像の解析の外部コンポーネント（設計書 第12.8節）。

決まった形の入力（画像のファイルと params.json）を受け取り、JSON を標準出力に返す。
同じ入力と同じ版なら、同じ結果を返す（乱数は使わない）。

  analyze /data/params.json   … 解析する
  selfcheck                   … 既知の見本で、検出することと誤検出しないことを確かめる

params.json の例：
  {"task": "diff", "before": "a.png", "after": "b.png", "threshold": 0.01, "cell": 16}
  {"task": "layout", "image": "a.png", "background_tolerance": 12}
"""

import io
import json
import sys
from importlib import metadata
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

VERSION = "0.1.0"


def versions():
    return {
        "component": "custos-analyzer-image",
        "version": VERSION,
        "python": sys.version.split()[0],
        "pillow": metadata.version("pillow"),
        "numpy": metadata.version("numpy"),
    }


def load(path):
    with Image.open(path) as image:
        return np.asarray(image.convert("RGB"), dtype=np.int16)


def diff(before, after, threshold=0.01, cell=16, tolerance=24):
    """見た目の差分：変わった画素の割合と、変わった区画（cell×cell）の外接矩形。"""
    if before.shape != after.shape:
        return {"comparable": False, "reason": f"大きさが違う {before.shape} と {after.shape}"}
    changed = np.abs(before - after).max(axis=2) > tolerance
    ratio = float(changed.mean())
    height, width = changed.shape
    cells = []
    for y in range(0, height, cell):
        for x in range(0, width, cell):
            if changed[y:y + cell, x:x + cell].mean() > 0.05:
                cells.append((x, y))
    box = None
    if cells:
        xs = [c[0] for c in cells]
        ys = [c[1] for c in cells]
        box = [min(xs), min(ys), min(width, max(xs) + cell) - min(xs), min(height, max(ys) + cell) - min(ys)]
    return {"comparable": True, "changed_ratio": round(ratio, 6), "changed_cells": len(cells), "changed_box": box, "changed": ratio > threshold}


def layout(image, background_tolerance=12, edge=2):
    """崩れの検出（簡単な規則）：内容が画面の右端・下端にはみ出している行・列、画面の大部分を占める単色の領域。"""
    height, width, _ = image.shape
    background = np.median(image.reshape(-1, 3), axis=0)
    content = np.abs(image - background).max(axis=2) > background_tolerance
    right = content[:, width - edge:].any(axis=1)
    bottom = content[height - edge:, :].any(axis=0)
    overflow_right_rows = int(right.sum())
    overflow_bottom_cols = int(bottom.sum())
    blank = float(1.0 - content.mean())
    problems = []
    if overflow_right_rows > height * 0.02:
        problems.append(f"内容が右端にはみ出している（{overflow_right_rows} 行）")
    if overflow_bottom_cols > width * 0.02:
        problems.append(f"内容が下端にはみ出している（{overflow_bottom_cols} 列）")
    if blank > 0.995:
        problems.append("ほぼ単色（描画されていない）")
    return {"overflow_right_rows": overflow_right_rows, "overflow_bottom_cols": overflow_bottom_cols, "blank_ratio": round(blank, 6), "problems": problems, "broken": bool(problems)}


def analyze(params_path):
    params_path = Path(params_path)
    params = json.loads(params_path.read_text(encoding="utf-8"))
    base = params_path.parent
    task = params.get("task")
    if task == "diff":
        result = diff(load(base / params["before"]), load(base / params["after"]), params.get("threshold", 0.01), params.get("cell", 16), params.get("tolerance", 24))
    elif task == "layout":
        result = layout(load(base / params["image"]), params.get("background_tolerance", 12))
    else:
        raise SystemExit(f"知らない task です：{task}")
    return {"task": task, "result": result, **versions()}


def sample(kind):
    """見本の画像を作る（決定的）。normal：余白の中に収まった箱と文字のような縞。broken：右端にはみ出した箱。"""
    image = Image.new("RGB", (320, 200), (250, 250, 250))
    draw = ImageDraw.Draw(image)
    draw.rectangle([20, 20, 200, 80], fill=(60, 120, 200))
    for i in range(5):
        draw.rectangle([30, 100 + i * 14, 180 - i * 10, 106 + i * 14], fill=(30, 30, 30))
    if kind == "broken":
        draw.rectangle([150, 120, 330, 160], fill=(200, 60, 60))
    if kind == "shifted":
        image = Image.new("RGB", (320, 200), (250, 250, 250))
        draw = ImageDraw.Draw(image)
        draw.rectangle([40, 20, 220, 80], fill=(60, 120, 200))
        for i in range(5):
            draw.rectangle([30, 100 + i * 14, 180 - i * 10, 106 + i * 14], fill=(30, 30, 30))
    buffer = io.BytesIO()
    image.save(buffer, format="PNG")
    buffer.seek(0)
    return np.asarray(Image.open(buffer).convert("RGB"), dtype=np.int16)


def selfcheck():
    normal = sample("normal")
    broken = sample("broken")
    shifted = sample("shifted")
    cases = {
        "layout_broken_detected": layout(broken)["broken"],
        "layout_normal_clean": not layout(normal)["broken"],
        "diff_identical_clean": not diff(normal, sample("normal"))["changed"],
        "diff_shifted_detected": diff(normal, shifted)["changed"],
    }
    return {
        "positives_detected": cases["layout_broken_detected"] and cases["diff_shifted_detected"],
        "negatives_clean": cases["layout_normal_clean"] and cases["diff_identical_clean"],
        "cases": cases,
        **versions(),
    }


def main(argv):
    if len(argv) >= 2 and argv[1] == "selfcheck":
        output = selfcheck()
    elif len(argv) >= 3 and argv[1] == "analyze":
        output = analyze(argv[2])
    else:
        raise SystemExit("使い方：analyzer.py analyze <params.json> | selfcheck")
    json.dump(output, sys.stdout, ensure_ascii=False, sort_keys=True)
    sys.stdout.write("\n")


if __name__ == "__main__":
    main(sys.argv)

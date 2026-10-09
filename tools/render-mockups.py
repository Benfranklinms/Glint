"""Renders UI mock-ups for the Windows fsearch port (Spotlight-style launcher)."""
from PIL import Image, ImageDraw, ImageFilter, ImageFont
import math, os

OUT = os.path.dirname(os.path.abspath(__file__))
F = "/workspace/work/fonts/inter/extras/ttf/Inter-%s.ttf"
def font(sz, w="Regular"): return ImageFont.truetype(F % w, sz)

W, H = 1600, 1000

def wallpaper():
    img = Image.new("RGB", (W, H))
    px = img.load()
    for y in range(H):
        for x in range(W):
            t = x / W; u = y / H
            r = int(20 + 60 * u + 40 * math.sin(t * 3))
            g = int(40 + 50 * t)
            b = int(110 + 90 * (1 - u) + 30 * math.cos(t * 2))
            px[x, y] = (max(0, min(255, r)), max(0, min(255, g)), max(0, min(255, b)))
    d = ImageDraw.Draw(img)
    for i, (cx, cy, rr, c) in enumerate([(300, 750, 420, (230, 90, 150)), (1300, 250, 380, (60, 180, 220)), (900, 900, 300, (250, 170, 80))]):
        d.ellipse([cx - rr, cy - rr, cx + rr, cy + rr], fill=c)
    img = img.filter(ImageFilter.GaussianBlur(160))
    # taskbar
    d = ImageDraw.Draw(img, "RGBA")
    d.rectangle([0, H - 48, W, H], fill=(20, 20, 28, 210))
    for i in range(6):
        x = W // 2 - 150 + i * 52
        d.rounded_rectangle([x, H - 40, x + 34, H - 8], 6, fill=(255, 255, 255, 40 if i else 90))
    return img

ICON = {"rs": (222, 120, 60), "pdf": (220, 60, 60), "md": (90, 110, 140), "dir": (240, 190, 60),
        "png": (70, 160, 110), "toml": (150, 100, 200), "docx": (50, 110, 210), "txt": (120, 120, 130)}

def icon(d, x, y, kind, s=30):
    c = ICON.get(kind, (120, 120, 130))
    if kind == "dir":
        d.rounded_rectangle([x, y + 5, x + s, y + s], 5, fill=c)
        d.rounded_rectangle([x, y + 2, x + s * 0.45, y + 10], 3, fill=c)
    else:
        d.rounded_rectangle([x + 3, y, x + s - 3, y + s], 5, fill=c)
        f = font(9 if len(kind) > 2 else 10, "Bold")
        tw = d.textlength(kind.upper(), font=f)
        d.text((x + s / 2 - tw / 2, y + s - 14), kind.upper(), font=f, fill="white")

def theme(dark):
    if dark:
        return dict(panel=(32, 32, 38, 215), line=(70, 70, 78), text=(240, 240, 245), sub=(160, 162, 172),
                    sel=(0, 120, 212, 255), seltext=(255, 255, 255), head=(140, 142, 152), chip=(68, 68, 78), hl=(255, 196, 0))
    return dict(panel=(248, 248, 250, 225), line=(214, 215, 222), text=(25, 25, 30), sub=(105, 108, 118),
                sel=(0, 103, 192, 255), seltext=(255, 255, 255), head=(120, 122, 130), chip=(220, 222, 230), hl=(200, 120, 0))

def panel(bg, x, y, w, h, t, r=18):
    """Acrylic: blur what is behind, tint, shadow."""
    shadow = Image.new("RGBA", bg.size, (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle([x, y + 14, x + w, y + h + 14], r, fill=(0, 0, 0, 120))
    shadow = shadow.filter(ImageFilter.GaussianBlur(28))
    bg.alpha_composite(shadow)
    crop = bg.crop((x, y, x + w, y + h)).filter(ImageFilter.GaussianBlur(40))
    mask = Image.new("L", (w, h), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, w - 1, h - 1], r, fill=255)
    bg.paste(crop, (x, y), mask)
    tint = Image.new("RGBA", (w, h), t["panel"])
    bg.paste(Image.alpha_composite(bg.crop((x, y, x + w, y + h)), tint), (x, y), mask)
    d = ImageDraw.Draw(bg, "RGBA")
    d.rounded_rectangle([x, y, x + w - 1, y + h - 1], r, outline=t["line"], width=1)
    return d

def searchbar(d, x, y, w, t, query, caret=True, chips=()):
    # magnifier
    d.ellipse([x + 24, y + 22, x + 46, y + 44], outline=t["sub"], width=3)
    d.line([x + 43, y + 41, x + 52, y + 50], fill=t["sub"], width=3)
    f = font(28)
    cx = x + 70
    for c in chips:
        cw = d.textlength(c, font=font(17, "Medium")) + 22
        d.rounded_rectangle([cx, y + 18, cx + cw, y + 50], 8, fill=t["chip"])
        d.text((cx + 11, y + 23), c, font=font(17, "Medium"), fill=t["text"])
        cx += cw + 8
    if query:
        d.text((cx, y + 16), query, font=f, fill=t["text"])
        cx += d.textlength(query, font=f)
    else:
        d.text((cx, y + 16), "Search files, folders and contents", font=f, fill=t["sub"])
    if caret:
        d.line([cx + 3, y + 18, cx + 3, y + 50], fill=t["text"], width=2)

def results(d, x, y, w, t, groups, sel=1, show_sub=True):
    i = 0
    for head, rows in groups:
        d.text((x + 24, y + 8), head.upper(), font=font(12, "SemiBold"), fill=t["head"])
        y += 30
        for kind, name, sub, extra in rows:
            selected = i == sel
            if selected:
                d.rounded_rectangle([x + 10, y, x + w - 10, y + 50], 10, fill=t["sel"])
            icon(d, x + 22, y + 10, kind)
            tc = t["seltext"] if selected else t["text"]
            sc = (220, 230, 245) if selected else t["sub"]
            d.text((x + 66, y + 6), name, font=font(17, "Medium"), fill=tc)
            if show_sub:
                d.text((x + 66, y + 28), sub, font=font(13), fill=sc)
            if extra:
                ew = d.textlength(extra, font=font(13))
                d.text((x + w - 28 - ew, y + 17), extra, font=font(13), fill=sc)
            y += 54; i += 1
        y += 6
    return y

def footer(d, x, y, w, t, text):
    d.line([x, y, x + w, y], fill=t["line"], width=1)
    d.text((x + 24, y + 12), text, font=font(13), fill=t["sub"])

GROUPS = [
    ("Top hit", [("rs", "main.rs", "~\\Developer\\fsearch\\src", "1.1 ms")]),
    ("Files", [("rs", "main_window.rs", "~\\Developer\\clipdrop-rs\\src", "Today"),
               ("md", "maintenance-notes.md", "~\\Documents\\MEC\\Labs", "Mon"),
               ("toml", "Cargo.toml", "~\\Developer\\fsearch", "2 Oct")]),
    ("Folders", [("dir", "main", "~\\Developer\\fsearch\\.git\\refs\\heads", "")]),
]

def sample1(dark=True, name="1-spotlight-dark.png"):
    t = theme(dark)
    bg = wallpaper().convert("RGBA")
    if not dark:
        bg = Image.blend(bg, Image.new("RGBA", bg.size, (235, 238, 245, 255)), 0.45)
    pw, x, y = 760, (W - 760) // 2, 150
    ph = 520
    d = panel(bg, x, y, pw, ph, t)
    searchbar(d, x, y, pw, t, "mian")
    d.line([x, y + 68, x + pw, y + 68], fill=t["line"])
    results(d, x, y + 74, pw, t, GROUPS, sel=0)
    footer(d, x, y + ph - 44, pw, t, "Did you mean main?  ·  Enter open   Ctrl+Enter reveal   Alt+Space toggle")
    bg.convert("RGB").save(os.path.join(OUT, name))

def sample2():
    t = theme(True)
    bg = wallpaper().convert("RGBA")
    pw, x, y, ph = 1100, (W - 1100) // 2, 130, 600
    d = panel(bg, x, y, pw, ph, t)
    searchbar(d, x, y, pw, t, "report", chips=("ext:pdf", "mtime:<7d"))
    d.line([x, y + 68, x + pw, y + 68], fill=t["line"])
    lw = 470
    rows = [("Documents", [("pdf", "DBMS-lab-report.pdf", "~\\Documents\\MEC\\S7", "2.4 MB"),
                           ("pdf", "placement-report-oct.pdf", "~\\Downloads", "880 KB"),
                           ("pdf", "report-final-v3.pdf", "~\\OneDrive\\Project", "5.1 MB"),
                           ("pdf", "expense-report.pdf", "~\\Downloads", "120 KB")])]
    results(d, x, y + 74, lw, t, rows, sel=0)
    d.line([x + lw, y + 69, x + lw, y + ph - 45], fill=t["line"])
    # preview
    px0 = x + lw + 30
    d.rounded_rectangle([px0 + 120, y + 100, px0 + 470, y + 340], 8, fill=(250, 250, 250))
    d.rectangle([px0 + 120, y + 100, px0 + 470, y + 130], fill=(220, 60, 60))
    d.text((px0 + 135, y + 106), "DBMS Lab Report", font=font(15, "SemiBold"), fill="white")
    for k in range(8):
        ww = 300 - (k * 37) % 120
        d.rounded_rectangle([px0 + 140, y + 150 + k * 22, px0 + 140 + ww, y + 160 + k * 22], 3, fill=(205, 205, 212))
    d.text((px0, y + 365), "DBMS-lab-report.pdf", font=font(22, "SemiBold"), fill=t["text"])
    meta = [("Where", "C:\\Users\\Ben\\Documents\\MEC\\S7"), ("Size", "2.4 MB  ·  12 pages"),
            ("Modified", "Yesterday, 9:42 PM"), ("Created", "1 October 2026")]
    for k, (a, b) in enumerate(meta):
        d.text((px0, y + 410 + k * 28), a, font=font(14), fill=t["sub"])
        d.text((px0 + 100, y + 410 + k * 28), b, font=font(14), fill=t["text"])
    footer(d, x, y + ph - 44, pw, t, "4 results in 0.9 ms  ·  Space preview   Ctrl+C copy path   Drag a row to drop the file anywhere")
    bg.convert("RGB").save(os.path.join(OUT, "2-preview-filters.png"))

def sample3():
    t = theme(False)
    bg = wallpaper().convert("RGBA")
    bg = Image.blend(bg, Image.new("RGBA", bg.size, (235, 238, 245, 255)), 0.45)
    pw, x, y, ph = 820, (W - 820) // 2, 150, 560
    d = panel(bg, x, y, pw, ph, t)
    searchbar(d, x, y, pw, t, "apply_dir", chips=("grep:",))
    d.line([x, y + 68, x + pw, y + 68], fill=t["line"])
    hits = [("rs", "index.rs", 212, "    fn apply_dir(&mut self, path: &Path) {"),
            ("rs", "index.rs", 348, "        self.apply_dir(&parent)?;"),
            ("rs", "watcher.rs", 77, "    // replay: apply_dir for every changed folder"),
            ("md", "README.md", 14, "fsearch 'ext:rs grep:apply_dir'")]
    yy = y + 82
    d.text((x + 24, yy), "INSIDE FILES", font=font(12, "SemiBold"), fill=t["head"]); yy += 28
    for k, (kind, fn, ln, code) in enumerate(hits):
        if k == 0:
            d.rounded_rectangle([x + 10, yy, x + pw - 10, yy + 70], 10, fill=(208, 224, 242))
        icon(d, x + 22, yy + 10, kind)
        d.text((x + 66, yy + 6), fn, font=font(16, "SemiBold"), fill=t["text"])
        d.text((x + 66 + d.textlength(fn, font=font(16, "SemiBold")) + 8, yy + 8), f"line {ln}", font=font(13), fill=t["sub"])
        mono = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf", 14)
        pre, _, post = code.partition("apply_dir")
        cx = x + 66
        d.text((cx, yy + 36), pre, font=mono, fill=t["sub"]); cx += d.textlength(pre, font=mono)
        aw = d.textlength("apply_dir", font=mono)
        d.rounded_rectangle([cx - 2, yy + 34, cx + aw + 2, yy + 54], 4, fill=(255, 214, 102))
        d.text((cx, yy + 36), "apply_dir", font=mono, fill=t["text"]); cx += aw
        d.text((cx, yy + 36), post, font=mono, fill=t["sub"])
        yy += 82
    footer(d, x, y + ph - 44, pw, t, "4 matches in 3 files  ·  9 ms  ·  Enter opens at the line in VS Code")
    bg.convert("RGB").save(os.path.join(OUT, "3-inside-files-light.png"))

def ease_drop(p):
    """Spring: drops past its spot a little, then settles."""
    if p >= 1: return 1.0
    return 1 - math.exp(-6 * p) * math.cos(9 * p)

def drop_in_gif():
    t = theme(True)
    base = wallpaper().convert("RGBA")
    pw, ph = 760, 520
    x, y_final = (W - pw) // 2, 150
    frames = []
    S = 0.6  # downscale for gif size
    n_in = 16
    typed = ["", "", "m", "mi", "mia", "mian", "mian", "mian", "mian", "mian"]
    for f in range(n_in + len(typed) * 2 + 10):
        bg = base.copy()
        if f < n_in:
            p = f / (n_in - 1)
            e = ease_drop(p)
            yy = int(y_final - 120 + 120 * e)
            alpha = min(1.0, p * 2.2)
            # dim backdrop
            bg.alpha_composite(Image.new("RGBA", bg.size, (0, 0, 0, int(70 * alpha))))
            layer = base.copy()
            layer.alpha_composite(Image.new("RGBA", bg.size, (0, 0, 0, int(70 * alpha))))
            d = panel(layer, x, yy, pw, 72, t)
            searchbar(d, x, yy, pw, t, "", caret=False)
            # scale-in feel: compose with alpha
            bg = Image.blend(bg, layer, alpha)
        else:
            k = (f - n_in) // 2
            q = typed[min(k, len(typed) - 1)]
            bg.alpha_composite(Image.new("RGBA", bg.size, (0, 0, 0, 70)))
            if q:
                d = panel(bg, x, y_final, pw, ph, t)
                searchbar(d, x, y_final, pw, t, q, caret=(f // 3) % 2 == 0)
                d.line([x, y_final + 68, x + pw, y_final + 68], fill=t["line"])
                results(d, x, y_final + 74, pw, t, GROUPS, sel=0)
                footer(d, x, y_final + ph - 44, pw, t, "Did you mean main?  ·  Enter open   Ctrl+Enter reveal")
            else:
                d = panel(bg, x, y_final, pw, 72, t)
                searchbar(d, x, y_final, pw, t, "", caret=(f // 3) % 2 == 0)
        frames.append(bg.convert("RGB").resize((int(W * S), int(H * S)), Image.LANCZOS))
    import subprocess, tempfile
    tmp = tempfile.mkdtemp(); k = 0
    for i, fr in enumerate(frames):
        reps = 1 if i < n_in else (3 if i < len(frames) - 1 else 45)
        for _ in range(reps):
            fr.save(f"{tmp}/f{k:04d}.png"); k += 1
    subprocess.run(["ffmpeg", "-y", "-v", "error", "-framerate", "30", "-i", f"{tmp}/f%04d.png", "-c:v", "libx264",
                    "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p", "-movflags", "+faststart",
                    os.path.join(OUT, "4-drop-in.mp4")], check=True)
    frames[0].save(os.path.join(OUT, "4-drop-in.gif"), save_all=True, append_images=frames[1:],
                   duration=[33] * n_in + [90] * (len(frames) - n_in - 1) + [1500], loop=0, optimize=True)

if __name__ == "__main__":
    sample1(True)
    sample2()
    sample3()
    drop_in_gif()
    print("done")

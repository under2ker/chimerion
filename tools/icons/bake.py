"""
Анимированные иконки useanimations.com (MIT) для CHIMERION.

Скачивает Lottie-анимации нужных иконок и запекает их в кадры: каждый кадр — набор контуров (ломаных) в поле 32x32
с обводкой или заливкой. Программа рисует эти контуры цветом текста, поэтому иконки перекрашиваются вместе с темой,
а тяжелый Lottie-плеер не нужен. Поддерживается то, что встречается в иконках useanimations: группы, контуры,
обводка, заливка, обрезка контура (прорисовка), трансформации слоев и групп с ключевыми кадрами, вложенные композиции.

Запуск: python tools/icons/bake.py  →  Assets/animated-icons.json.gz
"""
import gzip, json, math, os, sys, urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, '..', '..'))
CACHE = os.path.join(HERE, 'cache')
OUT = os.path.join(ROOT, 'Assets', 'animated-icons.json.gz')

# Иконка программы → иконка useanimations и какой кадр показывать в покое (начало или конец анимации)
MAP = {
    "settings": ("settings", "start"), "zap": ("zap", "start"), "stethoscope": ("activity", "start"),
    "eye-off": ("visibility", "start"), "info": ("info", "start"), "triangle-alert": ("alert-triangle", "start"),
    "circle-check": ("check-circle", "end"), "circle-x": ("x-octagon", "end"), "download": ("download", "start"),
    "external-link": ("external-link", "start"), "x": ("x-square", "end"),
    "chevron-down": ("chevron-down", "start"), "refresh-cw": ("refresh", "start"), "check": ("check", "end"),
    "plus": ("plus-circle", "end"), "scroll-text": ("file-text", "start"), "sliders-horizontal": ("filter", "start"),
    "shrink": ("minimize-2", "start"), "expand": ("maximize-2", "start"), "send": ("send", "start"),
    "activity": ("activity", "start"), "shield-check": ("shield", "start"), "globe": ("globe", "start"),
    "copy": ("copy", "start"), "search": ("zoom-in", "start"), "link": ("link", "start"), "minus": ("minus", "end"),
    "square": ("square", "start"), "circle-arrow-up": ("arrow-up-circle", "start"), "smartphone": ("smartphone", "start"),
    "circle-help": ("help", "start"), "arrow-left-right": ("repeat", "start"),
}

SEGMENTS = 10      # отрезков на кривую Безье при разбиении в ломаную
ROUND = 2          # знаков после запятой в координатах


def load(name):
    os.makedirs(CACHE, exist_ok=True)
    path = os.path.join(CACHE, name + '.json')
    if not os.path.exists(path):
        urllib.request.urlretrieve(f'https://useanimations.com/icons/{name}.json', path)
    with open(path, encoding='utf-8') as f:
        return json.load(f)


# ---------- Значения свойств во времени ----------

def bezier_ease(x1, y1, x2, y2, t):
    """Кубическая кривая плавности (как CSS cubic-bezier): по доле времени t доля значения."""
    if t <= 0: return 0.0
    if t >= 1: return 1.0
    lo, hi = 0.0, 1.0
    for _ in range(40):
        mid = (lo + hi) / 2
        x = 3 * (1 - mid) ** 2 * mid * x1 + 3 * (1 - mid) * mid ** 2 * x2 + mid ** 3
        if x < t: lo = mid
        else: hi = mid
    u = (lo + hi) / 2
    return 3 * (1 - u) ** 2 * u * y1 + 3 * (1 - u) * u ** 2 * y2 + u ** 3


def first(v):
    return v[0] if isinstance(v, list) else v


def lerp(a, b, k):
    if isinstance(a, dict):  # контур
        return {'c': a.get('c', False),
                'v': [lerp(p, q, k) for p, q in zip(a['v'], b['v'])],
                'i': [lerp(p, q, k) for p, q in zip(a['i'], b['i'])],
                'o': [lerp(p, q, k) for p, q in zip(a['o'], b['o'])]}
    if isinstance(a, list):
        return [lerp(p, q, k) for p, q in zip(a, b)]
    return a + (b - a) * k


def value(prop, t, default=None):
    if prop is None: return default
    k = prop.get('k')
    if prop.get('a') != 1 and not (isinstance(k, list) and k and isinstance(k[0], dict) and 't' in k[0]):
        return k
    kfs = k
    if t <= kfs[0]['t']:
        return unwrap(kfs[0]['s'])
    for n, kf in enumerate(kfs):
        nxt = kfs[n + 1] if n + 1 < len(kfs) else None
        if nxt is None:
            return unwrap(kf.get('s', kf.get('e')))
        if kf['t'] <= t < nxt['t']:
            start = unwrap(kf['s'])
            end = unwrap(kf['e']) if 'e' in kf else unwrap(nxt.get('s', kf.get('s')))
            if kf.get('h') == 1: return start
            span = nxt['t'] - kf['t']
            lin = (t - kf['t']) / span if span else 1
            o, i = kf.get('o', {'x': 0, 'y': 0}), kf.get('i', {'x': 1, 'y': 1})
            eased = bezier_ease(first(o['x']), first(o['y']), first(i['x']), first(i['y']), lin)
            return lerp(start, end, eased)
    return unwrap(kfs[-1]['s'])


def unwrap(v):
    # Контур в ключевом кадре лежит в списке из одного элемента
    if isinstance(v, list) and len(v) == 1 and isinstance(v[0], dict): return v[0]
    return v


def num(v, default=0.0):
    if v is None: return default
    return float(v[0]) if isinstance(v, list) else float(v)


# ---------- Матрицы 2D: (a, b, c, d, e, f) как в SVG ----------

def mul(m, n):
    a, b, c, d, e, f = m
    a2, b2, c2, d2, e2, f2 = n
    return (a * a2 + c * b2, b * a2 + d * b2, a * c2 + c * d2, b * c2 + d * d2, a * e2 + c * f2 + e, b * e2 + d * f2 + f)


def transform(tr, t):
    """Матрица слоя или группы: перенос(p) · поворот(r) · масштаб(s) · перенос(−a)."""
    a = value(tr.get('a'), t, [0, 0]) or [0, 0]
    p = value(tr.get('p'), t, [0, 0]) or [0, 0]
    if 'x' in (tr.get('p') or {}) or tr.get('p', {}).get('s'):
        p = [num(value(tr['p'].get('x'), t)), num(value(tr['p'].get('y'), t))]
    s = value(tr.get('s'), t, [100, 100]) or [100, 100]
    r = num(value(tr.get('r'), t, 0))
    rad = math.radians(r)
    m = (1, 0, 0, 1, p[0], p[1])
    m = mul(m, (math.cos(rad), math.sin(rad), -math.sin(rad), math.cos(rad), 0, 0))
    m = mul(m, (s[0] / 100, 0, 0, s[1] / 100, 0, 0))
    m = mul(m, (1, 0, 0, 1, -a[0], -a[1]))
    op = num(value(tr.get('o'), t, 100), 100) / 100
    return m, op


def apply(m, x, y):
    a, b, c, d, e, f = m
    return (a * x + c * y + e, b * x + d * y + f)


def scale_of(m):
    a, b, c, d, _, _ = m
    return math.sqrt(abs(a * d - b * c)) or 1


# ---------- Контуры: разбиение в ломаные и обрезка ----------

def flatten(path, m):
    v, ii, oo, closed = path['v'], path['i'], path['o'], path.get('c', False)
    if not v: return None
    pts = [apply(m, *v[0])]
    count = len(v) if closed else len(v) - 1
    for n in range(count):
        p0, p3 = v[n], v[(n + 1) % len(v)]
        p1 = (p0[0] + oo[n][0], p0[1] + oo[n][1])
        p2 = (p3[0] + ii[(n + 1) % len(v)][0], p3[1] + ii[(n + 1) % len(v)][1])
        straight = oo[n] == [0, 0] and ii[(n + 1) % len(v)] == [0, 0]
        steps = 1 if straight else SEGMENTS
        for k in range(1, steps + 1):
            t = k / steps
            x = (1 - t) ** 3 * p0[0] + 3 * (1 - t) ** 2 * t * p1[0] + 3 * (1 - t) * t ** 2 * p2[0] + t ** 3 * p3[0]
            y = (1 - t) ** 3 * p0[1] + 3 * (1 - t) ** 2 * t * p1[1] + 3 * (1 - t) * t ** 2 * p2[1] + t ** 3 * p3[1]
            pts.append(apply(m, x, y))
    return {'pts': pts, 'closed': closed}


def length(pts):
    return sum(math.dist(pts[n], pts[n + 1]) for n in range(len(pts) - 1))


def cut(pts, a, b):
    """Часть ломаной от длины a до длины b."""
    out, acc = [], 0.0
    for n in range(len(pts) - 1):
        p, q = pts[n], pts[n + 1]
        seg = math.dist(p, q)
        if seg == 0: continue
        s0, s1 = acc, acc + seg
        if s1 >= a and s0 <= b:
            t0 = max(0.0, (a - s0) / seg)
            t1 = min(1.0, (b - s0) / seg)
            pa = (p[0] + (q[0] - p[0]) * t0, p[1] + (q[1] - p[1]) * t0)
            pb = (p[0] + (q[0] - p[0]) * t1, p[1] + (q[1] - p[1]) * t1)
            if not out: out.append(pa)
            elif math.dist(out[-1], pa) > 1e-6: out.append(pa)
            out.append(pb)
        acc = s1
    return out


def trim_one(pts, s, e, off):
    total = length(pts)
    if total == 0: return []
    s, e = (s + off) * total, (e + off) * total
    if s > e: s, e = e, s
    pieces = []
    # Смещение может вывести за конец: часть с начала контура
    for lo, hi in ((s, e),):
        if hi <= total:
            pieces.append(cut(pts, lo, hi))
        else:
            pieces.append(cut(pts, lo, total))
            pieces.append(cut(pts, 0, hi - total))
    return [p for p in pieces if len(p) > 1]


def trim(paths, tm, t):
    s = num(value(tm.get('s'), t, 0)) / 100
    e = num(value(tm.get('e'), t, 100)) / 100
    off = num(value(tm.get('o'), t, 0)) / 360
    off -= math.floor(off)
    if abs(e - s) >= 0.9999 and off == 0: return paths
    out = []
    mode = tm.get('m', 1)
    prepared = []
    for p in paths:
        pts = p['pts'] + ([p['pts'][0]] if p['closed'] else [])
        prepared.append(pts)
    if mode == 1 and len(prepared) > 1:
        # Одновременно: все контуры как одна линия
        lens = [length(pts) for pts in prepared]
        total = sum(lens)
        if total == 0: return []
        a, b = sorted(((s + off) * total, (e + off) * total))
        ranges = [(a, min(b, total))] + ([(0, b - total)] if b > total else [])
        acc = 0
        for pts, ln in zip(prepared, lens):
            for lo, hi in ranges:
                l0, l1 = max(lo, acc), min(hi, acc + ln)
                if l1 > l0:
                    piece = cut(pts, l0 - acc, l1 - acc)
                    if len(piece) > 1: out.append({'pts': piece, 'closed': False})
            acc += ln
        return out
    for pts in prepared:
        for piece in trim_one(pts, s, e, off):
            out.append({'pts': piece, 'closed': False})
    return out


# ---------- Отрисовка кадра ----------

def shapes(items, m, op, t, drawn, clips=()):
    """Контуры группы без собственного стиля возвращаются наверх; обводка и заливка рисуют контуры над собой."""
    tr = next((it for it in items if it.get('ty') == 'tr'), None)
    if tr is not None:
        gm, gop = transform(tr, t)
        m = mul(m, gm)
        op *= gop
    paths = []
    for it in items:
        ty = it.get('ty')
        if it.get('hd'): continue
        if ty == 'sh':
            p = flatten(value(it['ks'], t), m)
            if p: paths.append(p)
        elif ty == 'el':
            sz = value(it.get('s'), t, [0, 0]); c = value(it.get('p'), t, [0, 0])
            rx, ry = sz[0] / 2, sz[1] / 2
            pts = [apply(m, c[0] + rx * math.cos(2 * math.pi * k / 48), c[1] + ry * math.sin(2 * math.pi * k / 48)) for k in range(48)]
            paths.append({'pts': pts, 'closed': True})
        elif ty == 'rc':
            sz = value(it.get('s'), t, [0, 0]); c = value(it.get('p'), t, [0, 0])
            x0, y0, x1, y1 = c[0] - sz[0] / 2, c[1] - sz[1] / 2, c[0] + sz[0] / 2, c[1] + sz[1] / 2
            paths.append({'pts': [apply(m, x0, y0), apply(m, x1, y0), apply(m, x1, y1), apply(m, x0, y1)], 'closed': True})
        elif ty == 'gr':
            paths += shapes(it.get('it', []), m, op, t, drawn, clips)
        elif ty == 'tm':
            paths = trim(paths, it, t)
        elif ty in ('st', 'fl'):
            alpha = op * num(value(it.get('o'), t, 100), 100) / 100
            if alpha <= 0.01 or not paths: continue
            width = num(value(it.get('w'), t, 2)) * scale_of(m) if ty == 'st' else 0
            if ty == 'st' and width <= 0.01: continue
            drawn.append({'kind': 's' if ty == 'st' else 'f', 'w': width, 'o': alpha,
                          'paths': [dict(p) for p in paths], 'clips': list(clips)})
    return paths


def layers(layer_list, assets, m, op, t, drawn, ip_offset=0, clips=()):
    by_index = {L.get('ind'): L for L in layer_list}
    # Нижние слои рисуются первыми: в Lottie первый слой списка — верхний
    for L in reversed(layer_list):
        if L.get('hd'): continue
        lt = t - ip_offset
        if lt < L.get('ip', -1e9) or lt >= L.get('op', 1e9): continue
        lm, lop = transform(L.get('ks', {}), lt)
        parent = L.get('parent')
        while parent is not None and parent in by_index:
            pm, _ = transform(by_index[parent].get('ks', {}), lt)
            lm = mul(pm, lm)
            parent = by_index[parent].get('parent')
        full = mul(m, lm)
        # Маски слоя (сложение и пересечение): рисунок слоя обрезается по их контурам. Вложенные обрезки копятся
        layer_clips = list(clips)
        for mask in L.get('masksProperties', []) if L.get('hasMask') else []:
            if mask.get('mode', 'a') not in ('a', 'i') or mask.get('inv'): continue
            mp = flatten(value(mask['pt'], lt), full)
            if mp: layer_clips.append(svg_path({'pts': mp['pts'], 'closed': True}))
        if L.get('ty') == 4:
            shapes(L.get('shapes', []), full, op * lop, lt, drawn, layer_clips)
        elif L.get('ty') == 0:
            ref = next((a for a in assets if a.get('id') == L.get('refId')), None)
            if ref: layers(ref.get('layers', []), assets, full, op * lop, lt - L.get('st', 0), drawn, clips=layer_clips)


def svg_path(p):
    pts = p['pts']
    r = lambda v: f'{round(v, ROUND):g}'
    d = 'M' + r(pts[0][0]) + ',' + r(pts[0][1]) + ''.join('L' + r(x) + ',' + r(y) for x, y in pts[1:])
    return d + ('Z' if p['closed'] else '')


def bake(name):
    d = load(name)
    frames = []
    for t in range(int(math.floor(d['ip'])), int(math.ceil(d['op']))):
        drawn = []
        layers(d['layers'], d.get('assets', []), (1, 0, 0, 1, 0, 0), 1.0, t, drawn)
        frames.append([[x['kind'], round(x['w'], 2), round(x['o'], 2), ''.join(svg_path(p) for p in x['paths'] if len(p['pts']) > 1)]
                       + ([x['clips']] if x['clips'] else []) for x in drawn])
    return {'fps': d['fr'], 'size': d['w'], 'frames': frames}


def main():
    out = {}
    cache = {}
    for ours, (ua, rest) in MAP.items():
        if ua not in cache: cache[ua] = bake(ua)
        anim = dict(cache[ua])
        anim['rest'] = rest
        anim['source'] = ua
        out[ours] = anim
        print(f'{ours:20} ← {ua:16} кадров {len(anim["frames"])}')
    data = json.dumps({'source': 'https://useanimations.com (MIT)', 'icons': out}, ensure_ascii=False, separators=(',', ':')).encode('utf-8')
    with gzip.open(OUT, 'wb', compresslevel=9) as f:
        f.write(data)
    print(f'{OUT}: {len(data) // 1024} КБ, сжато {os.path.getsize(OUT) // 1024} КБ')


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    main()

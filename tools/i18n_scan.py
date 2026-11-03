#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""从 app.js 提取所有含中文的字符串字面量（供翻译），并支持回填 TT() 包装。

  py i18n_scan.py extract            -> tools/strings.txt
  py i18n_scan.py apply dict.json    -> static/app.js.new

规则：
  * 正确处理 // /* */ 注释、正则字面量、嵌套模板串
  * 模板串里的 ${expr} 变成 {vN} 占位（N 从 0 起），回填时还原成 {vN: expr}
  * 词典 key 就是占位化后的文本；不含中文的字面量不碰
"""
import html
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.normpath(os.path.join(HERE, '..', 'static', 'app.js'))

CJK = re.compile(r'[\u4e00-\u9fff]')
REGEX_PREV = set('(,=:[!&|?{};+-*%^~<>')


def is_regex_start(src, i):
    j = i - 1
    while j >= 0 and src[j] in ' \t\r\n':
        j -= 1
    if j < 0:
        return True
    if src[j] in REGEX_PREV:
        return True
    tail = src[max(0, j - 8):j + 1]
    return bool(re.search(r'\b(return|typeof|case|in|of|delete|void|new)$', tail))


def skip_regex(src, i):
    i += 1
    n = len(src)
    in_cls = False
    while i < n:
        c = src[i]
        if c == '\\':
            i += 2
            continue
        if c == '[':
            in_cls = True
        elif c == ']':
            in_cls = False
        elif c == '/' and not in_cls:
            i += 1
            break
        elif c == '\n':
            break
        i += 1
    while i < n and src[i].isalpha():
        i += 1
    return i


def tokenize(src):
    """-> [(kind, start, end, body, exprs)]"""
    toks = []
    frames = []
    i, n = 0, len(src)
    while i < n:
        if frames and not frames[-1]['active']:
            f = frames[-1]
            c = src[i]
            if c == '\\':
                f['buf'].append(src[i:i + 2])
                i += 2
                continue
            if c == '`':
                i += 1
                body = ''.join(f['buf'])
                toks.append(('tpl', f['start'], i, body, list(f['exprs'])))
                frames.pop()
                if frames and frames[-1]['active']:
                    frames[-1]['cur'].append(src[f['start']:i])
                continue
            if c == '$' and i + 1 < n and src[i + 1] == '{':
                f['buf'].append('{v%d}' % len(f['exprs']))
                f['cur'] = []
                f['depth'] = 1
                f['active'] = True
                i += 2
                continue
            f['buf'].append(c)
            i += 1
            continue

        c = src[i]
        if c == '/' and i + 1 < n and src[i + 1] == '/':
            j = src.find('\n', i)
            i = n if j < 0 else j + 1
            continue
        if c == '/' and i + 1 < n and src[i + 1] == '*':
            j = src.find('*/', i + 2)
            i = n if j < 0 else j + 2
            continue
        if c == '/' and is_regex_start(src, i):
            i = skip_regex(src, i)
            continue
        if c in '\'"':
            q, start = c, i
            i += 1
            while i < n:
                if src[i] == '\\':
                    i += 2
                    continue
                if src[i] == q:
                    i += 1
                    break
                i += 1
            toks.append(('str', start, i, src[start + 1:i - 1], []))
            if frames and frames[-1]['active']:
                frames[-1]['cur'].append(src[start:i])
            continue
        if c == '`':
            frames.append({'start': i, 'buf': [], 'exprs': [], 'active': False})
            i += 1
            continue
        if frames and frames[-1]['active']:
            f = frames[-1]
            if c == '{':
                f['depth'] += 1
            elif c == '}':
                f['depth'] -= 1
                if f['depth'] == 0:
                    f['exprs'].append(''.join(f['cur']))
                    f['active'] = False
                    i += 1
                    continue
            f['cur'].append(c)
            i += 1
            continue
        i += 1
    return toks


def js_quote(text):
    for q in ("'", '"', '`'):
        if q not in text:
            return q + text + q
    return json.dumps(text, ensure_ascii=False)


def escape_for_line(t):
    return (t.replace('\\', '\\\\').replace('\n', '\\n').replace('\t', '\\t'))


def fragments(body, is_tpl):
    """把一段字面量拆成「最终会出现在 DOM 里的文本片段」。
    含 HTML 的拆成文本节点 + title/placeholder/alt 属性值；否则整体就是一段文本。
    模板占位 {vN} 统一变成 {} 通配。"""
    out = []

    def norm(t):
        t = html.unescape(t)
        t = t.replace('\\n', '\n').replace('\\t', '\t').replace("\\'", "'").replace('\\\\', '\\')
        t = re.sub(r'\s+', ' ', t).strip()
        t = re.sub(r'\{v\d+\}', '{}', t)
        return t

    if '<' in body and '>' in body:
        for m in re.finditer(r'>([^<>]*)<', body):
            t = norm(m.group(1))
            if t and CJK.search(t):
                out.append(t)
        for m in re.finditer(r'\b(?:title|placeholder|alt)="([^"]*)"', body):
            t = norm(m.group(1))
            if t and CJK.search(t):
                out.append(t)
    else:
        t = norm(body)
        if t and CJK.search(t):
            out.append(t)
    return out


def extract():
    src = open(SRC, encoding='utf-8').read()
    order, count = [], {}
    for kind, s, e, body, exprs in tokenize(src):
        if not CJK.search(body):
            continue
        for frag in fragments(body, kind == 'tpl'):
            if frag not in count:
                count[frag] = 0
                order.append(frag)
            count[frag] += 1
    order.sort(key=lambda x: (-count[x], x))
    with open(os.path.join(HERE, 'strings.txt'), 'w', encoding='utf-8') as fh:
        for idx, frag in enumerate(order, 1):
            fh.write('%03d\t%d\t%s\n' % (idx, count[frag], escape_for_line(frag)))
    with open(os.path.join(HERE, 'fragments.json'), 'w', encoding='utf-8') as fh:
        json.dump(order, fh, ensure_ascii=False, indent=0)
    print('unique fragments=%d' % len(order))


def apply(dict_path):
    src = open(SRC, encoding='utf-8').read()
    dic = json.load(open(dict_path, encoding='utf-8'))
    edits, miss = [], []
    for kind, s, e, body, exprs in tokenize(src):
        if not CJK.search(body):
            continue
        if body not in dic:
            miss.append(body)
            continue
        if exprs:
            vs = ', '.join('v%d: %s' % (i, x.strip()) for i, x in enumerate(exprs))
            code = 'TT(%s, {%s})' % (js_quote(body), vs)
        else:
            code = 'TT(%s)' % js_quote(body)
        edits.append((s, e, code))
    out = src
    for s, e, code in sorted(edits, reverse=True):
        out = out[:s] + code + out[e:]
    open(SRC + '.new', 'w', encoding='utf-8').write(out)
    print('replaced=%d missed=%d' % (len(edits), len(set(miss))))
    if miss:
        with open(os.path.join(HERE, 'missed.txt'), 'w', encoding='utf-8') as fh:
            for m in sorted(set(miss)):
                fh.write(escape_for_line(m) + '\n')
        for m in sorted(set(miss))[:50]:
            print('  miss:', escape_for_line(m)[:90])


if __name__ == '__main__':
    cmd = sys.argv[1] if len(sys.argv) > 1 else 'extract'
    if cmd == 'extract':
        extract()
    elif cmd == 'apply':
        apply(sys.argv[2])

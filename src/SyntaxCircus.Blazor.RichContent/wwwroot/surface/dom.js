// Maps between a writing surface's DOM and the wire nodes (SurfaceNode.cs). Elements are made with DOM APIs only,
// and text only ever becomes text nodes.
//
// A wire node is { kind, text?, url?, level?, ordered?, children? }. Absent children differ from [] and absent
// ordered differs from false, in the stored document too, so the DOM remembers both in data attributes.

const KEY = 'data-key';
// On an element built from a node with nothing in it: how that node stored its children.
const EMPTY = 'data-rt-empty';
// On a list built from a node that stores no ordering.
const ORDERED = 'data-rt-ordered';
// On a link: its address exactly as stored. Only a safe address is also put in href.
const URL_ATTRIBUTE = 'data-rt-url';
// On the paragraph an empty surface offers to type into. While it is empty and alone, nothing is stored for it.
const PLACEHOLDER = 'data-rt-placeholder';
// On a list item that stores one paragraph, shown without its <p>: how that paragraph stored its children. Browsers
// make a mess of nesting items that hold paragraphs, so they are shown like items that do not.
const PARAGRAPH = 'data-rt-paragraph';

// Inline code is the browser's font command with this face, since no command makes <code>, and a <code> inserted as
// markup is dropped in places, or cannot be taken away again.
export const CODE_FACE = 'rt-code';
export const CODE_SELECTOR = `code, font[face="${CODE_FACE}"]`;

const NBSP = '\u00a0';
const NBSP_CONTEXT = 8;

const MODEL_BLOCKS = new Set(['P', 'H2', 'H3', 'H4', 'UL', 'OL', 'LI', 'BLOCKQUOTE']);

// Unwrapped like any other element the model does not have, but their edges still separate paragraphs.
const OTHER_BLOCKS = new Set([
    'ADDRESS', 'ARTICLE', 'ASIDE', 'CAPTION', 'CENTER', 'DD', 'DETAILS', 'DIR', 'DIV', 'DL', 'DT', 'FIELDSET',
    'FIGCAPTION', 'FIGURE', 'FOOTER', 'FORM', 'H1', 'H5', 'H6', 'HEADER', 'HR', 'MAIN', 'MENU', 'NAV', 'PRE',
    'SECTION', 'SUMMARY', 'TABLE', 'TBODY', 'TD', 'TFOOT', 'TH', 'THEAD', 'TR',
]);

const BLOCK_SELECTOR = [...MODEL_BLOCKS, ...OTHER_BLOCKS].join(',').toLowerCase();

// Their text is not content.
const IGNORED = new Set(['SCRIPT', 'STYLE', 'TEMPLATE', 'NOSCRIPT', 'HEAD', 'META', 'LINK', 'TITLE']);

// Keys .NET issues start with "s". These must differ, and are never reused while the page lives.
let lastKey = 0;
const newKey = () => `c${++lastKey}`;

const tagOf = element => element.tagName.toUpperCase();

// ---- Building ----

// Deliberately stricter than RichContentUrl.IsSafe. It only decides whether an address may be a live href.
export function mayBeHref(url, maximumLength = 2048) {
    if (url.length === 0 || url.length > maximumLength || /[\s\u0000-\u001f\u007f-\u009f\\<>"']/.test(url)) {
        return false;
    }

    return url[0] === '/' ? url[1] !== '/' : /^(https:\/\/[^/?#@]+([/?#]|$)|mailto:|tel:)/i.test(url);
}

function tagFor(node) {
    switch (node.kind) {
        case 'paragraph': return 'p';
        case 'heading': return `h${[2, 3, 4].includes(node.level) ? node.level : 2}`;
        case 'strong': return 'strong';
        case 'emphasis': return 'em';
        case 'inlineCode': return 'font';
        case 'link': return 'a';
        case 'list': return node.ordered === true ? 'ol' : 'ul';
        case 'listItem': return 'li';
        case 'quote': return 'blockquote';
        default: throw new Error('A writing surface cannot show this kind of node.');
    }
}

// An empty block needs a <br> or the caret cannot enter it. The reader ignores it.
const NEEDS_FILLER = new Set(['paragraph', 'heading', 'listItem', 'quote']);

export function buildNode(node) {
    if (node.kind === 'text') {
        return document.createTextNode(node.text ?? '');
    }

    const element = document.createElement(tagFor(node));
    if (node.kind === 'inlineCode') {
        element.setAttribute('face', CODE_FACE);
        element.textContent = node.text ?? '';
        return element;
    }

    if (node.kind === 'link') {
        const url = node.url ?? '';
        element.setAttribute(URL_ATTRIBUTE, url);
        if (mayBeHref(url)) {
            element.setAttribute('href', url);
        }
    }

    if (node.kind === 'list' && (node.ordered === null || node.ordered === undefined)) {
        element.setAttribute(ORDERED, 'none');
    }

    let { children } = node;
    const paragraphs = node.kind === 'listItem' ? (children ?? []).filter(child => child.kind === 'paragraph') : [];
    if (paragraphs.length === 1 && children[0] === paragraphs[0]) {
        const own = paragraphs[0].children;
        element.setAttribute(PARAGRAPH, !own ? 'absent' : own.length === 0 ? 'empty' : '');
        children = [...(own ?? []), ...children.slice(1)];
    } else if (!children || children.length === 0) {
        element.setAttribute(EMPTY, children ? 'empty' : 'absent');
    }

    for (const child of children ?? []) {
        element.appendChild(buildNode(child));
    }

    if (NEEDS_FILLER.has(node.kind) && element.textContent === '' && !element.querySelector('ul,ol')) {
        element.appendChild(document.createElement('br'));
    }

    return element;
}

// Replaces the surface's content with stored nodes. keyedNodes is [{ key, node }].
export function buildSurface(root, keyedNodes, inline) {
    const built = keyedNodes.map(({ key, node }) => {
        const dom = buildNode(node);
        if (!inline) {
            dom.setAttribute(KEY, key);
        }

        return dom;
    });

    if (!inline && built.length === 0) {
        const placeholder = document.createElement('p');
        placeholder.setAttribute(PLACEHOLDER, '');
        placeholder.appendChild(document.createElement('br'));
        built.push(placeholder);
    }

    const content = document.createDocumentFragment();
    content.append(...built);
    root.replaceChildren(content);
}

// ---- Non-breaking spaces ----

// Browsers can turn an ordinary space the author typed into U+00A0. Those are read back as spaces, while the ones
// in the stored text stay. They are told apart by position in all of the surface's text, which splitting, joining
// and formatting do not change: a U+00A0 outside what an edit replaced keeps what it was. One inside is the
// author's only if the text around it is what surrounded a stored one, as when a deletion is undone.
function createSpaces(root) {
    let text = '';
    let stored = new Set();
    const contexts = new Set();

    const contextAt = (value, index) =>
        value.slice(Math.max(0, index - NBSP_CONTEXT), index).replaceAll(NBSP, ' ')
        + '\n' + value.slice(index + 1, index + 1 + NBSP_CONTEXT).replaceAll(NBSP, ' ');

    const positions = value => {
        const found = [];
        for (let index = value.indexOf(NBSP); index >= 0; index = value.indexOf(NBSP, index + 1)) {
            found.push(index);
        }

        return found;
    };

    const collect = () => {
        const offsets = new Map();
        const parts = [];
        let length = 0;
        const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
        for (let node = walker.nextNode(); node; node = walker.nextNode()) {
            offsets.set(node, length);
            parts.push(node.data);
            length += node.data.length;
        }

        return { value: parts.join(''), offsets };
    };

    return {
        // The surface was just built from stored nodes, so every U+00A0 in it is stored.
        adopt() {
            text = collect().value;
            stored = new Set(positions(text));
            contexts.clear();
            for (const index of stored) {
                contexts.add(contextAt(text, index));
            }
        },

        // Takes in the current text and returns how to read a text node of it.
        read() {
            const { value, offsets } = collect();
            const shortest = Math.min(value.length, text.length);
            let prefix = 0;
            while (prefix < shortest && value[prefix] === text[prefix]) {
                prefix++;
            }

            let suffix = 0;
            while (suffix < shortest - prefix && value[value.length - 1 - suffix] === text[text.length - 1 - suffix]) {
                suffix++;
            }

            const kept = new Set();
            for (const index of positions(value)) {
                const keep = index < prefix ? stored.has(index)
                    : index >= value.length - suffix ? stored.has(index - value.length + text.length)
                    : contexts.has(contextAt(value, index));
                if (keep) {
                    kept.add(index);
                }
            }

            text = value;
            stored = kept;

            return node => {
                if (!node.data.includes(NBSP)) {
                    return node.data;
                }

                const start = offsets.get(node);
                return node.data.replaceAll(NBSP, (found, index) => (kept.has(start + index) ? found : ' '));
            };
        },
    };
}

// ---- Reading inline content ----

// A <br> is not in the model. The one a browser keeps at the end of a block means nothing; one between two words
// still separates them.
function spacedBreaks(run) {
    const leaves = [];
    const visit = node => {
        if (node.nodeType === Node.TEXT_NODE) {
            if (node.data !== '') {
                leaves.push(node);
            }
        } else if (node.nodeType === Node.ELEMENT_NODE && !IGNORED.has(tagOf(node))) {
            if (tagOf(node) === 'BR') {
                leaves.push(node);
            } else {
                node.childNodes.forEach(visit);
            }
        }
    };
    run.forEach(visit);

    const spaced = new Set();
    let before = '';
    let waiting = [];
    for (const leaf of leaves) {
        if (leaf.nodeType === Node.ELEMENT_NODE) {
            waiting.push(leaf);
            continue;
        }

        if (waiting.length > 0 && before !== '' && !/\s$/.test(before) && !/^\s/.test(leaf.data)) {
            spaced.add(waiting[0]);
        }

        waiting = [];
        before = leaf.data;
    }

    return spaced;
}

function merged(nodes) {
    const result = [];
    for (const node of nodes) {
        if (node.kind === 'text') {
            if (node.text === '') {
                continue;
            }

            const last = result[result.length - 1];
            if (last?.kind === 'text') {
                result[result.length - 1] = { kind: 'text', text: last.text + node.text };
                continue;
            }
        }

        result.push(node);
    }

    return result;
}

export function linkUrl(element) {
    const stored = element.getAttribute(URL_ATTRIBUTE);
    const href = element.getAttribute('href');
    // An href that is not the one put there with the stored address was set since, so it is the newer one.
    return href !== null && href !== stored ? href : stored;
}

function readInline(run, textOf) {
    const spaced = spacedBreaks(run);

    const convert = (node, withinLink, out) => {
        if (node.nodeType === Node.TEXT_NODE) {
            out.push({ kind: 'text', text: textOf(node) });
            return;
        }

        if (node.nodeType !== Node.ELEMENT_NODE || IGNORED.has(tagOf(node))) {
            return;
        }

        const children = (link = withinLink) => {
            const inner = [];
            node.childNodes.forEach(child => convert(child, link, inner));
            return merged(inner);
        };
        // Wrappers with nothing left in them are dropped.
        const wrap = (wrapper, content) => {
            if (content.length > 0) {
                out.push({ ...wrapper, children: content });
            }
        };

        switch (tagOf(node)) {
            case 'BR':
                if (spaced.has(node)) {
                    out.push({ kind: 'text', text: ' ' });
                }
                return;
            case 'B':
            case 'STRONG':
                wrap({ kind: 'strong' }, children());
                return;
            case 'I':
            case 'EM':
                wrap({ kind: 'emphasis' }, children());
                return;
            case 'FONT': {
                const face = node.getAttribute('face');
                if (face === CODE_FACE) {
                    // A command can leave formatting and links inside code, which the model only has around it. Each
                    // run of text is made code where it is.
                    out.push(...codeRuns(children()));
                    return;
                }

                // Taking code away from part of a stretch of it can leave another face inside it. The innermost face
                // is the one that counts, so what is in this one is not code.
                if (face !== null && isInCode(node)) {
                    wrap({ kind: PLAIN }, children());
                    return;
                }

                break;
            }
            case 'CODE': {
                const content = children();
                // The model has no link inside code. A command can put code around one, which then has to hold the
                // code instead, or its address would be lost.
                if (!withinLink && node.querySelector('a')) {
                    out.push(...codeRuns(content));
                    return;
                }

                const text = content.map(plainText).join('');
                if (text !== '') {
                    out.push({ kind: 'inlineCode', text });
                }
                return;
            }
            case 'A': {
                const url = withinLink ? null : linkUrl(node);
                if (url !== null) {
                    wrap({ kind: 'link', url }, children(true));
                    return;
                }
                break;
            }
        }

        // Everything else is unwrapped.
        node.childNodes.forEach(child => convert(child, withinLink, out));
    };

    const result = [];
    run.forEach(node => convert(node, false, result));
    return merged(result);
}

const plainText = node => node.text ?? (node.children ?? []).map(plainText).join('');

// While code is being read, what is inside it but not code. Never leaves the reader.
const PLAIN = 'plain';

const isInCode = element => element.parentElement?.closest('code, font[face]')?.matches(CODE_SELECTOR) === true;

const withoutPlain = nodes => merged(nodes.flatMap(node => {
    if (node.kind === PLAIN) {
        return withoutPlain(node.children);
    }

    return [node.children ? { ...node, children: withoutPlain(node.children) } : node];
}));

// The nodes with each run of text in them made code, keeping the formatting and links around the runs.
const codeRuns = nodes => merged(nodes.flatMap(node => {
    if (node.kind === PLAIN) {
        return withoutPlain(node.children);
    }

    if (node.kind === 'text') {
        return node.text === '' ? [] : [{ kind: 'inlineCode', text: node.text }];
    }

    return [node.children ? { ...node, children: codeRuns(node.children) } : node];
}));

// Only the white space HTML collapses, so a stored U+00A0 counts as content.
const isBlank = inline => inline.every(node => node.kind === 'text' && /^[ \t\r\n\f]*$/.test(node.text));

// ---- Reading blocks ----

// Splits DOM nodes into runs of inline content and the model's block elements, unwrapping every other element.
// With breaks, a <br> among the nodes themselves ends a run: directly in the surface or in a quote there is no line
// for it to break, and it is what separates the lines a browser leaves when it ends a list or a quote.
function split(domNodes, breaks = false) {
    const parts = [];
    let run = [];
    const flush = () => {
        if (run.length > 0) {
            parts.push({ run });
            run = [];
        }
    };
    const visitAll = (nodes, ending) => {
        for (const node of nodes) {
            if (ending && node.nodeName === 'BR') {
                flush();
            } else {
                visit(node);
            }
        }
    };
    const visit = node => {
        if (node.nodeType === Node.TEXT_NODE) {
            run.push(node);
            return;
        }

        if (node.nodeType !== Node.ELEMENT_NODE || IGNORED.has(tagOf(node))) {
            return;
        }

        const tag = tagOf(node);
        if (MODEL_BLOCKS.has(tag)) {
            flush();
            parts.push({ element: node, tag });
        } else if (OTHER_BLOCKS.has(tag)) {
            flush();
            // A <div> is the browser's, and a <br> directly in one separates lines as it does in the surface itself.
            visitAll(node.childNodes, tag === 'DIV');
            flush();
        } else if (node.querySelector(BLOCK_SELECTOR)) {
            node.childNodes.forEach(visit);
        } else {
            run.push(node);
        }
    };
    visitAll(domNodes, breaks);
    flush();
    return parts;
}

// The children of an element with nothing in it: as the node it was built from stored them, or, for one the author
// made, the one empty text run the content rules ask for.
function emptyChildren(element) {
    switch (element?.getAttribute(EMPTY)) {
        case 'absent': return undefined;
        case 'empty': return [];
        default: return [{ kind: 'text', text: '' }];
    }
}

const withChildren = (node, children) => (children === undefined ? node : { ...node, children });

// context is "top" (any block) or "quote" (paragraphs and lists only).
function readSequence(parts, context, textOf) {
    return parts.flatMap(part => {
        if (part.element) {
            return readBlock(part.element, part.tag, context, textOf);
        }

        const inline = readInline(part.run, textOf);
        return isBlank(inline) ? [] : [{ kind: 'paragraph', children: inline }];
    });
}

function readBlock(element, tag, context, textOf) {
    if (tag === 'UL' || tag === 'OL') {
        const list = readList(element, textOf);
        return list ? [list] : [];
    }

    if (tag === 'LI') {
        return readSequence(split(element.childNodes), context, textOf);
    }

    if (tag === 'BLOCKQUOTE') {
        let children = readSequence(split(element.childNodes, true), 'quote', textOf);
        if (context === 'quote') {
            return children;
        }

        // A quote with only a filler <br> left keeps a paragraph to type in. With nothing at all, it is gone.
        if (children.length === 0 && element.firstChild) {
            children = [{ kind: 'paragraph', children: emptyChildren(null) }];
        }

        return children.length > 0 ? [{ kind: 'quote', children }] : [];
    }

    const base = tag !== 'P' && context === 'top' ? { kind: 'heading', level: Number(tag[1]) } : { kind: 'paragraph' };
    const parts = split(element.childNodes);
    const hasBlocks = parts.some(part => part.element);
    const result = parts.flatMap(part => {
        if (part.element) {
            return readBlock(part.element, part.tag, context, textOf);
        }

        const inline = readInline(part.run, textOf);
        return inline.length === 0 || (hasBlocks && isBlank(inline)) ? [] : [{ ...base, children: inline }];
    });

    return result.length > 0 || hasBlocks ? result : [withChildren(base, emptyChildren(element))];
}

// A list item is inline content or paragraphs, then nested lists. Content that follows a nested list starts another
// item, which keeps the order.
function readListItems(domNodes, element, textOf) {
    const units = split(domNodes).flatMap(part => {
        if (!part.element) {
            const inline = readInline(part.run, textOf);
            return inline.length > 0 ? [{ inline }] : [];
        }

        if (part.tag === 'LI') {
            return [{ items: readListItems(part.element.childNodes, part.element, textOf) }];
        }

        return readBlock(part.element, part.tag, 'quote', textOf)
            .map(node => (node.kind === 'list' ? { list: node } : { paragraph: node }));
    });
    const content = units.length > 1 ? units.filter(unit => !unit.inline || !isBlank(unit.inline)) : units;

    const items = [];
    let own = [];
    let lists = [];
    const finish = () => {
        if (own.length === 0 && lists.length === 0) {
            return;
        }

        // An item shown without its paragraph gets it back.
        const wrapped = items.length === 0 && element?.hasAttribute(PARAGRAPH);
        const paragraphs = wrapped || own.length > 1 || own.some(unit => unit.paragraph);
        const children = paragraphs
            ? own.map(unit => unit.paragraph ?? { kind: 'paragraph', children: unit.inline })
            : own[0]?.inline ?? [];
        if (wrapped && children.length === 0) {
            children.push(emptyParagraph(element));
        }
        items.push({ kind: 'listItem', children: [...children, ...lists] });
        own = [];
        lists = [];
    };

    for (const unit of content) {
        if (unit.list) {
            lists.push(unit.list);
        } else if (unit.items) {
            finish();
            items.push(...unit.items);
        } else {
            if (lists.length > 0) {
                finish();
            }

            own.push(unit);
        }
    }

    finish();
    if (items.length > 0 || !element) {
        return items;
    }

    return [element.hasAttribute(PARAGRAPH)
        ? { kind: 'listItem', children: [emptyParagraph(element)] }
        : withChildren({ kind: 'listItem' }, emptyChildren(element))];
}

// The paragraph of an item shown without it, with nothing left in it.
function emptyParagraph(element) {
    const stored = element.getAttribute(PARAGRAPH);
    const children = stored === 'absent' ? undefined : stored === 'empty' ? [] : [{ kind: 'text', text: '' }];
    return withChildren({ kind: 'paragraph' }, children);
}

const isEmptyRun = node => node.kind === 'text' && node.text === '';

// Null for a list with no items left.
function readList(element, textOf) {
    const items = [];
    let loose = [];
    const flush = () => {
        if (loose.some(node => node.nodeType === Node.ELEMENT_NODE || !/^[ \t\r\n\f]*$/.test(node.data))) {
            items.push(...readListItems(loose, null, textOf));
        }

        loose = [];
    };

    for (const child of element.childNodes) {
        const tag = child.nodeType === Node.ELEMENT_NODE ? tagOf(child) : null;
        if (tag === 'LI') {
            flush();
            items.push(...readListItems(child.childNodes, child, textOf));
        } else if (tag === 'UL' || tag === 'OL') {
            // Indenting can leave a list directly inside a list. It belongs to the item before it.
            flush();
            const nested = readList(child, textOf);
            if (!nested) {
                continue;
            }

            const previous = items.pop();
            const kept = (previous?.children ?? []).filter(node => !isEmptyRun(node));
            items.push({ kind: 'listItem', children: [...kept, nested] });
        } else if (child.nodeType === Node.TEXT_NODE || tag !== null) {
            loose.push(child);
        }
    }

    flush();
    if (items.length === 0) {
        return null;
    }

    const base = { kind: 'list' };
    if (tagOf(element) === 'OL') {
        base.ordered = true;
    } else if (element.getAttribute(ORDERED) !== 'none') {
        base.ordered = false;
    }

    return { ...base, children: items };
}

// ---- Reading a surface ----

const isBlockElement = node =>
    node.nodeType === Node.ELEMENT_NODE && !IGNORED.has(tagOf(node))
    && (MODEL_BLOCKS.has(tagOf(node)) || OTHER_BLOCKS.has(tagOf(node)) || node.querySelector(BLOCK_SELECTOR) !== null);

// Splitting an element copies its attributes. The first keeps the key; later ones, and new elements, get their own.
function assignKeys(root) {
    const used = new Set();
    for (const element of root.children) {
        if (!isBlockElement(element)) {
            continue;
        }

        let key = element.getAttribute(KEY);
        if (!key || used.has(key)) {
            if (key) {
                element.removeAttribute(EMPTY);
            }

            key = newKey();
            element.setAttribute(KEY, key);
        }

        used.add(key);
    }
}

const entry = (key, node) => ({ key, node, signature: JSON.stringify(node) });

// Reads one surface. read() returns its top-level nodes as [{ key, node, signature }].
export function createReader(root, inline) {
    const spaces = createSpaces(root);
    // Keys for what has no element of its own to carry one: loose text, and the second and later nodes of an element.
    let otherKeys = new WeakMap();

    const keyFor = (source, index) => {
        const keys = otherKeys.get(source) ?? [];
        keys[index] ??= newKey();
        otherKeys.set(source, keys);
        return keys[index];
    };

    const readProse = textOf => {
        const only = root.childNodes.length === 1 ? root.firstElementChild : null;
        if (only?.hasAttribute(PLACEHOLDER) && only.textContent === '') {
            return [];
        }

        assignKeys(root);
        const result = [];
        let loose = [];
        const flush = () => {
            if (loose.length > 0) {
                readSequence(split(loose, true), 'top', textOf)
                    .forEach((node, index) => result.push(entry(keyFor(loose[0], index), node)));
                loose = [];
            }
        };

        for (const child of root.childNodes) {
            if (isBlockElement(child)) {
                flush();
                readSequence(split([child]), 'top', textOf).forEach((node, index) =>
                    result.push(entry(index === 0 ? child.getAttribute(KEY) : keyFor(child, index), node)));
            } else {
                loose.push(child);
            }
        }

        flush();
        return result;
    };

    return {
        // Call after buildSurface. keyedNodes are what it built.
        adopt() {
            otherKeys = new WeakMap();
            spaces.adopt();
        },

        read() {
            const textOf = spaces.read();
            // Inline content has no elements to carry keys, so a position is its key and every patch is complete.
            return inline
                ? readInline([...root.childNodes], textOf).map((node, index) => entry(`i${index}`, node))
                : readProse(textOf);
        },
    };
}

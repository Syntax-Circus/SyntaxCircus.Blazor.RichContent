// The writing commands, and the keys, paste and toolbar that run them.
//
// Every change goes through the browser's own editing commands (execCommand), so that its undo and redo cover all of
// it. Where a command needs markup, it is the serialization of nodes made with DOM APIs, from nodes already in the
// surface or with text set as text.

import { CODE_FACE, PLAIN_FACE, codeElement, firstFamily, linkUrl, mayBeHref } from './dom.js';

// The most of a link's address that is reported, so that the message stays small. The link box holds no more.
const MAX_REPORTED_URL = 4000;
const GECKO = CSS.supports('-moz-appearance', 'none');

// Above zero while one of the commands below runs. WebKit raises beforeinput for those as it does for the author's
// own edits, which alone are handled there.
let running = 0;

function exec(command, value = null) {
    running++;
    try {
        return document.execCommand(command, false, value);
    } finally {
        running--;
    }
}

// ---- Text blocks and places in them ----

const TEXT_BLOCKS = 'p,div,h2,h3,h4,li,blockquote';

// An element that holds a line of text itself, rather than other blocks.
function isTextBlock(element) {
    if (!element.matches(TEXT_BLOCKS)
        || element.querySelector(':scope > p, :scope > div, :scope > h2, :scope > h3, :scope > h4, :scope > blockquote')) {
        return false;
    }

    return element.tagName === 'LI' || !element.querySelector(':scope > ul, :scope > ol');
}

const textBlocks = root => [...root.querySelectorAll(TEXT_BLOCKS)].filter(isTextBlock);

function textBlockOf(root, node) {
    for (let element = node instanceof Element ? node : node.parentElement; element && element !== root;
        element = element.parentElement) {
        if (isTextBlock(element)) {
            return element;
        }
    }

    return null;
}

// Where a block's own text ends: before its nested lists and before the filler <br>.
function ownEnd(block) {
    const children = [...block.childNodes];
    let end = children.findIndex(child => child.nodeName === 'UL' || child.nodeName === 'OL');
    if (end < 0) {
        end = children.length;
    }

    if (end > 0 && children[end - 1].nodeName === 'BR') {
        end--;
    }

    return [block, end];
}

function rangeBetween(start, end) {
    const range = document.createRange();
    range.setStart(...start);
    range.setEnd(...end);
    return range;
}

const ownText = block => rangeBetween([block, 0], ownEnd(block)).toString();

const offsetIn = (block, container, offset) => rangeBetween([block, 0], [container, offset]).toString().length;

// The place so many characters into a block's own text.
function pointAt(block, characters) {
    const end = rangeBetween([block, 0], ownEnd(block));
    const walker = document.createTreeWalker(block, NodeFilter.SHOW_TEXT);
    let left = characters;
    let last = null;
    for (let node = walker.nextNode(); node && end.comparePoint(node, 0) <= 0; node = walker.nextNode()) {
        if (left <= node.data.length) {
            return [node, left];
        }

        left -= node.data.length;
        last = node;
    }

    return last ? [last, last.data.length] : [block, 0];
}

export function selectionIn(root) {
    const selection = getSelection();
    if (selection.rangeCount === 0) {
        return null;
    }

    const range = selection.getRangeAt(0);
    return root.contains(range.startContainer) && root.contains(range.endContainer) ? range : null;
}

function select(range) {
    const selection = getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
}

function placeCaret(point) {
    select(rangeBetween(point, point));
}

// The selection as positions among the text blocks, which survive elements being replaced by others.
function saveSelection(root) {
    const range = selectionIn(root);
    if (!range) {
        return null;
    }

    const blocks = textBlocks(root);
    const position = (container, offset) => {
        const block = textBlockOf(root, container);
        return block ? [blocks.indexOf(block), offsetIn(block, container, offset)] : null;
    };
    const start = position(range.startContainer, range.startOffset);
    const end = position(range.endContainer, range.endOffset);
    return start && end ? { start, end } : null;
}

function restoreSelection(root, saved) {
    if (!saved) {
        return;
    }

    const blocks = textBlocks(root);
    const start = blocks[saved.start[0]];
    const end = blocks[saved.end[0]];
    if (start && end) {
        select(rangeBetween(pointAt(start, saved.start[1]), pointAt(end, saved.end[1])));
    }
}

// Whether the range reaches into the block's own text, and not only into lists nested in it.
function touches(range, block) {
    const own = rangeBetween([block, 0], ownEnd(block));
    return range.compareBoundaryPoints(Range.END_TO_START, own) <= 0
        && range.compareBoundaryPoints(Range.START_TO_END, own) >= 0;
}

function selectedBlocks(root, range) {
    if (range.collapsed) {
        const block = textBlockOf(root, range.startContainer);
        return block ? [block] : [];
    }

    return textBlocks(root).filter(block => touches(range, block));
}

function closestWithin(root, node, selector) {
    const element = (node instanceof Element ? node : node.parentElement)?.closest(selector);
    return element && element !== root && root.contains(element) ? element : null;
}

// ---- State at the cursor ----

// The first family of what the browser answers, which can be a computed list.
const fontAtCaret = () => firstFamily(document.queryCommandValue('fontName'));

// The text nodes the range selects at least one character of.
function selectedTexts(root, range) {
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    const found = [];
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
        if (node.data !== '' && range.intersectsNode(node)
            && !(node === range.startContainer && range.startOffset === node.data.length)
            && !(node === range.endContainer && range.endOffset === 0)) {
            found.push(node);
        }
    }

    return found;
}

function isCode(root, range) {
    if (!range.collapsed) {
        const texts = selectedTexts(root, range);
        return texts.length > 0 && texts.every(text => codeElement(root, text) !== null);
    }

    // The browser knows what it will format the next character as, which a click on a button may just have changed.
    const font = fontAtCaret();
    if (font === CODE_FACE) {
        return true;
    }

    return font !== PLAIN_FACE && codeElement(root, range.startContainer) !== null;
}

function linksIn(root, range) {
    const around = closestWithin(root, range.startContainer, 'a');
    if (range.collapsed) {
        return around ? [around] : [];
    }

    return [...new Set(selectedTexts(root, range).map(text => closestWithin(root, text, 'a')).filter(Boolean))];
}

export function selectionState(root) {
    const range = selectionIn(root);
    if (!range) {
        return null;
    }

    const first = range.collapsed ? range.startContainer : selectedTexts(root, range)[0] ?? range.startContainer;
    const within = selector => closestWithin(root, first, selector);
    const link = linksIn(root, range)[0];
    const address = link ? linkUrl(link) ?? '' : null;
    const list = within('ul,ol');
    return {
        bold: document.queryCommandState('bold'),
        italic: document.queryCommandState('italic'),
        code: isCode(root, range),
        linkUrl: address?.slice(0, MAX_REPORTED_URL) ?? null,
        // Said, and not left to be guessed: the start of an address must never be taken for the whole of it.
        linkUrlTruncated: address !== null && address.length > MAX_REPORTED_URL,
        style: within('h2,h3,h4')?.tagName.toLowerCase() ?? 'p',
        list: list ? (list.tagName === 'OL' ? 'numbers' : 'bullets') : 'none',
        quote: within('blockquote') !== null,
        inListItem: within('li') !== null,
    };
}

// ---- Lists ----

function outermostList(root, node) {
    let found = null;
    for (let element = closestWithin(root, node, 'ul,ol'); element; element = closestWithin(root, element.parentElement, 'ul,ol')) {
        found = element;
    }

    return found;
}

const isList = node => node?.nodeName === 'UL' || node?.nodeName === 'OL';

// A browser's indent can leave a list directly inside a list. It belongs to the item before it.
function tidy(list) {
    for (const nested of [...list.querySelectorAll('ul > ul, ul > ol, ol > ul, ol > ol')]) {
        if (nested.previousElementSibling?.tagName === 'LI') {
            nested.previousElementSibling.append(nested);
        }
    }
}

// Makes the item the last child of the one before it.
function indentItem(item) {
    const previous = item.previousElementSibling;
    if (previous?.tagName !== 'LI') {
        return false;
    }

    let nested = isList(previous.lastElementChild) ? previous.lastElementChild : null;
    if (!nested) {
        nested = document.createElement(item.parentElement.tagName);
        previous.querySelector(':scope > br:last-child')?.remove();
        previous.append(nested);
    }

    nested.append(item);
    return true;
}

// Moves the item to after the one it is nested in. What followed it stays below it, so the order of the text stays.
function outdentItem(item) {
    const list = item.parentElement;
    const parent = list.parentElement;
    if (parent?.tagName !== 'LI') {
        return false;
    }

    const following = [];
    while (item.nextSibling) {
        following.push(item.nextSibling);
        item.nextSibling.remove();
    }

    if (following.length > 0) {
        const rest = list.cloneNode(false);
        rest.removeAttribute('data-key');
        rest.append(...following);
        item.querySelector(':scope > br:last-child')?.remove();
        item.append(rest);
    }

    while (list.nextSibling) {
        item.append(list.nextSibling);
    }

    parent.after(item);
    if (!list.querySelector(':scope > li')) {
        list.remove();
    }

    if (!parent.firstChild) {
        parent.append(document.createElement('br'));
    }

    return true;
}

const outline = list => [...list.querySelectorAll('li')].map(item => {
    let depth = 0;
    for (let element = item.parentElement; element !== list; element = element.parentElement) {
        depth += isList(element) ? 1 : 0;
    }

    return `${depth}:${item.textContent}`;
}).join('\n');

// How deep a surface's content may be nested, as RichContentLimits.Depth has it by default. .NET refuses more.
const MAX_DEPTH = 8;

// The depth of the model node an element or text is read as.
function modelDepth(node) {
    if (node.nodeType !== Node.ELEMENT_NODE || node.tagName === 'CODE') {
        return 1;
    }

    // A <font> adds no level of its own: the code it makes is the text in it, below any formatting that is in it too.
    if (node.tagName === 'FONT') {
        return Math.max(1, ...[...node.childNodes].map(modelDepth));
    }

    // A list item shown without its paragraph has one all the same, around all but its lists.
    const wrapped = node.hasAttribute('data-rt-paragraph');
    const depths = [...node.childNodes].filter(child => child.nodeName !== 'BR')
        .map(child => modelDepth(child) + (wrapped && !isList(child) ? 1 : 0));
    return 1 + Math.max(0, ...depths);
}

// Chromium and WebKit move the start of a selection into the first paragraph of the first item, and then put the new
// list inside it. Such a list is not replaced there.
const canReplace = list => GECKO || !list.querySelector(':scope > li:first-child > p:first-child');

// Replaces a list's content by that of a changed copy of it, as one undoable step. Verifies the result, and takes
// the step back if the browser did something else with it.
function replaceList(root, list, copy) {
    const index = textBlocks(root).findIndex(block => list.contains(block));
    const range = document.createRange();
    if (GECKO) {
        range.setStartBefore(list);
        range.setEnd(list, list.childNodes.length);
    } else {
        range.selectNodeContents(list);
    }

    const wanted = outline(copy);
    if (GECKO) {
        // Firefox gives an empty item it inserts a filler <br> of its own, and would show two.
        for (const filler of copy.querySelectorAll('li > br:only-child')) {
            filler.remove();
        }
    }

    // The serialization of a copy of what is in the surface: its text came from text nodes.
    const markup = copy.outerHTML;
    select(range);
    if (!exec('insertHTML', markup)) {
        return false;
    }

    const block = textBlocks(root)[index];
    const result = block && outermostList(root, block);
    if (result && outline(result) === wanted) {
        return true;
    }

    exec('undo');
    return false;
}

// Nests or un-nests the list items in the selection. False when there is nothing that can move.
function shiftItems(root, outwards) {
    const range = selectionIn(root);
    if (!range) {
        return false;
    }

    const items = [...new Set(selectedBlocks(root, range).map(block => closestWithin(root, block, 'li')).filter(Boolean))];
    const list = items.length > 0 ? outermostList(root, items[0]) : null;
    if (!list || !canReplace(list)) {
        return false;
    }

    const all = [...list.querySelectorAll('li')];
    // An item moves with everything below it, so what is below a selected one does not move again.
    const chosen = items.filter(item => list.contains(item) && !items.some(other => other !== item && other.contains(item)));
    const indexes = chosen.map(item => all.indexOf(item));
    const copy = list.cloneNode(true);
    tidy(copy);
    const copies = [...copy.querySelectorAll('li')];
    let moved = false;
    for (const index of outwards ? indexes.reverse() : indexes) {
        moved = (outwards ? outdentItem : indentItem)(copies[index]) || moved;
    }

    let quotes = 0;
    for (let quote = closestWithin(root, list, 'blockquote'); quote; quote = closestWithin(root, quote.parentElement, 'blockquote')) {
        quotes++;
    }

    // A list nested deeper than the model allows could not be delivered.
    if (!moved || quotes + modelDepth(copy) > MAX_DEPTH) {
        return false;
    }

    const saved = saveSelection(root);
    const replaced = replaceList(root, list, copy);
    restoreSelection(root, saved);
    return replaced;
}

// ---- Inline commands ----

// Both ways are the font command, which changes the face and nothing else. Commands that remove formatting take bold
// and italic with the code, which cannot be put back where they were only in part of the selection.
function toggleCode(root) {
    const range = selectionIn(root);
    return range !== null && exec('fontName', isCode(root, range) ? PLAIN_FACE : CODE_FACE);
}

function selectNode(node) {
    const range = document.createRange();
    range.selectNode(node);
    select(range);
}

function unlink(root, link) {
    selectNode(link);
    exec('unlink');
    // Firefox leaves a link that has no live address. Giving it one first lets it be removed.
    if (link.isConnected && !link.hasAttribute('href')) {
        selectNode(link);
        exec('createLink', '/');
        const made = selectionIn(root) && linksIn(root, selectionIn(root))[0];
        if (made) {
            selectNode(made);
            exec('unlink');
        }
    }
}

// An empty address removes the links in the selection.
function setLink(root, url) {
    let range = selectionIn(root);
    // .NET has checked the length against the host's limit. The address is checked again before it can be a live one.
    if (!range || (url !== '' && !mayBeHref(url, Infinity))) {
        return false;
    }

    const links = linksIn(root, range);
    if (url === '') {
        const saved = saveSelection(root);
        links.forEach(link => unlink(root, link));
        restoreSelection(root, saved);
        return links.length > 0;
    }

    if (links.length === 1 && (range.collapsed || selectedTexts(root, range).every(text => links[0].contains(text)))) {
        // The whole link takes the new address. Selected from outside, no browser nests another link in it.
        selectNode(links[0]);
        return exec('createLink', url);
    }

    if (links.length > 0) {
        const saved = saveSelection(root);
        links.forEach(link => unlink(root, link));
        restoreSelection(root, saved);
        range = selectionIn(root);
    }

    if (range.collapsed) {
        // With nothing selected, the address is its own text.
        if (!exec('insertText', url)) {
            return false;
        }

        const { focusNode, focusOffset } = getSelection();
        if (focusNode.nodeType !== Node.TEXT_NODE || focusOffset < url.length) {
            return false;
        }

        getSelection().setBaseAndExtent(focusNode, focusOffset - url.length, focusNode, focusOffset);
    }

    const done = exec('createLink', url);
    getSelection().collapseToEnd();
    return done;
}

// ---- Block commands ----

const STYLES = ['p', 'h2', 'h3', 'h4'];

// One block at a time: over several at once, some browsers make one block of them all.
function formatEach(root, tag, blocks) {
    const saved = saveSelection(root);
    const indexes = blocks.map(block => textBlocks(root).indexOf(block));
    let done = false;
    for (const index of indexes) {
        const block = textBlocks(root)[index];
        if (block && block.tagName.toLowerCase() !== tag) {
            placeCaret(pointAt(block, 0));
            done = exec('formatBlock', tag) || done;
        }
    }

    restoreSelection(root, saved);
    return done;
}

function setStyle(root, tag) {
    const range = selectionIn(root);
    if (!range || !STYLES.includes(tag)) {
        return false;
    }

    const blocks = selectedBlocks(root, range).filter(block => !closestWithin(root, block, 'li,blockquote'));
    if (blocks.length > 0) {
        return formatEach(root, tag, blocks);
    }

    // Text that is in no block yet.
    return !closestWithin(root, range.startContainer, 'li,blockquote') && exec('formatBlock', tag);
}

// A <br> directly in the element with something after it, which ends a line. CSS cannot tell: it does not see text.
const lineBreakIn = element =>
    [...element.children].find(child => child.tagName === 'BR' && child.nextSibling) ?? null;

// Ending a list or a quote can leave lines lying loose in the surface, or in a <div>, perhaps inside another and apart
// from each other only by <br>. Each becomes a paragraph.
function makeParagraphs(root) {
    const lineDivisions = () => [...root.querySelectorAll('div')]
        .filter(division => !closestWithin(root, division, 'li') && isTextBlock(division));
    // Replacing such a <br> by a paragraph break splits the <div> there.
    for (let attempt = 0; attempt < 50; attempt++) {
        const br = lineDivisions().map(lineBreakIn).find(Boolean);
        if (!br) {
            break;
        }

        selectNode(br);
        if (!exec('insertParagraph') || br.isConnected) {
            break;
        }
    }

    for (const division of lineDivisions()) {
        if (division.isConnected) {
            placeCaret(pointAt(division, 0));
            exec('formatBlock', 'p');
        }
    }

    const loose = node => (node.nodeType === Node.TEXT_NODE && node.data !== '')
        || (node.nodeType === Node.ELEMENT_NODE && !node.matches('p,h1,h2,h3,h4,h5,h6,ul,ol,blockquote,div,br'));
    // Lines lie loose in the surface, or in a <div> beside what else it holds.
    const containers = () => [root, ...root.querySelectorAll('div')]
        .filter(container => container === root || (!closestWithin(root, container, 'li') && !isTextBlock(container)));
    for (let attempt = 0; attempt < 50; attempt++) {
        const line = containers().flatMap(container => [...container.childNodes]).find(loose);
        if (!line) {
            break;
        }

        const parent = line.parentNode;

        const text = line.nodeType === Node.TEXT_NODE ? line : document.createTreeWalker(line, NodeFilter.SHOW_TEXT).nextNode();
        if (!text) {
            break;
        }

        placeCaret([text, 0]);
        if (!exec('formatBlock', 'p') || (text.isConnected && text.parentNode === parent)) {
            break;
        }
    }
}

// Runs a command and puts the selection back with the text it was in, which commands that replace blocks do not
// always do. By block where the blocks stay as many, which finds empty lines too; otherwise by the text before it.
function keepingSelection(root, command) {
    const saved = saveSelection(root);
    const count = textBlocks(root).length;
    const range = selectionIn(root);
    const before = (container, offset) => rangeBetween([root, 0], [container, offset]).toString().length;
    const ends = [before(range.startContainer, range.startOffset), before(range.endContainer, range.endOffset)];
    const collapsed = range.collapsed;
    if (!command()) {
        return false;
    }

    if (saved && textBlocks(root).length === count) {
        restoreSelection(root, saved);
        return true;
    }

    const point = (characters, atEnd) => {
        const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
        let left = characters;
        for (let node = walker.nextNode(); node; node = walker.nextNode()) {
            if (left < node.data.length || (atEnd && left === node.data.length)) {
                return [node, left];
            }

            left -= node.data.length;
        }

        return null;
    };
    const from = point(ends[0], collapsed);
    const to = collapsed ? from : point(ends[1], true);
    if (from && to) {
        select(rangeBetween(from, to));
    }

    return true;
}

// Runs a command that can leave loose lines, and makes paragraphs of them.
const leavingParagraphs = (root, command) => keepingSelection(root, () => {
    const done = command();
    if (done) {
        makeParagraphs(root);
    }

    return done;
});

function toggleList(root, command, tag) {
    const range = selectionIn(root);
    if (!range) {
        return false;
    }

    const list = closestWithin(root, range.startContainer, 'ul,ol');
    if (list && list.tagName === tag) {
        if (closestWithin(root, list.parentElement, 'li') || list.parentElement.matches('ul,ol')) {
            return shiftItems(root, true);
        }

        return leavingParagraphs(root, () => exec(command));
    }

    // A list item cannot hold a heading, so headings become paragraphs first.
    const headings = selectedBlocks(root, range).filter(block => block.matches('h2,h3,h4'));
    if (headings.length > 0) {
        formatEach(root, 'p', headings);
    }

    if (!GECKO) {
        // Chromium and WebKit put a new list inside the paragraph it is made from, where its items cannot later be
        // rearranged. Inside a <div>, which may hold a list, they can.
        const paragraphs = selectedBlocks(root, selectionIn(root)).filter(block => block.tagName === 'P');
        formatEach(root, 'div', paragraphs);
    }

    return keepingSelection(root, () => exec(command));
}

// A paragraph holding what the line holds.
function paragraphOf(line) {
    const paragraph = document.createElement('p');
    paragraph.append(...line.cloneNode(true).childNodes);
    return paragraph;
}

const textsOf = elements => JSON.stringify([...elements].map(element => element.textContent));

// Chromium and WebKit make a quote with no paragraph in it, its lines apart by <br> alone, and Enter in one starts
// another quote. Their indent keeps paragraphs but writes style attributes. So the quote is made their way, and then
// what is in it is replaced by the lines as paragraphs, as a list's items are. Both steps are the browser's own.
function quoteAsParagraphs(root) {
    const lines = selectedBlocks(root, selectionIn(root));
    const paragraphs = lines.map(paragraphOf);
    const wanted = textsOf(paragraphs);
    const text = paragraphs.map(paragraph => paragraph.textContent).join('');
    const saved = saveSelection(root);
    if (!exec('formatBlock', 'blockquote')) {
        return false;
    }

    if (paragraphs.length === 0) {
        return true;
    }

    const range = selectionIn(root);
    const quote = range && closestWithin(root, range.startContainer, 'blockquote');
    if (!quote || quote.textContent !== text) {
        exec('undo');
        return false;
    }

    const inside = document.createRange();
    inside.selectNodeContents(quote);
    // The serialization of copies of what was in the surface.
    const markup = serialized(paragraphs);
    select(inside);
    exec('insertHTML', markup);
    if (!quote.isConnected || textsOf(quote.children) !== wanted
        || [...quote.childNodes].some(child => child.nodeName !== 'P')) {
        exec('undo');
        exec('undo');
        return false;
    }

    restoreSelection(root, saved);
    return true;
}

function toggleQuote(root) {
    const range = selectionIn(root);
    if (!range || selectedBlocks(root, range).some(block => closestWithin(root, block, 'li'))) {
        return false;
    }

    if (closestWithin(root, range.startContainer, 'blockquote')) {
        return leavingParagraphs(root, () => exec('outdent'));
    }

    // A quote cannot hold a quote.
    if (selectedBlocks(root, range).some(block => closestWithin(root, block, 'blockquote'))) {
        return false;
    }

    // A quote cannot hold a heading.
    const headings = selectedBlocks(root, range).filter(block => block.matches('h2,h3,h4'));
    if (headings.length > 0) {
        formatEach(root, 'p', headings);
    }

    // Firefox quotes with the paragraphs kept inside, by indenting; asked to format a block as a quote it makes one
    // in which Enter only breaks the line.
    return GECKO ? keepingSelection(root, () => exec('indent')) : quoteAsParagraphs(root);
}

const BLOCK_COMMANDS = {
    style: (root, argument) => setStyle(root, argument),
    bullets: root => toggleList(root, 'insertUnorderedList', 'UL'),
    numbers: root => toggleList(root, 'insertOrderedList', 'OL'),
    quote: root => toggleQuote(root),
};

const INLINE_COMMANDS = {
    bold: () => exec('bold'),
    italic: () => exec('italic'),
    code: root => toggleCode(root),
    link: (root, argument) => setLink(root, argument ?? ''),
};

// Runs a command where the selection is. Returns whether it ran.
export function runCommand(root, inline, name, argument) {
    if (!selectionIn(root)) {
        return false;
    }

    // Elements and not style attributes, which the model cannot read and a strict policy refuses.
    exec('styleWithCSS', 'false');
    if (Object.hasOwn(INLINE_COMMANDS, name)) {
        return INLINE_COMMANDS[name](root, argument);
    }

    return !inline && Object.hasOwn(BLOCK_COMMANDS, name) && BLOCK_COMMANDS[name](root, argument);
}

// ---- Joining blocks (Firefox) ----

// Firefox's own editing, when it joins two blocks, can delete all of the second one from its first inline element on
// (seen where their kinds differ: a paragraph, a heading, a list item). So there every deletion or replacement that
// would join blocks is done here instead, still with the browser's own commands: everything from the start of the
// selection to the end of the last block is replaced by a copy of what that block keeps, so nothing is left to join.

// The block a boundary is in. A boundary between blocks is moved into the one on the side the selection covers.
function joinBoundary(root, container, offset, isStart) {
    const block = textBlockOf(root, container);
    if (block) {
        return { block, point: [container, offset] };
    }

    const at = rangeBetween([container, offset], [container, offset]);
    const blocks = textBlocks(root);
    const found = isStart
        ? blocks.find(candidate => at.comparePoint(candidate, 0) >= 0)
        : blocks.findLast(candidate => at.comparePoint(candidate, 0) < 0);
    return found ? { block: found, point: isStart ? [found, 0] : ownEnd(found) } : null;
}

// The two ends, when changing this range would join blocks. Ranges that cover whole elements only join nothing.
function joinOf(root, range) {
    if (!range || range.collapsed
        || (!textBlockOf(root, range.startContainer) && !textBlockOf(root, range.endContainer))) {
        return null;
    }

    const start = joinBoundary(root, range.startContainer, range.startOffset, true);
    const end = joinBoundary(root, range.endContainer, range.endOffset, false);
    return GECKO && start && end && start.block !== end.block ? { start, end } : null;
}

// Replaces the joined range by text, as one undoable step, and puts the cursor after the text.
function replaceJoin(root, join, text) {
    const { start, end } = join;
    const index = textBlocks(root).indexOf(start.block);
    const at = offsetIn(start.block, ...start.point);
    const box = document.createElement('div');
    if (text !== '') {
        box.append(document.createTextNode(text));
    }

    box.append(rangeBetween(end.point, ownEnd(end.block)).cloneContents());
    select(rangeBetween(start.point, ownEnd(end.block)));
    // The serialization of nodes made here and of copies of the surface's own.
    const markup = box.innerHTML;
    if (markup === '') {
        exec('delete');
    } else {
        exec('insertHTML', markup);
        const block = textBlocks(root)[index];
        if (block) {
            placeCaret(pointAt(block, at + text.length));
        }
    }
}

function targetRange(root, event) {
    const target = event.getTargetRanges()[0];
    if (!target) {
        return selectionIn(root);
    }

    return root.contains(target.startContainer) && root.contains(target.endContainer)
        ? rangeBetween([target.startContainer, target.startOffset], [target.endContainer, target.endOffset])
        : null;
}

// Before an input method replaces a selection, which cannot be prevented once it starts.
export function prepareComposition(root) {
    const join = joinOf(root, selectionIn(root));
    if (join) {
        replaceJoin(root, join, '');
    }
}

// ---- Enter ----

const isEmptyLine = block => ownText(block) === '' && !block.querySelector(':scope > ul, :scope > ol');

// Enter on an empty line of a list or quote leaves it. True if that was done here.
function leaveEmptyLine(root) {
    const range = selectionIn(root);
    const block = range?.collapsed ? textBlockOf(root, range.startContainer) : null;
    if (!block || !isEmptyLine(block)) {
        return false;
    }

    const item = closestWithin(root, block, 'li');
    if (item) {
        // A browser leaves a top-level list itself; one level of nesting it does not leave reliably.
        return closestWithin(root, item.parentElement, 'li') !== null && shiftItems(root, true);
    }

    if (!closestWithin(root, block, 'blockquote')) {
        return false;
    }

    exec('outdent');
    const after = selectionIn(root);
    if (after && !closestWithin(root, after.startContainer, 'p,blockquote')) {
        exec('formatBlock', 'p');
    }

    return true;
}

// ---- Events ----

const REPLACING = /^(delete|insertText$|insertReplacementText$|insertParagraph$|insertLineBreak$)/;
// Formatting the model cannot store, which a browser's menus offer.
const FORMATTING = /^format(Underline|StrikeThrough|Superscript|Subscript|BackColor|FontColor|Justify|SetBlockTextDirection|SetInlineTextDirection)/;

export function handleBeforeInput(root, inline, event) {
    if (running > 0) {
        return;
    }

    const type = event.inputType;
    // Nothing the model cannot store, and nothing dragged in.
    if (FORMATTING.test(type) || type === 'insertFromDrop' || type === 'insertFromPaste' || type === 'deleteByDrag') {
        event.preventDefault();
        return;
    }

    const breaks = type === 'insertParagraph' || type === 'insertLineBreak';
    if (breaks && inline) {
        event.preventDefault();
        return;
    }

    if (GECKO && event.cancelable && !event.isComposing && REPLACING.test(type)) {
        const join = joinOf(root, targetRange(root, event));
        if (join) {
            event.preventDefault();
            replaceJoin(root, join, type.startsWith('insert') && !breaks
                ? event.data ?? event.dataTransfer?.getData('text/plain') ?? ''
                : '');
            if (breaks) {
                exec('insertParagraph');
            }

            return;
        }
    }

    if (!breaks || !event.cancelable) {
        return;
    }

    if (leaveEmptyLine(root)) {
        event.preventDefault();
    } else if (type === 'insertLineBreak') {
        // The model has no line break, so Shift+Enter is Enter.
        event.preventDefault();
        exec('insertParagraph');
    }
}

// At the end of code that ends its line there is nowhere to the right to go, so the arrow only leaves the code.
function leaveCodeAtLineEnd(root) {
    const range = selectionIn(root);
    if (!range?.collapsed) {
        return false;
    }

    const code = codeElement(root, range.startContainer);
    const block = code && textBlockOf(root, code);
    // A style can make a whole block code, and then there is nothing inside the block to step out of.
    if (!block || block === code || fontAtCaret() === PLAIN_FACE
        || rangeBetween([range.startContainer, range.startOffset], ownEnd(block)).toString() !== '') {
        return false;
    }

    let outer = code;
    while (outer.parentElement !== block) {
        outer = outer.parentElement;
    }

    const after = document.createRange();
    after.setStartAfter(outer);
    select(after);
    if (!GECKO) {
        // These browsers put the cursor back inside, so what is typed next is also told not to be code.
        exec('styleWithCSS', 'false');
        exec('fontName', PLAIN_FACE);
    }

    return true;
}

const SHORTCUTS = { b: 'bold', i: 'italic', e: 'code' };

const toolbarOf = root => root.closest('[data-rt-editor]')?.querySelector('[data-rt-toolbar]') ?? null;

const controlsOf = toolbar => [...toolbar.querySelectorAll('button, select')].filter(control => !control.disabled);

// run(name) runs a command as the toolbar does. changed() is told when only the state at the cursor changed, and
// linkWanted() when the author asks for the link box.
export function handleKeydown(root, inline, event, { run, changed, linkWanted }) {
    if (event.defaultPrevented || event.isComposing) {
        return;
    }

    const { key, altKey, shiftKey } = event;
    const command = event.ctrlKey || event.metaKey;
    if (key === 'F10' && altKey && !command) {
        const first = toolbarOf(root) && controlsOf(toolbarOf(root))[0];
        if (first) {
            event.preventDefault();
            first.focus();
        }
    } else if (command && !altKey && !shiftKey) {
        const letter = key.toLowerCase();
        if (Object.hasOwn(SHORTCUTS, letter)) {
            event.preventDefault();
            run(SHORTCUTS[letter]);
        } else if (letter === 'k') {
            event.preventDefault();
            linkWanted();
        } else if (letter === 'u') {
            event.preventDefault();
        }
    } else if (key === 'Tab' && !command && !altKey && !inline) {
        // Only a Tab that moves a list item is kept. Any other moves the focus, so the keyboard is never held here.
        exec('styleWithCSS', 'false');
        if (shiftItems(root, shiftKey)) {
            event.preventDefault();
        }
    } else if (key === 'ArrowRight' && !command && !altKey && !shiftKey && leaveCodeAtLineEnd(root)) {
        event.preventDefault();
        changed();
    }
}

// ---- Paste ----

// Blank lines separate paragraphs; a single line break is a space.
function paragraphsOf(text) {
    const lines = text.replace(/\r\n?/g, '\n').replace(/[\u0000-\u0008\u000b-\u001f\u007f]/g, '').replace(/^\n+|\n+$/g, '');
    const paragraphs = lines.split(/\n[ \t]*(?:\n[ \t]*)+/);
    if (paragraphs.length === 1) {
        return lines === '' ? [] : [lines.replace(/\n/g, ' ')];
    }

    // The spaces before the first paragraph and after the last one belong to the line they are pasted into.
    return paragraphs
        .map((paragraph, index) => {
            const line = paragraph.replace(/\n/g, ' ');
            const start = index === 0 ? line : line.trimStart();
            return index === paragraphs.length - 1 ? start : start.trimEnd();
        })
        .filter(paragraph => paragraph.trim() !== '');
}

function withText(element, text) {
    element.textContent = text;
    return element;
}

// A copy of a block, without its identity, holding what is between two places in it.
function partOf(block, from, to) {
    const part = block.cloneNode(false);
    part.append(rangeBetween(from, to).cloneContents());
    return part;
}

const serialized = nodes => {
    const box = document.createElement('div');
    box.append(...nodes);
    return box.innerHTML;
};

// Pastes several paragraphs where the selection, now within one block, is. One undoable step.
function insertParagraphs(root, paragraphs) {
    const range = selectionIn(root);
    const block = textBlockOf(root, range.startContainer);
    const first = paragraphs[0];
    const last = paragraphs[paragraphs.length - 1];
    const middle = paragraphs.slice(1, -1);
    const within = block && textBlockOf(root, range.endContainer) === block;
    const item = within && block.tagName === 'LI' ? block : null;

    if (item) {
        const saved = saveSelection(root);
        const list = outermostList(root, item);
        if (list && canReplace(list)) {
            const index = textBlocks(root).indexOf(item);
            const copy = list.cloneNode(true);
            const target = [...copy.querySelectorAll('li')][[...list.querySelectorAll('li')].indexOf(item)];
            const point = (container, offset) => {
                // The same place in the copy.
                const path = [];
                for (let node = container; node !== item; node = node.parentNode) {
                    path.unshift([...node.parentNode.childNodes].indexOf(node));
                }

                return [path.reduce((node, child) => node.childNodes[child], target), offset];
            };
            const startPoint = point(range.startContainer, range.startOffset);
            const tail = rangeBetween(point(range.endContainer, range.endOffset), ownEnd(target)).extractContents();
            rangeBetween(startPoint, ownEnd(target)).deleteContents();
            target.querySelector(':scope > br')?.remove();
            const nested = [...target.children].filter(isList);
            nested.forEach(child => child.remove());
            const items = [...middle, last].map(text => {
                const made = target.cloneNode(false);
                made.removeAttribute('data-rt-empty');
                made.textContent = text;
                return made;
            });
            target.append(first);
            items[items.length - 1].append(tail, ...nested);
            target.after(...items);
            if (replaceList(root, list, copy)) {
                const pasted = textBlocks(root)[index + paragraphs.length - 1];
                if (pasted) {
                    placeCaret(pointAt(pasted, last.length));
                }

                return;
            }

            restoreSelection(root, saved);
        }

        exec('insertText', paragraphs.join(' '));
        return;
    }

    if (GECKO && within) {
        // Firefox does not merge the first and last pasted paragraphs into the line as other browsers do, so the
        // whole block is replaced by the blocks wanted.
        const index = textBlocks(root).indexOf(block);
        const head = partOf(block, [block, 0], [range.startContainer, range.startOffset]);
        head.append(document.createTextNode(first));
        const tail = partOf(block, [range.endContainer, range.endOffset], ownEnd(block));
        tail.removeAttribute('data-key');
        tail.removeAttribute('data-rt-placeholder');
        head.removeAttribute('data-rt-placeholder');
        tail.prepend(document.createTextNode(last));
        const blocks = [head, ...middle.map(text => withText(document.createElement('p'), text)), tail];
        selectNode(block);
        exec('insertHTML', serialized(blocks));
        const pasted = textBlocks(root)[index + blocks.length - 1];
        if (pasted) {
            placeCaret(pointAt(pasted, last.length));
        }

        return;
    }

    exec('insertHTML', serialized(paragraphs.map(text => withText(document.createElement('p'), text))));
}

// Only plain text is ever taken from the clipboard.
export function pasteText(root, inline, text) {
    const paragraphs = paragraphsOf(text);
    let range = selectionIn(root);
    if (paragraphs.length === 0 || !range) {
        return;
    }

    exec('styleWithCSS', 'false');
    const single = inline || paragraphs.length === 1 ? paragraphs.join(' ') : null;
    const join = joinOf(root, range);
    if (join) {
        replaceJoin(root, join, single ?? '');
        if (single !== null) {
            return;
        }
    }

    if (single !== null) {
        exec('insertText', single);
        return;
    }

    range = selectionIn(root);
    if (!range.collapsed && textBlockOf(root, range.startContainer) !== textBlockOf(root, range.endContainer)) {
        exec('delete');
    }

    insertParagraphs(root, paragraphs);
}

// ---- Toolbar ----

// The surface each editor's toolbar acts on: the one that last had the focus.
const active = new WeakMap();

export function surfaceFocused(root) {
    const editor = root.closest('[data-rt-editor]');
    if (editor) {
        active.set(editor, root);
    }
}

// That surface, or null if the editor has none the toolbar may act on.
export function activeSurface(editor) {
    const found = editor ? active.get(editor) : null;
    return found?.isConnected ? found : null;
}

export function surfaceLeft(root) {
    const editor = root.closest('[data-rt-editor]');
    if (editor && active.get(editor) === root) {
        active.delete(editor);
    }
}

// The address .NET has accepted for exactly what the link box holds now, or null. .NET alone decides what may be
// linked: it puts both on the Apply button once it has checked what was typed.
function checkedAddress(area) {
    const apply = area.querySelector('[data-rt-link-apply]');
    const input = area.querySelector('[data-rt-link-input]');
    return apply && input && apply.getAttribute('data-rt-checked') === input.value
        ? apply.getAttribute('data-rt-address')
        : null;
}

// run(surfaceElement, name, argument) runs a command; restore(surfaceElement) gives it back focus and selection;
// link(surfaceElement, address) makes or removes a link and has the box closed.
export function wireToolbar(area, { run, restore, link }) {
    const toolbar = area.querySelector('[data-rt-toolbar]');
    const surface = () => activeSurface(area.closest('[data-rt-editor]'));
    // Where the browser can act at once, the event goes no further, so that .NET does not act on it as well. Otherwise
    // it reaches .NET, which checks the address and runs the command itself.
    const linkNow = (event, address) => {
        if (address !== null && surface()) {
            event.preventDefault();
            event.stopPropagation();
            link(surface(), address);
        }
    };
    area.addEventListener('click', event => {
        if (event.target.closest('[data-rt-link-apply]')) {
            linkNow(event, checkedAddress(area));
        } else if (event.target.closest('[data-rt-link-remove]')) {
            linkNow(event, '');
        }
    });
    area.addEventListener('keydown', event => {
        if (event.key === 'Enter' && !event.isComposing && event.target.matches('[data-rt-link-input]')) {
            linkNow(event, checkedAddress(area));
        }
    });
    // A press on a button must not take the selection from the text. A <select> cannot open without it.
    toolbar.addEventListener('mousedown', event => {
        if (event.target.closest('button')) {
            event.preventDefault();
        }
    });
    toolbar.addEventListener('click', event => {
        const button = event.target.closest('button[data-rt-command]');
        if (button && !button.disabled && surface()) {
            run(surface(), button.dataset.rtCommand, '');
        }
    });
    toolbar.addEventListener('change', event => {
        const list = event.target.closest('select[data-rt-command]');
        if (list && !list.disabled && surface()) {
            run(surface(), list.dataset.rtCommand, list.value);
        }
    });
    toolbar.addEventListener('keydown', event => {
        if (event.key === 'Escape' && surface()) {
            event.preventDefault();
            restore(surface());
            return;
        }

        // Left and right go along the toolbar, past the style list too: a closed list takes them as a new choice,
        // which would restyle the text and go back to it. Up and down are left to the list.
        const step = { ArrowLeft: -1, ArrowRight: 1 }[event.key];
        if (step && !event.altKey && ['BUTTON', 'SELECT'].includes(event.target.tagName)) {
            const controls = controlsOf(toolbar);
            const next = controls[(controls.indexOf(event.target) + step + controls.length) % controls.length];
            if (next) {
                event.preventDefault();
                next.focus();
            }
        }
    });
}

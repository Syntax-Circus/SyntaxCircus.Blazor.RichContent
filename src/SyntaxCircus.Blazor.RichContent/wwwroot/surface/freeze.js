// While an editor rearranges its content, parts of what it shows are about to be rewritten or removed, and what was
// typed there would be lost. So from the click that asks for it until the result has been drawn, nothing in the editor
// can be typed into. .NET only hears of the click, and the page keeps showing the old content until .NET's render
// arrives, which can be a long time when it is holding renders back.
//
// The release does not depend on what the click did. The script asks .NET to draw a number into the toolbar. .NET
// takes the two in order, so a page that shows the number has been drawn since the click was handled, whether that
// changed anything, was refused or did nothing. An ask that is lost or never answered is made again.

const FIRST_REPEAT = 500;
const LONGEST_REPEAT = 5000;
const DRAWN = 'data-rt-drawn';

// By the editor's element: only for editors whose toolbar has started and is still there. The editor's element stays
// when its mode changes, while its toolbar goes and comes back, so what is kept here belongs to a toolbar.
const editors = new WeakMap();
// The same states by their toolbar's own key, which can name a toolbar whose elements have already gone.
const toolbars = new Map();

// The element is an editor's only in Write mode, which is when it carries the attribute.
const editorOf = element => element.closest('[data-rt-editor]');

export function isFrozen(element) {
    const editor = editorOf(element);
    return editor !== null && editors.get(editor)?.frozen === true;
}

// Runs the callback now, or once the element's editor can be typed into again.
export function whenThawed(element, callback) {
    if (isFrozen(element)) {
        editors.get(editorOf(element)).waiting.push(callback);
    } else {
        callback();
    }
}

// The fields .NET renders: what is typed in one that is about to go would be lost too.
const FIELDS = 'textarea:not([data-rt-code]), input:not([type]), input[type="text"], input[type="url"], '
    + 'input[type="search"], input[type="number"], input[type="email"]';

// Scripted editors say for themselves whether they can be typed into. See their richContentRefresh.
function refresh(editor) {
    for (const element of editor.querySelectorAll('[data-rt-surface], [data-rt-code]')) {
        element.richContentRefresh?.();
    }
}

function thaw(editor, state) {
    clearTimeout(state.timer);
    state.frozen = false;
    for (const field of state.fields.splice(0)) {
        field.readOnly = false;
    }

    refresh(editor);
    for (const callback of state.waiting.splice(0)) {
        callback();
    }
}

// Forgets a toolbar's wiring: whatever it froze can be typed into again, and it listens and asks no more.
function unwire(state) {
    const { editor } = state;
    toolbars.delete(state.key);
    if (editors.get(editor) !== state) {
        return;
    }

    // Forgotten first, so that nothing counts as frozen while it is thawed.
    editors.delete(editor);
    delete editor.richContentFreeze;
    state.observer.disconnect();
    state.listeners.abort();
    thaw(editor, state);
}

// Whether the toolbar has gone from the page, or the editor has left Write mode.
const isGone = state => !state.area.isConnected || !state.editor.hasAttribute('data-rt-editor');

function thawIfDrawn(editor, state) {
    if (state.frozen && Number(state.area.getAttribute(DRAWN)) >= state.wanted) {
        thaw(editor, state);
    }
}

// Asks, and goes on asking, with growing pauses, until the page shows the answer.
function ask(editor, state, pause) {
    state.timer = setTimeout(() => {
        thawIfDrawn(editor, state);
        if (state.frozen && editor.isConnected) {
            // A failure is only waited out: the next ask is already due.
            new Promise(resolve => resolve(state.ask(state.wanted))).catch(() => {});
            ask(editor, state, pause === 0 ? FIRST_REPEAT : Math.min(pause * 2, LONGEST_REPEAT));
        }
    }, pause);
}

// Called in the click, before or after it is sent. With the connection down the click does not reach .NET, so
// nothing is about to be rearranged.
export function freeze(editor) {
    const state = editor ? editors.get(editor) : null;
    if (!state || state.down()) {
        return;
    }

    // A later click needs a later answer.
    state.wanted = ++state.last;
    state.frozen = true;
    for (const field of editor.querySelectorAll(FIELDS)) {
        if (!field.readOnly) {
            field.readOnly = true;
            state.fields.push(field);
        }
    }

    refresh(editor);
    clearTimeout(state.timer);
    // The first ask waits for this task to end, so that it is sent after the click.
    ask(editor, state, 0);
}

// area is the toolbar's, which .NET draws the numbers into, and key names that toolbar. redraw(number) asks it to.
// down() is true while the connection is known to be down. A button that rearranges the editor carries
// data-rt-rearrange. A later toolbar of the same editor takes over from an earlier one.
export function wireFreeze(area, key, redraw, down) {
    const editor = editorOf(area);
    if (!editor || !area.isConnected) {
        return;
    }

    const earlier = editors.get(editor);
    if (earlier) {
        unwire(earlier);
    }

    // Numbers go on from what the toolbar already shows.
    const last = Number(area.getAttribute(DRAWN)) || 0;
    // ask is replaceable, so that tests can lose it.
    const state = {
        editor, area, key, ask: redraw, down, last, wanted: 0, frozen: false, timer: null, fields: [], waiting: [],
        listeners: new AbortController(),
    };
    editors.set(editor, state);
    toolbars.set(key, state);
    // For tests.
    editor.richContentFreeze = state;
    state.observer = new MutationObserver(() => {
        // The page itself shows when the toolbar has gone, whether or not .NET's word of it has arrived or ever does.
        if (isGone(state)) {
            unwire(state);
        } else {
            thawIfDrawn(editor, state);
        }
    });
    state.observer.observe(area, { attributes: true, attributeFilter: [DRAWN] });
    state.observer.observe(editor, { attributes: true, attributeFilter: ['data-rt-editor'], childList: true });
    // A click is what a pointer, Enter and Space all end in. Captured, so it comes before anything else can stop it.
    editor.addEventListener('click', event => {
        const button = event.target.closest?.('[data-rt-rearrange]');
        if (button && !button.disabled && !isGone(state)) {
            freeze(editor);
        }
    }, { capture: true, signal: state.listeners.signal });
}

// For a toolbar that is being disposed. Once this has returned, nothing asks anything of it.
export function unwireFreeze(key) {
    const state = toolbars.get(key);
    if (state) {
        unwire(state);
    }
}

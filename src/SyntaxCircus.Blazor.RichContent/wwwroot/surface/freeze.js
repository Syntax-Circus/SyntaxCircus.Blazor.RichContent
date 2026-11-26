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

// By the editor's element: only for editors whose toolbar has started.
const editors = new WeakMap();

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

// area is the toolbar's, which .NET draws the numbers into. redraw(number) asks it to. down() is true while the
// connection is known to be down. A button that rearranges the editor carries data-rt-rearrange.
export function wireFreeze(area, redraw, down) {
    const editor = editorOf(area);
    if (!editor || editors.has(editor)) {
        return;
    }

    // Numbers go on from what the toolbar already shows.
    const last = Number(area.getAttribute(DRAWN)) || 0;
    // ask is replaceable, so that tests can lose it.
    const state = { area, ask: redraw, down, last, wanted: 0, frozen: false, timer: null, fields: [], waiting: [] };
    editors.set(editor, state);
    // For tests.
    editor.richContentFreeze = state;
    new MutationObserver(() => thawIfDrawn(editor, state)).observe(area, { attributes: true, attributeFilter: [DRAWN] });
    // A click is what a pointer, Enter and Space all end in. Captured, so it comes before anything else can stop it.
    editor.addEventListener('click', event => {
        const button = event.target.closest?.('[data-rt-rearrange]');
        if (button && !button.disabled) {
            freeze(editor);
        }
    }, true);
}

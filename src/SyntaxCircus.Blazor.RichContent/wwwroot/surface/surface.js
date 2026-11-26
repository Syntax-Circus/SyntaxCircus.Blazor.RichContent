// A writing surface: a contenteditable element whose edits are sent to a RichTextSurface component.

import { buildSurface, createReader } from './dom.js';
import { createTransport } from './transport.js';
import {
    activeSurface, handleBeforeInput, handleKeydown, pasteText, prepareComposition, runCommand, selectionIn,
    selectionState, surfaceFocused, surfaceLeft, wireToolbar,
} from './commands.js';
import { childAtEnd, wireInsert } from './insert.js';
import { freeze, isFrozen, unwireFreeze, whenThawed, wireFreeze } from './freeze.js';

const surfaces = new Map();

// The surface and state last sent to .NET, so that only a change is sent.
let reported = null;
let styled = false;

// The surface's own font, kept on it for the one thing that cannot inherit it. Firefox, when code is switched off at its
// end, writes the "plain" face inside the code, where all there is to inherit is the code font, and what is stored as
// plain text would look like code.
const SURFACE_FONT = '--_rt-font';

// The browser's commands decide what is bold, italic or code from how text looks, not from what the elements are. A
// heading, a quote or a link that a page showed bold or italic would count as formatted, and the command would take
// the formatting away instead of adding it; a bold element shown unbold would be wrapped again; and a font whose
// family a page reset would no longer be code. So in a surface those three looks follow the elements alone, whatever
// the page's own styles say, unless they too are !important and more specific.
// Only the surface itself is given a weight and a style; everything in it inherits them. Giving each kind of block its
// own makes Chromium and WebKit write those into style attributes on what moves from one block to another.
// Code is marked by its font alone, with no background or other box. Plain text can lie inside a code element, and
// nothing given to it could undo a box drawn around it. And Chromium and WebKit copy such a background onto what moves
// when lines are joined.
const SURFACE_STYLES = `
    [data-rt-surface] { font-weight: 400 !important; font-style: normal !important; }
    [data-rt-surface] :is(p, div, h2, h3, h4, ul, ol, li, blockquote, a, span, code, font) {
        font-weight: inherit !important; font-style: inherit !important;
    }
    [data-rt-surface] :is(b, strong) { font-weight: 700 !important; font-style: inherit !important; }
    [data-rt-surface] :is(i, em) { font-style: italic !important; font-weight: inherit !important; }
    [data-rt-surface] :is(code, font[face="rt-code"]) { font-family: "rt-code", monospace !important; }
    [data-rt-surface] font[face="rt-plain"] { font-family: inherit !important; }
    [data-rt-surface] :is(code, font[face^="rt-code"], font[face^='"rt-code"'], [style*="rt-code"]) font[face="rt-plain"] {
        font-family: var(${SURFACE_FONT}, sans-serif) !important;
    }
`;

// Taken anew each time the surface gets the focus, which anything that nests the faces needs, so that a host's change
// of font is followed. Set only when it differs, since setting it restyles everything in the surface.
function keepFont(element) {
    const font = getComputedStyle(element).fontFamily;
    if (element.style.getPropertyValue(SURFACE_FONT) !== font) {
        element.style.setProperty(SURFACE_FONT, font);
    }
}

// Made through the object model, which no content security policy refuses.
function addStyles() {
    if (styled) {
        return;
    }

    styled = true;
    const sheet = new CSSStyleSheet();
    sheet.replaceSync(SURFACE_STYLES);
    document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];
}

function release(surface) {
    surface.listeners.abort();
    surface.transport.dispose();
    surfaces.delete(surface.element);
    delete surface.element.richContentSurface;
    delete surface.element.richContentRefresh;
    delete surface.element.richContentSettle;
    hold(surface, false);
}

// An element that was removed can no longer be passed in, so its surface is found this way.
function releaseRemoved() {
    for (const surface of [...surfaces.values()]) {
        if (!surface.element.isConnected) {
            release(surface);
        }
    }
}

// Sends whatever differs from what was last sent. Synchronous, so that an edit leaves before any later click.
function flush(surface) {
    if (!surface.composing) {
        surface.transport.send(surface.reader.read());
    }
}

// The focus and the selection leave the element. The surface still knows where the selection was.
function withdraw(element) {
    if (element.contains(document.activeElement)) {
        document.activeElement.blur();
    }

    const selection = getSelection();
    for (let i = 0; i < selection.rangeCount; i++) {
        if (selection.getRangeAt(i).intersectsNode(element)) {
            selection.removeAllRanges();
            break;
        }
    }
}

// Takes a surface out of use for a while without touching contenteditable: turning that off and on again makes Firefox
// forget its undo history, and the author is to be able to undo afterwards. It is made inert instead. That alone is
// not enough, since Firefox goes on typing into an inert element that holds the selection, so the focus and the
// selection leave first. wasInert is what the element had, which is put back.
function hold(surface, on) {
    const { element } = surface;
    if (on) {
        withdraw(element);
        if (surface.wasInert === null) {
            surface.wasInert = element.inert;
            element.inert = true;
        }
    } else if (surface.wasInert !== null) {
        element.inert = surface.wasInert;
        surface.wasInert = null;
    }
}

const isHeld = surface => surface.wasInert !== null;

const isHistoryKey = event =>
    (event.ctrlKey || event.metaKey) && !event.altKey && ['z', 'y'].includes(event.key.toLowerCase());

// Whether a key pressed there could change what is in the surface's editor: anywhere in it, or nowhere at all.
function reaches(surface, target) {
    const editor = surface.element.closest('.doc-editor') ?? surface.element;
    return target === document.body || target === document.documentElement || editor.contains(target);
}

// Only a surface that holds a generation, having been drawn in full, can be typed into. One that is disabled is not
// contenteditable; one that is only to be left alone for a while, as during a rearrangement or a change of view, is
// held.
function applyState(surface) {
    const { element } = surface;
    const drawn = surface.generation !== null;
    const enabled = drawn && (!surface.disabled || surface.briefly);
    element.contentEditable = enabled ? 'true' : 'false';
    hold(surface, enabled && (surface.briefly || isFrozen(element)));
    element.setAttribute('aria-disabled', isEditable(surface) ? 'false' : 'true');
    if (drawn) {
        element.setAttribute('data-rt-ready', 'true');
    } else {
        element.removeAttribute('data-rt-ready');
    }
}

// The surface holds the new generation only once all of this has succeeded. If any of it throws, the surface holds
// none: whatever is on screen could not be delivered, so it takes no typing and sends nothing, .NET is never told
// that the redraw arrived, and the same redraw can be tried again.
function render(surface, keyedNodes, generation) {
    try {
        buildSurface(surface.element, keyedNodes, surface.inline);
        surface.reader.adopt();
        // The baseline is the DOM as read back, not the stored nodes, so what the reader normalises is not a change.
        surface.transport.reset(generation, surface.reader.read());
    } catch (error) {
        surface.generation = null;
        surface.transport.reset(null, []);
        applyState(surface);
        throw error;
    }

    surface.generation = generation;
    surface.saved = null;
    applyState(surface);
}

const RECONNECT_STATES = ['show', 'failed', 'rejected', 'paused', 'resume-failed'];

// Read from what Blazor does to its reconnect element, not from whether it can be seen, which is up to the host's
// styles. Wrongly believing the connection to be down would hold edits back for ever.
export function isReconnecting() {
    const element = document.getElementById('components-reconnect-modal');
    if (!element) {
        return false;
    }

    // Blazor's own element has a shadow root, and is shown and hidden with its display style.
    if (element.shadowRoot) {
        return element.style.display !== 'none';
    }

    // An element the host supplies has its state set on it as a class.
    return RECONNECT_STATES.some(state => element.classList.contains(`components-reconnect-${state}`));
}

// A call made while the connection is down never settles, and Blazor leaves the failed send behind it as an
// unhandled rejection. So no call is made then: it fails here, and what it carried is retried. Every call to .NET
// goes through here, the code card's too.
export function callDotNet(dotNetRef, method, ...args) {
    return isReconnecting()
        ? Promise.reject(new Error('The connection is down.'))
        : dotNetRef.invokeMethodAsync(method, ...args);
}

// Not while the editor is being rearranged: see freeze.js.
const isEditable = surface => surface.generation !== null && !surface.disabled && !isFrozen(surface.element);

// The author is in this surface (again): the toolbar acts on it.
function entered(surface) {
    surface.left = false;
    surfaceFocused(surface.element);
}

// The author has gone to work on something that is neither a surface nor the toolbar, such as a card. The toolbar must
// not go on acting on a place the author is not in, so it lets go of this one until the focus comes back.
function leave(surface) {
    surface.left = true;
    surfaceLeft(surface.element);
    if (reported?.surface === surface) {
        reported = null;
    }

    // With the connection down, .NET still shows the toolbar as it was. Its buttons do nothing here.
    callDotNet(surface.dotNetRef, 'SurfaceLeft').catch(() => {});
}

// What the author types or chooses in. A button does neither.
const FIELDS = 'textarea, select, input:not([type="button"], [type="submit"], [type="reset"], [type="image"]), '
    + '[contenteditable]:not([contenteditable="false"])';

// Called for each press and each move of the focus, anywhere in the page. Anything else in the editor is another place
// in the document, and so is a field anywhere. A button or bare page outside the editor, such as the host's Save, is
// not: after it, the toolbar still goes back to where the author was writing.
function leaveIfElsewhere(surface, target) {
    const editor = surface.element.closest('[data-rt-editor]');
    if (!editor || surface.left || activeSurface(editor) !== surface.element) {
        return;
    }

    if (!(target instanceof Element)) {
        return;
    }

    const inside = editor.contains(target);
    const part = target.closest('[data-rt-surface], [data-rt-toolbar-area]');
    // This editor's own parts: those of an editor nested in it, as in an extension block, are another place.
    if (inside ? part?.closest('[data-rt-editor]') !== editor : target.closest(FIELDS) !== null) {
        leave(surface);
    }
}

// Tells .NET the formatting at the cursor, if it or the surface with the cursor has changed.
function report(surface) {
    if (!isEditable(surface) || surface.composing) {
        return;
    }

    if (surface.left) {
        // The selection can still lie here. Only the focus shows that the author is back.
        if (document.activeElement !== surface.element) {
            return;
        }

        entered(surface);
    }

    const state = selectionState(surface.element);
    if (!state) {
        return;
    }

    const json = JSON.stringify(state);
    if (reported?.surface === surface && reported.json === json) {
        return;
    }

    const sent = { surface, json };
    reported = sent;
    // While the connection is down this fails at once. It is only forgotten, so that the next change sends it.
    callDotNet(surface.dotNetRef, 'SelectionChanged', state).catch(() => {
        if (reported === sent) {
            reported = null;
        }
    });
}

// The toolbar cannot be asked from here: it may not yet have heard of the cursor. The state is sent first, and .NET
// takes the two in order.
function requestLink(surface) {
    report(surface);
    // With the connection down there is no box to open.
    callDotNet(surface.dotNetRef, 'LinkRequested').catch(() => {});
}

// Gives the surface the focus and the selection it last had. False if it cannot take a command, as when the author
// has since gone elsewhere.
function activate(surface) {
    if (!isEditable(surface) || surface.composing || surface.left) {
        return false;
    }

    const { element, saved } = surface;
    if (document.activeElement !== element) {
        element.focus({ preventScroll: true });
        if (saved && element.contains(saved.startContainer) && element.contains(saved.endContainer)) {
            getSelection().removeAllRanges();
            getSelection().addRange(saved.cloneRange());
        }
    }

    return selectionIn(element) !== null;
}

// What a command changes is sent as it changes, in this task.
function run(surface, name, argument) {
    if (!activate(surface)) {
        return false;
    }

    const ran = runCommand(surface.element, surface.inline, name, argument);
    report(surface);
    return ran;
}

function compositionChanged(surface, composing) {
    // Only advice to .NET; there is nothing to do if it does not arrive.
    callDotNet(surface.dotNetRef, 'CompositionChanged', composing).catch(() => {});
}

function endComposition(surface) {
    if (!surface.composing) {
        return;
    }

    surface.composing = false;
    const pending = surface.pending;
    surface.pending = null;
    try {
        if (pending) {
            // The content was replaced while the author was composing, and the replacement wins.
            render(surface, pending.keyedNodes, pending.generation);
        } else {
            flush(surface);
        }
    } finally {
        compositionChanged(surface, false);
    }
}

// Whatever is on screen and not known to be in the document is sent now, text still being composed included. For
// when the surface may be about to stop taking input, or to go: what is sent now reaches .NET before whatever asked.
function settle(surface) {
    const outstanding = surface.transport.unacknowledged > 0;
    endComposition(surface);
    if (outstanding) {
        surface.transport.settle();
    }
}

// Safe to call again for the same element, as .NET does when it cannot tell that an earlier call arrived: the
// surface it has is kept, with whatever has been typed in it.
export function attach(element, dotNetRef, options) {
    releaseRemoved();
    const attached = surfaces.get(element);
    if (attached) {
        attached.dotNetRef = dotNetRef;
        return;
    }

    const inline = options?.profile === 'inline';
    const surface = {
        element,
        dotNetRef,
        inline,
        composing: false,
        pending: null,
        // The selection when it was last in the surface.
        saved: null,
        // True once the author has gone elsewhere, until the focus comes back.
        left: false,
        // Of the content shown. Null until it has been drawn in full.
        generation: null,
        disabled: false,
        // Disabled only until a change of view is made or refused.
        briefly: false,
        wasInert: null,
        listeners: new AbortController(),
        reader: createReader(element, inline),
    };
    surface.transport = createTransport({
        invoke: (sequence, index, count, chunk) =>
            callDotNet(surface.dotNetRef, 'ReceiveEdit', sequence, index, count, chunk),
        snapshot: () => {
            if (!element.isConnected) {
                release(surface);
                return null;
            }

            return surface.composing ? null : surface.reader.read();
        },
        complete: inline,
    });

    const on = (target, type, handler, capture = false) =>
        target.addEventListener(type, handler, { signal: surface.listeners.signal, capture });

    on(element, 'input', event => {
        if (event.isComposing) {
            return;
        }

        // An input that is not part of a composition means one has ended, whether or not that was announced.
        if (surface.composing) {
            endComposition(surface);
        } else {
            flush(surface);
        }
    });
    on(element, 'compositionstart', () => {
        if (!surface.composing && isEditable(surface)) {
            prepareComposition(element);
        }

        surface.composing = true;
        compositionChanged(surface, true);
    });
    on(element, 'compositionend', () => {
        if (surface.composing) {
            endComposition(surface);
        } else {
            // It was settled while the composition went on, and the input method has had its last word since.
            flush(surface);
        }
    });
    on(element, 'focusout', event => {
        if (!element.contains(event.relatedTarget)) {
            endComposition(surface);
        }
    });
    on(element, 'focusin', () => {
        keepFont(element);
        entered(surface);
        // Otherwise some browsers make a <div> on Enter.
        document.execCommand('defaultParagraphSeparator', false, 'p');
        // The focus can come back with the selection as it was, which changes nothing that would be reported.
        queueMicrotask(() => {
            if (surfaces.get(element) === surface && document.activeElement === element) {
                report(surface);
            }
        });
    });
    on(document, 'selectionchange', () => {
        const range = selectionIn(element);
        if (range) {
            surface.saved = range.cloneRange();
            report(surface);
        }
    });

    on(element, 'keydown', event => {
        if (isEditable(surface)) {
            handleKeydown(element, inline, event, {
                run: name => run(surface, name, ''),
                changed: () => report(surface),
                linkWanted: () => requestLink(surface),
            });
        }
    });
    // Firefox undoes into a held surface from the keyboard all the same, with the focus elsewhere or nowhere. And some
    // builds of WebKit take Backspace outside a field to mean the page before, which is where the focus is left when it
    // is withdrawn: the author, still typing, would leave the page.
    on(document, 'keydown', event => {
        const nowhere = event.target === document.body || event.target === document.documentElement;
        if (isHeld(surface) && ((isHistoryKey(event) && reaches(surface, event.target))
            || (event.key === 'Backspace' && nowhere))) {
            event.preventDefault();
        }
    }, true);
    on(element, 'beforeinput', event => {
        if (isHeld(surface) && event.inputType.startsWith('history')) {
            event.preventDefault();
        } else if (isEditable(surface)) {
            handleBeforeInput(element, inline, event);
        }
    });
    // What the clipboard holds besides plain text never reaches the page.
    on(element, 'paste', event => {
        event.preventDefault();
        if (isEditable(surface) && !surface.composing) {
            pasteText(element, inline, event.clipboardData?.getData('text/plain') ?? '');
            report(surface);
        }
    });
    // Cancelling the drop is not enough: the browser still takes the move to have happened, and deletes what was
    // dragged from where it came, here or elsewhere. So nothing in a surface starts a drag, and a surface tells a drag
    // from elsewhere that it takes nothing.
    on(element, 'dragstart', event => event.preventDefault());
    on(element, 'dragover', event => {
        event.preventDefault();
        if (event.dataTransfer) {
            event.dataTransfer.dropEffect = 'none';
        }
    });
    on(element, 'drop', event => event.preventDefault());

    // A link in a surface is text to edit, never somewhere to go.
    const stay = event => {
        if (event.target instanceof Element && event.target.closest('a')) {
            event.preventDefault();
        }
    };
    on(element, 'click', stay);
    on(element, 'auxclick', stay);

    on(document, 'pointerdown', event => leaveIfElsewhere(surface, event.target), true);
    on(document, 'focusin', event => leaveIfElsewhere(surface, event.target), true);

    // Capture on the document runs before the event reaches what it is for, such as a Save button.
    on(document, 'pointerdown', () => surface.transport.nudge(), true);
    on(document, 'keydown', () => surface.transport.nudge(), true);

    addStyles();
    element.style.whiteSpace = 'pre-wrap';
    element.style.overflowWrap = 'break-word';
    if (inline) {
        // An empty table cell would otherwise have no height to click. A <br> to hold it open could not be clicked.
        element.style.minHeight = '1.5em';
    }
    surfaces.set(element, surface);
    // For tests.
    element.richContentSurface = surface;
    element.richContentRefresh = () => applyState(surface);
    element.richContentSettle = () => settle(surface);
}

// With force, redraws the surface from keyedNodes ([{ key, node }]) of the given generation, unless it already
// has that generation: .NET sends a redraw again when it cannot tell that it arrived, and by then the author may
// have typed. Without force, the content is left alone and only disabled is applied. briefly says that disabled is
// only for the time a change of view takes. Returns the generation the surface then has, which is null if there is no
// surface or none has been drawn, or if it is disabled and what it holds may not have gone. Throws if drawing fails.
// data-rt-ready is set while the surface has drawn content.
export function sync(element, keyedNodes, disabled, force, generation, briefly = false) {
    const surface = element && surfaces.get(element);
    if (!surface) {
        return null;
    }

    if (disabled) {
        // Before it stops taking input. A change of view starts this way, and the surface goes with the view. Also
        // when a redraw comes with it: it may be one the surface drew long ago, and has been typed in since.
        settle(surface);
    }

    surface.disabled = disabled;
    surface.briefly = disabled && briefly;
    if (force && generation !== surface.generation) {
        if (surface.composing) {
            // Rewriting the element now would break the composition.
            surface.pending = { keyedNodes, generation };
        } else {
            render(surface, keyedNodes, generation);
        }
    }

    applyState(surface);
    // While the connection still shows as down, what was just sent again has not gone. So .NET is not answered that
    // the surface is read-only, and asks again.
    return disabled && isReconnecting() ? null : surface.generation;
}

// Runs a writing command where the surface's selection is, or last was: bold, italic, code, link (with an address,
// or empty to remove it), style (p, h2, h3, h4), bullets, numbers or quote. Returns whether it ran. A surface that
// is disabled, or holds no drawn content, ignores every command.
export function command(element, name, argument = '') {
    const surface = element && surfaces.get(element);
    return surface ? run(surface, name, argument) : false;
}

// Gives the surface the focus again, with the selection it last had.
export function restore(element) {
    const surface = element && surfaces.get(element);
    if (surface) {
        whenThawed(element, () => {
            if (surfaces.get(element) === surface && activate(surface)) {
                report(surface);
            }
        });
    }
}

// Makes or removes a link as the link box asked, with an address .NET has already accepted, then has .NET close the
// box. Called in the click or key press, so that the edit leaves before anything the author clicks next.
function link(element, address) {
    const surface = surfaces.get(element);
    // Where the command does not run, as while the editor is rearranged or text is composed, the box stays open with
    // the address.
    if (command(element, 'link', address)) {
        // With the connection down the box stays open.
        callDotNet(surface.dotNetRef, 'LinkApplied').catch(() => {});
    }
}

// Asks .NET to insert a choice of the Insert menu where the cursor was last in this surface. Called in the click, with
// everything typed already sent or sent here first, so that .NET splits what it has.
function insert(element, choice) {
    const surface = surfaces.get(element);
    // With the connection down nothing is inserted, and nothing else changes.
    if (!surface || !isEditable(surface) || surface.composing || surface.left || isReconnecting()) {
        return;
    }

    const range = selectionIn(element) ?? surface.saved;
    if (!range || !element.contains(range.endContainer)) {
        return;
    }

    flush(surface);
    const after = surface.reader.keyThrough(childAtEnd(element, range));
    // This surface is about to be rewritten, or the author's attention to go to what is inserted. So nothing more is
    // typed here, or anywhere in the editor until the result is drawn, and the toolbar lets go of it.
    leave(surface);
    element.blur();
    freeze(element.closest('[data-rt-editor]'));
    // Read last: freezing sends again what may not have got through.
    callDotNet(surface.dotNetRef, 'InsertRequested', choice, after, surface.transport.newest).catch(() => {});
}

// The toolbar areas already wired.
const toolbarAreas = new WeakSet();

// area holds the toolbar and, while it is open, the link box. toolbarRef is the toolbar's component, and key names it
// to detachToolbar. Safe to call again for the same area.
export function attachToolbar(area, toolbarRef, key) {
    if (area && !toolbarAreas.has(area)) {
        toolbarAreas.add(area);
        wireFreeze(area, key, number => callDotNet(toolbarRef, 'Redraw', number), isReconnecting);
        wireToolbar(area, { run: command, restore, link });
        wireInsert(area, choice => {
            const element = activeSurface(area.closest('[data-rt-editor]'));
            if (element) {
                insert(element, choice);
            }
        });
    }
}

// The toolbar is being disposed. What it listens to on its own elements goes with them.
export function detachToolbar(key) {
    unwireFreeze(key);
}

export function dispose(element) {
    const surface = element && surfaces.get(element);
    if (surface) {
        release(surface);
    }

    releaseRemoved();
}

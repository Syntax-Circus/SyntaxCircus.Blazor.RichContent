// A writing surface: a contenteditable element whose edits are sent to a RichTextSurface component.

import { buildSurface, createReader } from './dom.js';
import { createTransport } from './transport.js';
import {
    handleBeforeInput, handleKeydown, pasteText, prepareComposition, runCommand, selectionIn, selectionState,
    surfaceFocused, wireToolbar,
} from './commands.js';

const surfaces = new Map();

// The surface and state last sent to .NET, so that only a change is sent.
let reported = null;
let styled = false;

// What the commands depend on, whatever styles the host has. A heading that looked bold would count as bold, and the
// bold command would then take boldness away instead of adding it; inline code has no look of its own. Made through
// the object model, which no content security policy refuses.
function addStyles() {
    if (styled) {
        return;
    }

    styled = true;
    const sheet = new CSSStyleSheet();
    sheet.replaceSync('[data-rt-surface] :is(h2, h3, h4) { font-weight: 400; }'
        + ' [data-rt-surface] font[face="rt-code"] { background: rgba(127, 127, 127, 0.18); }');
    document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];
}

function release(surface) {
    surface.listeners.abort();
    surface.transport.dispose();
    surfaces.delete(surface.element);
    delete surface.element.richContentSurface;
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

// Only a surface that holds a generation, having been drawn in full, can be typed into.
function applyState(surface) {
    const { element } = surface;
    const drawn = surface.generation !== null;
    const editable = drawn && !surface.disabled;
    element.contentEditable = editable ? 'true' : 'false';
    element.setAttribute('aria-disabled', editable ? 'false' : 'true');
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
function isReconnecting() {
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
// unhandled rejection. So no call is made then: it fails here, and what it carried is retried.
function callDotNet(dotNetRef, method, ...args) {
    return isReconnecting()
        ? Promise.reject(new Error('The connection is down.'))
        : dotNetRef.invokeMethodAsync(method, ...args);
}

const isEditable = surface => surface.generation !== null && !surface.disabled;

// Tells .NET the formatting at the cursor, if it or the surface with the cursor has changed.
function report(surface) {
    if (!isEditable(surface) || surface.composing) {
        return;
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

// Gives the surface the focus and the selection it last had. False if it cannot take a command.
function activate(surface) {
    if (!isEditable(surface) || surface.composing) {
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

export function attach(element, dotNetRef, options) {
    releaseRemoved();
    if (surfaces.has(element)) {
        release(surfaces.get(element));
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
        // Of the content shown. Null until it has been drawn in full.
        generation: null,
        disabled: false,
        listeners: new AbortController(),
        reader: createReader(element, inline),
    };
    surface.transport = createTransport({
        invoke: (sequence, index, count, chunk) =>
            callDotNet(dotNetRef, 'ReceiveEdit', sequence, index, count, chunk),
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
    on(element, 'compositionend', () => endComposition(surface));
    on(element, 'focusout', event => {
        if (!element.contains(event.relatedTarget)) {
            endComposition(surface);
        }
    });
    on(element, 'focusin', () => {
        surfaceFocused(element);
        // Otherwise some browsers make a <div> on Enter.
        document.execCommand('defaultParagraphSeparator', false, 'p');
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
    on(element, 'beforeinput', event => {
        if (isEditable(surface)) {
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
    const refuse = event => event.preventDefault();
    on(element, 'dragover', refuse);
    on(element, 'drop', refuse);

    // A link in a surface is text to edit, never somewhere to go.
    const stay = event => {
        if (event.target instanceof Element && event.target.closest('a')) {
            event.preventDefault();
        }
    };
    on(element, 'click', stay);
    on(element, 'auxclick', stay);

    // Capture on the document runs before the event reaches what it is for, such as a Save button.
    on(document, 'pointerdown', () => surface.transport.nudge(), true);
    on(document, 'keydown', () => surface.transport.nudge(), true);

    addStyles();
    element.style.whiteSpace = 'pre-wrap';
    element.style.overflowWrap = 'break-word';
    surfaces.set(element, surface);
    // For tests.
    element.richContentSurface = surface;
}

// With force, redraws the surface from keyedNodes ([{ key, node }]) of the given generation, unless it already
// has that generation: .NET sends a redraw again when it cannot tell that it arrived, and by then the author may
// have typed. Without force, the content is left alone and only disabled is applied. Returns the generation the
// surface then has, which is null if there is no surface or none has been drawn. Throws if drawing fails.
// data-rt-ready is set while the surface has drawn content.
export function sync(element, keyedNodes, disabled, force, generation) {
    const surface = element && surfaces.get(element);
    if (!surface) {
        return null;
    }

    surface.disabled = disabled;
    if (force && generation !== surface.generation) {
        if (surface.composing) {
            // Rewriting the element now would break the composition.
            surface.pending = { keyedNodes, generation };
        } else {
            render(surface, keyedNodes, generation);
        }
    }

    applyState(surface);
    return surface.generation;
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
    if (surface && activate(surface)) {
        report(surface);
    }
}

export function attachToolbar(toolbar) {
    if (toolbar) {
        wireToolbar(toolbar, command, restore);
    }
}

export function dispose(element) {
    const surface = element && surfaces.get(element);
    if (surface) {
        release(surface);
    }

    releaseRemoved();
}

// A writing surface: a contenteditable element whose edits are sent to a RichTextSurface component.

import { buildSurface, createReader } from './dom.js';
import { createTransport } from './transport.js';

const surfaces = new Map();

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

function render(surface, keyedNodes, generation) {
    surface.generation = generation;
    buildSurface(surface.element, keyedNodes, surface.inline);
    surface.reader.adopt();
    // The baseline is the DOM as read back, not the stored nodes, so what the reader normalises is not a change.
    surface.transport.reset(generation, surface.reader.read());
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
    if (pending) {
        // The content was replaced while the author was composing, and the replacement wins.
        render(surface, pending.keyedNodes, pending.generation);
    } else {
        flush(surface);
    }

    compositionChanged(surface, false);
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
        // Of the content shown. Null until the first redraw.
        generation: null,
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
        surface.composing = true;
        compositionChanged(surface, true);
    });
    on(element, 'compositionend', () => endComposition(surface));
    on(element, 'focusout', event => {
        if (!element.contains(event.relatedTarget)) {
            endComposition(surface);
        }
    });
    // Otherwise some browsers make a <div> on Enter.
    on(element, 'focusin', () => document.execCommand('defaultParagraphSeparator', false, 'p'));

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

    element.style.whiteSpace = 'pre-wrap';
    element.style.overflowWrap = 'break-word';
    surfaces.set(element, surface);
    // For tests.
    element.richContentSurface = surface;
}

// With force, redraws the surface from keyedNodes ([{ key, node }]) of the given generation, unless it already
// has that generation: .NET sends a redraw again when it cannot tell that it arrived, and by then the author may
// have typed. Without force, the content is left alone and only disabled is applied. Returns the generation the
// surface then has, or null if there is no surface.
export function sync(element, keyedNodes, disabled, force, generation) {
    const surface = element && surfaces.get(element);
    if (!surface) {
        return null;
    }

    element.contentEditable = disabled ? 'false' : 'true';
    element.setAttribute('aria-disabled', disabled ? 'true' : 'false');

    if (force && generation !== surface.generation) {
        if (surface.composing) {
            // Rewriting the element now would break the composition.
            surface.pending = { keyedNodes, generation };
        } else {
            render(surface, keyedNodes, generation);
        }
    }

    element.setAttribute('data-rt-ready', 'true');
    return surface.generation;
}

// Toolbar commands are not available yet. Returns whether the command ran.
export function command() {
    return false;
}

export function dispose(element) {
    const surface = element && surfaces.get(element);
    if (surface) {
        release(surface);
    }

    releaseRemoved();
}

// A code card's text area, whose edits are sent to a CodeCard component as a writing surface's are: patches in small
// messages, kept and sent again until .NET has them (see transport.js). The one entry of a patch is the whole text.
//
// Its text is only ever set as the element's value, and the browser's own editing, undo included, does the rest.

import { createTransport } from './transport.js';
import { callDotNet } from './surface.js';

const INDENT = '    ';

const cards = new Map();

const entriesOf = element => [{ key: 'code', node: element.value, signature: element.value }];

function release(card) {
    card.listeners.abort();
    card.transport.dispose();
    cards.delete(card.element);
    delete card.element.richContentCode;
}

// An element that was removed can no longer be passed in, so its card is found this way.
function releaseRemoved() {
    for (const card of [...cards.values()]) {
        if (!card.element.isConnected) {
            release(card);
        }
    }
}

// Sends the text if it differs from what was last sent. Synchronous, so that an edit leaves before any later click.
function flush(card) {
    if (!card.composing) {
        card.transport.send(entriesOf(card.element));
    }
}

const isEditable = card => card.generation !== null && !card.disabled;

// Only a text area that has been filled can be typed into. Anything else it holds could not be delivered.
function applyState(card) {
    const { element } = card;
    element.readOnly = !isEditable(card);
    element.setAttribute('aria-disabled', isEditable(card) ? 'false' : 'true');
    if (card.generation !== null) {
        element.setAttribute('data-rt-ready', 'true');
    } else {
        element.removeAttribute('data-rt-ready');
    }
}

// The card holds the new generation only once all of this has succeeded. If any of it throws, it holds none: it
// takes no typing and sends nothing, .NET is never told that the redraw arrived, and it can be tried again.
function render(card, text, generation) {
    try {
        card.element.value = text;
        // Read back, since the element makes every line break a line feed. That alone is not an edit.
        card.transport.reset(generation, entriesOf(card.element));
    } catch (error) {
        card.generation = null;
        card.transport.reset(null, []);
        applyState(card);
        throw error;
    }

    card.generation = generation;
    applyState(card);
}

function compositionChanged(card, composing) {
    // Only advice to .NET; there is nothing to do if it does not arrive.
    callDotNet(card.dotNetRef, 'CompositionChanged', composing).catch(() => {});
}

function endComposition(card) {
    if (!card.composing) {
        return;
    }

    card.composing = false;
    const pending = card.pending;
    card.pending = null;
    try {
        if (pending) {
            // The text was replaced while the author was composing, and the replacement wins.
            render(card, pending.text, pending.generation);
        } else {
            flush(card);
        }
    } finally {
        compositionChanged(card, false);
    }
}

// Through the browser's own command, so that it can be undone like any typing.
function indent(element) {
    if (!document.execCommand('insertText', false, INDENT)) {
        element.setRangeText(INDENT, element.selectionStart, element.selectionEnd, 'end');
        element.dispatchEvent(new InputEvent('input', { bubbles: true }));
    }
}

// Tab indents, so that Tab alone cannot leave. After Escape, the next Tab does what it always does.
function handleKeydown(card, event) {
    const { key } = event;
    if (event.isComposing) {
        return;
    }

    if (key === 'Escape') {
        card.leaving = true;
        return;
    }

    if (key === 'Shift') {
        // On the way to Shift+Tab.
        return;
    }

    const plain = key === 'Tab' && !event.shiftKey && !event.ctrlKey && !event.metaKey && !event.altKey;
    if (plain && !card.leaving && isEditable(card)) {
        event.preventDefault();
        indent(card.element);
    }

    card.leaving = false;
}

export function attach(element, dotNetRef) {
    releaseRemoved();
    if (cards.has(element)) {
        release(cards.get(element));
    }

    const card = {
        element,
        dotNetRef,
        composing: false,
        pending: null,
        leaving: false,
        // Of the text shown. Null until it has been filled.
        generation: null,
        disabled: false,
        listeners: new AbortController(),
    };
    card.transport = createTransport({
        invoke: (sequence, index, count, chunk) => callDotNet(dotNetRef, 'ReceiveEdit', sequence, index, count, chunk),
        snapshot: () => {
            if (!element.isConnected) {
                release(card);
                return null;
            }

            return card.composing ? null : entriesOf(element);
        },
        // The one entry is all there is, so every patch can say everything.
        complete: true,
    });

    const on = (target, type, handler, capture = false) =>
        target.addEventListener(type, handler, { signal: card.listeners.signal, capture });

    on(element, 'input', event => {
        if (event.isComposing) {
            return;
        }

        // An input that is not part of a composition means one has ended, whether or not that was announced.
        if (card.composing) {
            endComposition(card);
        } else {
            flush(card);
        }
    });
    on(element, 'compositionstart', () => {
        card.composing = true;
        compositionChanged(card, true);
    });
    on(element, 'compositionend', () => endComposition(card));
    on(element, 'blur', () => {
        card.leaving = false;
        endComposition(card);
    });
    on(element, 'keydown', event => handleKeydown(card, event));

    // Capture on the document runs before the event reaches what it is for, such as a Save button.
    on(document, 'pointerdown', () => card.transport.nudge(), true);
    on(document, 'keydown', () => card.transport.nudge(), true);

    cards.set(element, card);
    // For tests.
    element.richContentCode = card;
}

// With force, sets the text of the given generation, unless the card already has that generation: .NET sends a
// redraw again when it cannot tell that it arrived, and by then the author may have typed. Without force, the text is
// left alone and only disabled is applied. Returns the generation the card then has, which is null if there is no
// card or it has not been filled. Throws if setting the text fails.
export function sync(element, text, disabled, force, generation) {
    const card = element && cards.get(element);
    if (!card) {
        return null;
    }

    card.disabled = disabled;
    if (force && generation !== card.generation) {
        if (card.composing) {
            card.pending = { text, generation };
        } else {
            render(card, text, generation);
        }
    }

    applyState(card);
    return card.generation;
}

export function focus(element) {
    const card = element && cards.get(element);
    if (card && isEditable(card)) {
        element.focus();
    }
}

export function dispose(element) {
    const card = element && cards.get(element);
    if (card) {
        release(card);
    }

    releaseRemoved();
}

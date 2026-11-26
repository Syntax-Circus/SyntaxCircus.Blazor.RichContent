// "Add too much text": puts more into a writing area than the editor can send, so that the unsaved-text warning can
// be tried. It types as an author would and uses nothing of the editor's own script.
//
// Listeners are on the document, because Blazor draws and replaces the elements.
(() => {
    const AREAS = '[data-rt-surface], textarea[data-rt-code]';
    const BUTTON = '#demo-too-much';

    // 2,200,000 characters: more than the 2 MiB that one edit may carry.
    const text = () => 'word '.repeat(440_000);

    let lastArea = null;

    document.addEventListener('focusin', event => {
        if (event.target.matches?.(AREAS)) {
            lastArea = event.target;
        }
    });

    // Keeps the caret where the author left it.
    document.addEventListener('mousedown', event => {
        if (event.target.closest?.(BUTTON)) {
            event.preventDefault();
        }
    });

    const canTypeIn = area =>
        area !== null && area.isConnected && area.closest('[inert]') === null &&
        (area.matches('textarea') ? !area.readOnly && !area.disabled : area.getAttribute('contenteditable') === 'true');

    const say = message => {
        const result = document.getElementById('demo-too-much-result');
        if (result) {
            result.textContent = message;
        }
    };

    // A message about one editor must not stay beside another, or after the editor is disabled or enabled.
    document.addEventListener('change', event => {
        if (event.target.matches?.('#demo-sample, #demo-disabled')) {
            say('');
        }
    });

    document.addEventListener('click', event => {
        if (event.target.closest?.('#demo-reset')) {
            say('');
            return;
        }

        if (!event.target.closest?.(BUTTON)) {
            return;
        }

        if (document.getElementById('demo-disabled')?.checked) {
            say('Enable the editor first.');
            return;
        }

        if (!canTypeIn(lastArea)) {
            say('Click in a writing area in Write view first.');
            return;
        }

        say('Adding text…');

        // Lets the message be drawn before the browser is busy.
        setTimeout(() => {
            if (!canTypeIn(lastArea)) {
                say('Click in a writing area in Write view first.');
                return;
            }

            if (document.activeElement !== lastArea) {
                lastArea.focus();
            }

            say(document.execCommand('insertText', false, text())
                ? 'Added 2,200,000 characters. The editor should now report unsaved text.'
                : 'The browser did not accept the text.');
        }, 50);
    });
})();

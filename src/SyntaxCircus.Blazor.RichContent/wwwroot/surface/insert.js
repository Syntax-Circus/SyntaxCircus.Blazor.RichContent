// Insert, in the browser. Where the cursor is gets read in the click itself, after what was typed has been sent, so
// that .NET splits content it already has.

// The surface's own child that holds the end of the range, or null if the range ends before them all.
export function childAtEnd(root, range) {
    let node = range.endContainer;
    let offset = range.endOffset;
    // A selection that ends at the very start of a line, as a triple click leaves one, does not reach into it.
    if (!range.collapsed && offset === 0) {
        let before = node;
        while (before !== root && !before.previousSibling) {
            before = before.parentNode;
        }

        if (before !== root) {
            node = before.previousSibling;
            offset = 0;
        }
    }

    if (node === root) {
        return offset > 0 ? root.childNodes[offset - 1] : null;
    }

    while (node.parentNode !== root) {
        node = node.parentNode;
    }

    return node;
}

// area holds the toolbar. insert(index) is told which choice of the Insert menu was taken.
export function wireInsert(area, insert) {
    area.addEventListener('click', event => {
        const choice = event.target.closest('button[data-rt-insert]');
        if (choice && !choice.disabled) {
            // The event goes no further: all of it is done here, and in the order the script sends it.
            event.preventDefault();
            event.stopPropagation();
            insert(Number(choice.getAttribute('data-rt-insert')));
        }
    });
}

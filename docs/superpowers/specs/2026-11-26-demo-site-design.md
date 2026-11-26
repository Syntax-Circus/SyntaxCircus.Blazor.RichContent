# Demo site design

## Intent

A local app for trying the editor the way an author would, by hand or through browser automation, to find problems the
automated tests miss.

## Goals

- `dotnet run --project samples/RichContent.Demo` is enough to start it: fixed address `http://localhost:5199`, with a
  launch profile that sets the Development environment so static assets load.
- One page with the editor, a live preview, the saved JSON and the current validation errors.
- A choice of sample documents: empty, every block type, a long post, and content the editor does not support.
  Choosing one, or "Reset", replaces the document.
- Settings:
  - disable or enable the editor;
  - lower the content limits, which produces validation errors;
  - add too much text: puts more than 2 MiB of text into the writing area the author last used, which the editor
    cannot send, so the unsaved-text warning and everything that depends on it can be tried;
  - light or dark page theme.
- Switching between Write and Blocks uses the editor's own switch. The page shows which view the editor reports.
- Every control has a visible label and a stable `id`, and the page shows when it has become interactive, so that
  browser automation does not act too early.
- The README's "Sample" section describes both apps and how to run the demo.

## Non-goals

- Hosting it anywhere, packaging it, or storing documents. A reload starts again.
- Changing the library, the existing sample app or the browser tests. A library problem found with the demo is
  reported and handled separately.
- Simulating a lost connection or a strict Content Security Policy. The browser tests cover both.

## Constraints

- It uses the library's public API only, as a host would.
- Hub options stay at their defaults, so the demo has the same message limits as a host that changes nothing.
- It builds with the solution, with warnings as errors, and is not packable.
- This is a public repository: sample content is made up and names no private application.

## Approaches

| Approach | Trade-off | |
| --- | --- | --- |
| A new demo project | Free to change without breaking test pages; a little sample code is repeated | **Chosen** |
| Grow the current sample | One app, but demo changes can break the browser tests | |
| New demo, and rename the current sample as a test app | Clearest names, but changes the tests and project setup | |

It goes on `feat/inline-writing-editor`, before the pull request. Every push to `main` runs the publish job, so adding
the demo later would publish another version without library changes.

## Calls made while writing this

- The demo has its own media picker, `project-card` block and sample documents instead of sharing the current
  sample's. They are small, and sharing them would tie the demo to what the tests expect.
- It has no automated tests of its own. The solution build in CI shows it still compiles, and a walk through every
  control in a real browser checks it works.
- "Add too much text" needs a few lines of script in the demo. It types into the editor as an author would, and does
  not call the library's internals.

## Left to the builder

Layout, wording, the sample text and the component breakdown.

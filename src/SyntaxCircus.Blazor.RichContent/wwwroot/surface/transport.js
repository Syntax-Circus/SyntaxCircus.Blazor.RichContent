// Delivers a surface's edits to .NET as patches, in messages small enough for SignalR's default limit.
//
// A patch is { generation, order, changed, partial }: every top-level key now in the surface, and the nodes of the
// keys that are new or differ from the last patch sent. partial is true unless it also carries everything not yet
// known to have been applied; while .NET has an edit it rejected, it takes no partial one. .NET answers true (applied, or from a replaced generation) or false
// (rejected: the content cannot be converted). A call that throws did not get through.
//
// Whatever is not acknowledged may or may not have reached .NET. So after a failure or a rejection, the next patch
// is cumulative: it also sends every key that any unacknowledged patch changed, removed or added.
//   - A failure is retried, with a growing delay, until it succeeds or the surface is disposed.
//   - A rejection is not retried. The next change sends a cumulative patch.
//   - A call that never settles (the connection dropped under it) is sent again once it has waited too long.

const CHUNK_CHARACTERS = 4000;
const FIRST_RETRY = 250;
const LONGEST_RETRY = 5000;
const FIRST_STALL = 3000;
const LONGEST_STALL = 30000;
// A click or key press resends what has waited this long, so it is on its way before the event is.
const STALE = 2000;

// Sequence numbers must only increase for the life of a .NET component, so they are shared by the whole module.
let lastSequence = 0;

// Never splits a surrogate pair: half of one cannot be sent.
function chunksOf(payload) {
    const chunks = [];
    for (let start = 0; start < payload.length;) {
        let end = Math.min(start + CHUNK_CHARACTERS, payload.length);
        const last = payload.charCodeAt(end - 1);
        if (end < payload.length && last >= 0xd800 && last <= 0xdbff) {
            end--;
        }

        chunks.push(payload.slice(start, end));
        start = end;
    }

    return chunks;
}

const sameOrder = (a, b) => a.length === b.length && a.every((key, index) => key === b[index]);

// invoke(sequence, index, count, chunk) calls ReceiveEdit. snapshot() reads the surface, or returns null when it
// must not be read now (during composition). complete makes every patch list all of its nodes.
export function createTransport({ invoke, snapshot, complete = false }) {
    let generation = null;
    let epoch = 0;
    let lastSent = new Map();
    let lastOrder = [];
    // Patches not yet known to be covered, oldest first: { sequence, cumulative, touched, status, sentAt }.
    let records = [];
    let retryTimer = null;
    let retryDelay = FIRST_RETRY;
    let stallTimer = null;
    let stallDelay = FIRST_STALL;
    let disposed = false;
    // The sequence number of the newest patch sent since the last redraw, or 0.
    let newest = 0;

    const clearTimers = () => {
        clearTimeout(retryTimer);
        clearTimeout(stallTimer);
        retryTimer = stallTimer = null;
        retryDelay = FIRST_RETRY;
        stallDelay = FIRST_STALL;
    };

    // True when a failed patch has nothing newer and cumulative on its way.
    const needsRetry = () => {
        const failed = records.findLast(record => record.status === 'failed');
        return failed !== undefined && !records.some(record =>
            record.cumulative && record.status === 'pending' && record.sequence > failed.sequence);
    };

    const hasStalled = age => {
        const now = performance.now();
        const pending = records.filter(record => record.status === 'pending');
        return pending.length > 0 && now - pending[0].sentAt >= age
            && !pending.some(record => record.cumulative && now - record.sentAt < age);
    };

    // Sends the surface's current state as a cumulative patch, changed or not.
    const sendNow = () => {
        const entries = disposed ? null : snapshot();
        if (entries) {
            send(entries, true);
        }

        return entries !== null;
    };

    const scheduleRetry = () => {
        if (disposed || retryTimer !== null) {
            return;
        }

        retryTimer = setTimeout(() => {
            retryTimer = null;
            if (needsRetry() && !sendNow()) {
                scheduleRetry();
            }
        }, retryDelay);
        retryDelay = Math.min(retryDelay * 2, LONGEST_RETRY);
    };

    const watchForStall = () => {
        if (disposed || stallTimer !== null || !records.some(record => record.status === 'pending')) {
            return;
        }

        stallTimer = setTimeout(() => {
            stallTimer = null;
            if (hasStalled(stallDelay) && sendNow()) {
                stallDelay = Math.min(stallDelay * 2, LONGEST_STALL);
            }

            watchForStall();
        }, stallDelay);
    };

    const settle = (record, results) => {
        if (!records.includes(record)) {
            return; // a later cumulative patch has covered it
        }

        record.status = results.some(result => result.status === 'rejected') ? 'failed'
            : results.some(result => result.value === false) ? 'rejected'
            : 'ok';

        // A cumulative patch carries everything the earlier ones did, so it alone now says what is outstanding.
        if (record.cumulative) {
            records = records.filter(other => other.sequence >= record.sequence);
        }

        if (record.status === 'ok') {
            retryDelay = FIRST_RETRY;
            stallDelay = FIRST_STALL;
            if (record.cumulative) {
                records = records.filter(other => other !== record);
            }

            // An acknowledged delta says nothing about an earlier patch that was not.
            while (records[0]?.status === 'ok') {
                records.shift();
            }
        } else if (record.status === 'failed') {
            scheduleRetry();
        } else if (!record.cumulative
            && records.some(other => other.sequence < record.sequence && other.status !== 'ok')
            && !records.some(other => other.cumulative && other.sequence > record.sequence)) {
            // It may only have been rejected for building on a patch .NET does not have. Say everything once. A
            // rejected cumulative patch starts nothing, so this cannot loop.
            sendNow();
        }
    };

    const send = (entries, forced) => {
        if (disposed || generation === null) {
            return;
        }

        const order = entries.map(entry => entry.key);
        const differing = entries.filter(entry => lastSent.get(entry.key) !== entry.signature).map(entry => entry.key);
        if (!forced && differing.length === 0 && sameOrder(order, lastOrder)) {
            return;
        }

        const cumulative = forced || records.some(record => record.status === 'failed' || record.status === 'rejected');
        const present = new Set(order);
        const changed = new Set(complete ? order : differing);
        const touched = new Set();
        if (cumulative) {
            for (const record of records) {
                for (const key of record.touched) {
                    touched.add(key);
                    if (present.has(key)) {
                        changed.add(key);
                    }
                }
            }
        }

        changed.forEach(key => touched.add(key));
        // Removed from the order, or added to it.
        lastOrder.filter(key => !present.has(key)).forEach(key => touched.add(key));
        order.filter(key => !lastSent.has(key)).forEach(key => touched.add(key));

        lastOrder = order;
        lastSent = new Map(entries.map(entry => [entry.key, entry.signature]));

        const chunks = chunksOf(JSON.stringify({
            generation,
            order,
            changed: entries.filter(entry => changed.has(entry.key)).map(({ key, node }) => ({ key, node })),
            partial: !(cumulative || complete),
        }));
        const sequence = ++lastSequence;
        newest = sequence;
        const record = { sequence, cumulative, touched, status: 'pending', sentAt: performance.now() };
        records.push(record);

        // Every chunk is invoked now, in this task. Waiting between them would let a later click overtake the edit.
        const calls = chunks.map((chunk, index) =>
            new Promise(resolve => resolve(transport.invoke(sequence, index, chunks.length, chunk))));
        const sentIn = epoch;
        Promise.allSettled(calls).then(results => {
            if (!disposed && sentIn === epoch) {
                settle(record, results);
            }
        });
        watchForStall();
    };

    const transport = {
        // Replaceable, so that tests can lose messages.
        invoke,

        get newest() {
            return newest;
        },

        // How many patches are not yet known to have got through. For tests.
        get unacknowledged() {
            return records.length;
        },

        // The surface was redrawn: entries are what it now shows, which .NET already has.
        reset(newGeneration, entries) {
            epoch++;
            generation = newGeneration;
            lastOrder = entries.map(entry => entry.key);
            lastSent = new Map(entries.map(entry => [entry.key, entry.signature]));
            records = [];
            newest = 0;
            clearTimers();
        },

        // Sends what differs from the last patch, if anything does.
        send(entries) {
            send(entries, false);
        },

        // Called when what is here may be about to go, as before a rearrangement or a change of view: sends again,
        // now, whatever is not yet known to have got through, however recently it was sent.
        settle() {
            if (records.length > 0) {
                sendNow();
            }
        },

        // Called before a click or key press is handled: resends anything that may not have got through.
        nudge() {
            if (needsRetry() || hasStalled(STALE)) {
                sendNow();
            }
        },

        dispose() {
            disposed = true;
            clearTimers();
        },
    };
    return transport;
}

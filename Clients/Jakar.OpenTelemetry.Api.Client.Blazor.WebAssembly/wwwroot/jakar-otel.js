// Jakar.OpenTelemetry browser module: error/vitals capture, DOM screenshots, and durable browser storage for WebAssembly apps.

const state = {
    dotnet: null,
    listeners: [],
    observers: [],
    budget: { windowStart: 0, count: 0 },
    options: { maxEventsPerMinute: 30, reportWebVitals: true },
    vitals: { cls: 0, inp: 0, reported: false }
};

// ---------------------------------------------------------------- error & vitals capture

export function install(dotnetRef, options) {
    uninstall();
    state.dotnet = dotnetRef;
    state.options = Object.assign({ maxEventsPerMinute: 30, reportWebVitals: true }, options || {});

    listen(window, 'error', onError, true);
    listen(window, 'unhandledrejection', onRejection);
    listen(document, 'securitypolicyviolation', e => report('csp', {
        message: `CSP blocked ${e.blockedURI || 'inline'} (${e.violatedDirective})`,
        directive: e.violatedDirective,
        blockedUri: e.blockedURI,
        source: e.sourceFile,
        line: e.lineNumber
    }));
    listen(document, 'visibilitychange', () => {
        if (document.visibilityState !== 'hidden') { return; }
        reportPendingVitals();
        invoke('FlushNow');
    });

    if (state.options.reportWebVitals) { observeVitals(); }
}

export function uninstall() {
    for (const [target, type, handler, capture] of state.listeners) { target.removeEventListener(type, handler, capture); }
    for (const observer of state.observers) { observer.disconnect(); }
    state.listeners = [];
    state.observers = [];
    state.dotnet = null;
}

function listen(target, type, handler, capture = false) {
    target.addEventListener(type, handler, capture);
    state.listeners.push([target, type, handler, capture]);
}

function onError(event) {
    const target = event.target;

    // Resource load failures (img/script/link) do not bubble; they are seen here because the listener captures.
    if (target && target !== window && target.tagName) {
        report('resource', {
            message: `Failed to load ${target.tagName.toLowerCase()} ${target.src || target.href || ''}`,
            element: target.tagName.toLowerCase(),
            resourceUrl: stripQuery(target.src || target.href || '')
        });
        return;
    }

    const error = event.error;
    report('error', {
        type: (error && error.name) || 'Error',
        message: event.message || (error && error.message) || 'Unknown script error',
        stack: error && error.stack,
        source: stripQuery(event.filename || ''),
        line: event.lineno,
        column: event.colno
    });
}

function onRejection(event) {
    const reason = event.reason;
    report('unhandledrejection', {
        type: (reason && reason.name) || 'UnhandledRejection',
        message: (reason && reason.message) || String(reason),
        stack: reason && reason.stack
    });
}

function observeVitals() {
    observe('largest-contentful-paint', list => {
        const entries = list.getEntries();
        const last = entries[entries.length - 1];
        if (last) { state.vitals.lcp = last.startTime; }
    });

    observe('layout-shift', list => {
        for (const entry of list.getEntries()) {
            if (!entry.hadRecentInput) { state.vitals.cls += entry.value; }
        }
    });

    observe('event', list => {
        for (const entry of list.getEntries()) {
            if (entry.interactionId) { state.vitals.inp = Math.max(state.vitals.inp, entry.duration); }
        }
    }, { durationThreshold: 40 });

    observe('paint', list => {
        for (const entry of list.getEntries()) {
            if (entry.name === 'first-contentful-paint') { vital('FCP', entry.startTime, 1800, 3000); }
        }
    });

    observe('longtask', list => {
        for (const entry of list.getEntries()) {
            if (entry.duration >= 200) { vital('LongTask', entry.duration, 200, 500); }
        }
    });

    const navigation = performance.getEntriesByType && performance.getEntriesByType('navigation')[0];
    if (navigation) { vital('TTFB', navigation.responseStart, 800, 1800); }
}

function observe(type, callback, extra) {
    try {
        const observer = new PerformanceObserver(callback);
        observer.observe(Object.assign({ type, buffered: true }, extra || {}));
        state.observers.push(observer);
    } catch {
        // entry type not supported by this browser
    }
}

function reportPendingVitals() {
    if (state.vitals.reported) { return; }
    state.vitals.reported = true;
    if (state.vitals.lcp) { vital('LCP', state.vitals.lcp, 2500, 4000); }
    vital('CLS', state.vitals.cls, 0.1, 0.25);
    if (state.vitals.inp) { vital('INP', state.vitals.inp, 200, 500); }
}

function vital(name, value, good, poor) {
    const rating = value <= good ? 'good' : value <= poor ? 'needs-improvement' : 'poor';
    invoke('OnBrowserEvent', 'vital', JSON.stringify({ name, value: Math.round(value * 1000) / 1000, rating, url: stripQuery(location.href) }));
}

function report(kind, payload) {
    // Error storms (e.g. an exception in an animation frame) must not flood the log or the network.
    const now = Date.now();
    if (now - state.budget.windowStart > 60000) { state.budget = { windowStart: now, count: 0 }; }
    if (++state.budget.count > state.options.maxEventsPerMinute) { return; }

    payload.url = stripQuery(location.href);
    payload.userAgent = navigator.userAgent;
    invoke('OnBrowserEvent', kind, JSON.stringify(payload));
}

function invoke(method, ...args) {
    const dotnet = state.dotnet;
    if (!dotnet) { return; }
    try { dotnet.invokeMethodAsync(method, ...args).catch(() => { }); } catch { /* circuit gone */ }
}

function stripQuery(url) {
    const index = (url || '').search(/[?#]/);
    return index >= 0 ? url.substring(0, index) : (url || '');
}

// ---------------------------------------------------------------- screenshots

/**
 * Best-effort screenshot of the visible viewport without third-party libraries: the DOM (with same-origin CSS inlined and form values preserved)
 * is rendered through an SVG foreignObject onto a canvas. Cross-origin images, iframes and canvases render blank; returns null if the browser refuses.
 */
export async function captureScreenshot(maxWidth) {
    try {
        const root = document.documentElement;
        const width = Math.max(root.clientWidth, 1);
        const height = Math.max(window.innerHeight, 1);
        const scale = Math.min(1, (maxWidth || 1600) / width);

        let css = '';
        for (const sheet of Array.from(document.styleSheets)) {
            try { for (const rule of Array.from(sheet.cssRules)) { css += rule.cssText + '\n'; } } catch { /* cross-origin stylesheet */ }
        }

        const clone = root.cloneNode(true);
        syncFormValues(root, clone);
        clone.querySelectorAll('script, iframe, noscript, link[rel="stylesheet"]').forEach(node => node.remove());

        const style = document.createElement('style');
        style.textContent = css;
        (clone.querySelector('head') || clone).appendChild(style);

        const markup = new XMLSerializer().serializeToString(clone);
        const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}">` +
                    `<foreignObject x="0" y="${-window.scrollY}" width="${width}" height="${Math.max(root.scrollHeight, height)}">${markup}</foreignObject></svg>`;

        const image = new Image();
        image.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg);
        await image.decode();

        const canvas = document.createElement('canvas');
        canvas.width = Math.round(width * scale);
        canvas.height = Math.round(height * scale);

        const context = canvas.getContext('2d');
        context.fillStyle = getComputedStyle(document.body).backgroundColor || '#ffffff';
        context.fillRect(0, 0, canvas.width, canvas.height);
        context.scale(scale, scale);
        context.drawImage(image, 0, 0);

        const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/png'));
        return blob ? new Uint8Array(await blob.arrayBuffer()) : null;
    } catch {
        return null;
    }
}

function syncFormValues(source, target) {
    const sourceFields = source.querySelectorAll('input, textarea, select');
    const targetFields = target.querySelectorAll('input, textarea, select');

    sourceFields.forEach((field, index) => {
        const copy = targetFields[index];
        if (!copy) { return; }

        if (field.type === 'password') { copy.setAttribute('value', '********'); }
        else if (field.type === 'checkbox' || field.type === 'radio') { if (field.checked) { copy.setAttribute('checked', ''); } else { copy.removeAttribute('checked'); } }
        else if (field.tagName === 'TEXTAREA') { copy.textContent = field.value; }
        else if (field.tagName === 'SELECT') { Array.from(copy.options).forEach((option, i) => option.toggleAttribute('selected', field.options[i] && field.options[i].selected)); }
        else { copy.setAttribute('value', field.value); }
    });
}

// ---------------------------------------------------------------- durable storage (WebAssembly)

const DATABASE = 'jakar-otel';
const IMAGES = 'images';
let database = null;

function openDatabase() {
    if (database) { return database; }

    database = new Promise((resolve, reject) => {
        const request = indexedDB.open(DATABASE, 1);
        request.onupgradeneeded = () => request.result.createObjectStore(IMAGES, { keyPath: 'id' });
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => { database = null; reject(request.error); };
    });

    return database;
}

async function run(mode, action) {
    const db = await openDatabase();
    return new Promise((resolve, reject) => {
        const transaction = db.transaction(IMAGES, mode);
        const result = action(transaction.objectStore(IMAGES));
        transaction.oncomplete = () => resolve(result && 'result' in result ? result.result : undefined);
        transaction.onerror = () => reject(transaction.error);
        transaction.onabort = () => reject(transaction.error);
    });
}

export function imagePut(id, meta, bytes) { return run('readwrite', store => store.put({ id, meta, data: bytes })); }

export function imageUpdate(id, meta) {
    return openDatabase().then(db => new Promise((resolve, reject) => {
        const transaction = db.transaction(IMAGES, 'readwrite');
        const store = transaction.objectStore(IMAGES);
        const request = store.get(id);
        request.onsuccess = () => { if (request.result) { request.result.meta = meta; store.put(request.result); } };
        transaction.oncomplete = () => resolve();
        transaction.onerror = () => reject(transaction.error);
    }));
}

export async function imageMetas() {
    const records = await run('readonly', store => store.getAll());
    return (records || []).map(record => record.meta);
}

export async function imageGet(id) {
    const record = await run('readonly', store => store.get(id));
    return record ? record.data : null;
}

export function imageDelete(id) { return run('readwrite', store => store.delete(id)); }

export function storageGet(key) {
    try { return localStorage.getItem(key); } catch { return null; }
}

export function storageSet(key, value) {
    try {
        if (value === null || value === undefined) { localStorage.removeItem(key); } else { localStorage.setItem(key, value); }
        return true;
    } catch {
        return false; // quota exceeded or storage disabled
    }
}

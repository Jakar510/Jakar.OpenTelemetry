// Column dividers: any element with data-resize="--a-css-variable" sets that variable (a width in px) on <html>, so every element sized by it follows,
// whichever component renders it. Drag with the mouse or touch, nudge with the arrow keys, double-click to go back to the default.
// Widths are remembered in this browser; nothing goes to the server, and it works the same after Blazor re-renders.
(() => {
    const KEY = "jakar.otel.columnWidths";
    const MIN = 64;
    const MAX = 1200;
    const root = document.documentElement;
    const isVariable = name => /^--[a-z0-9-]+$/i.test(name);

    let saved = {};
    try { saved = JSON.parse(localStorage.getItem(KEY) || "{}") || {}; } catch { saved = {}; }

    for (const [name, value] of Object.entries(saved)) {
        if (isVariable(name) && /^\d+(\.\d+)?px$/.test(value)) { root.style.setProperty(name, value); }
    }

    const save = () => {
        try { localStorage.setItem(KEY, JSON.stringify(saved)); } catch { /* private mode: widths last until the page closes */ }
    };

    const apply = (name, width) => {
        const value = `${Math.round(Math.min(MAX, Math.max(MIN, width)))}px`;
        root.style.setProperty(name, value);
        saved[name] = value;
    };

    const handleOf = target => target instanceof Element ? target.closest("[data-resize]") : null;

    // The column's current width is that of the element holding the handle (its header cell).
    const widthOf = handle => (handle.parentElement ?? handle).getBoundingClientRect().width;

    document.addEventListener("pointerdown", event => {
        const handle = handleOf(event.target);
        if (!handle || event.button !== 0 || !isVariable(handle.dataset.resize)) { return; }

        const name = handle.dataset.resize;
        const startX = event.clientX;
        const startWidth = widthOf(handle);

        event.preventDefault();
        handle.setPointerCapture(event.pointerId);
        handle.classList.add("dragging");

        const move = e => apply(name, startWidth + e.clientX - startX);
        const end = () => {
            handle.classList.remove("dragging");
            handle.removeEventListener("pointermove", move);
            handle.removeEventListener("pointerup", end);
            handle.removeEventListener("pointercancel", end);
            save();
        };

        handle.addEventListener("pointermove", move);
        handle.addEventListener("pointerup", end);
        handle.addEventListener("pointercancel", end);
    });

    document.addEventListener("keydown", event => {
        const handle = handleOf(event.target);
        if (!handle || !isVariable(handle.dataset.resize) || (event.key !== "ArrowLeft" && event.key !== "ArrowRight")) { return; }

        event.preventDefault();
        apply(handle.dataset.resize, widthOf(handle) + (event.key === "ArrowRight" ? 16 : -16));
        save();
    });

    document.addEventListener("dblclick", event => {
        const handle = handleOf(event.target);
        if (!handle || !isVariable(handle.dataset.resize)) { return; }

        root.style.removeProperty(handle.dataset.resize);
        delete saved[handle.dataset.resize];
        save();
    });
})();

const connections = new WeakMap();

export function connect(list, callback) {
    const controller = new AbortController();
    const options = { signal: controller.signal };
    let dragged;
    let target;
    let after;

    function clearTarget() {
        target?.classList.remove("drop-before", "drop-after");
        target = null;
    }

    function finish() {
        clearTarget();
        dragged?.classList.remove("dragging");
        dragged = null;
    }

    list.addEventListener("dragstart", event => {
        const handle = event.target.closest("[data-queue-drag]");
        if (!handle) return;
        dragged = handle.closest("[data-queue-item]");
        dragged.classList.add("dragging");
        event.dataTransfer.effectAllowed = "move";
        event.dataTransfer.setData("text/plain", dragged.dataset.queueItem);
    }, options);

    list.addEventListener("dragover", event => {
        if (!dragged) return;
        const row = event.target.closest("[data-queue-item]");
        if (!row || row === dragged) {
            clearTarget();
            return;
        }
        event.preventDefault();
        event.dataTransfer.dropEffect = "move";
        const rect = row.getBoundingClientRect();
        const insertAfter = event.clientY > rect.top + rect.height / 2;
        if (row !== target || after !== insertAfter) {
            clearTarget();
            target = row;
            after = insertAfter;
            target.classList.add(after ? "drop-after" : "drop-before");
        }
    }, options);

    list.addEventListener("dragleave", event => {
        if (!list.contains(event.relatedTarget)) clearTarget();
    }, options);

    list.addEventListener("drop", event => {
        const row = event.target.closest("[data-queue-item]");
        if (dragged && row && row === target) {
            event.preventDefault();
            callback.invokeMethodAsync("DropTask", dragged.dataset.queueItem, row.dataset.queueItem, after);
        }
        finish();
    }, options);

    list.addEventListener("dragend", finish, options);
    connections.set(list, controller);
}

export function disconnect(list) {
    connections.get(list)?.abort();
    connections.delete(list);
}

export function focus(workspace, details, taskId) {
    const selector = details ? "[data-task-heading]" : `#queue-task-${taskId}`;
    const target = workspace.querySelector(selector) ?? workspace.querySelector("#queue-title");
    target?.focus();
}

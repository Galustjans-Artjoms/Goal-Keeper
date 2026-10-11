document.querySelectorAll(".puzzle").forEach((puzzle) => {
    const board = puzzle.querySelector(".puzzle-board");
    const piece = puzzle.querySelector(".puzzle-piece");
    const slider = puzzle.querySelector(".puzzle-slider");
    const hidden = puzzle.querySelector("input[name=captcha]");
    const max = Number(slider.max);
    const pieceSize = Number(puzzle.dataset.pieceSize || 64);
    const vertical = puzzle.dataset.axis === "y";

    const apply = (value) => {
        const offset = Math.max(0, Math.min(max, Math.round(Number(value))));
        slider.value = String(offset);
        hidden.value = String(offset);
        piece.style.transform = vertical ? `translateY(${offset}px)` : `translateX(${offset}px)`;
    };

    slider.addEventListener("input", () => apply(slider.value));

    let dragging = false;
    const fromPointer = (event) => {
        const rect = board.getBoundingClientRect();
        const raw = vertical
            ? event.clientY - rect.top - pieceSize / 2
            : event.clientX - rect.left - pieceSize / 2;
        apply(raw);
    };

    board.addEventListener("pointerdown", (event) => {
        dragging = true;
        board.setPointerCapture(event.pointerId);
        fromPointer(event);
    });
    board.addEventListener("pointermove", (event) => {
        if (dragging) {
            fromPointer(event);
        }
    });
    board.addEventListener("pointerup", () => {
        dragging = false;
    });
    board.addEventListener("pointercancel", () => {
        dragging = false;
    });

    apply(0);
});

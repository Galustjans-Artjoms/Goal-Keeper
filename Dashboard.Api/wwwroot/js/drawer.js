(function () {
    const rail = document.getElementById("sideRail");
    const drawer = document.getElementById("drawer");
    if (!rail || !drawer) {
        return;
    }

    const buttons = Array.from(rail.querySelectorAll("[data-panel]"));
    const panels = Array.from(drawer.querySelectorAll("[data-drawer-panel]"));
    const title = document.getElementById("drawerTitle");
    let current = null;

    const setOpen = (panel) => {
        current = panel;
        const open = Boolean(panel);
        document.body.classList.toggle("drawer-open", open);
        buttons.forEach((button) => {
            const name = button.getAttribute("data-panel");
            const active = name === panel || (panel === "roadmap" && name === "analysis");
            button.classList.toggle("is-active", active);
            button.setAttribute("aria-expanded", active ? "true" : "false");
        });
        panels.forEach((node) => {
            node.hidden = node.getAttribute("data-drawer-panel") !== panel;
        });
        if (title && panel) {
            const labels = {
                analysis: "Analysis",
                roadmap: "Roadmap",
                goals: "Goals",
                op: "OP",
                advanced: "Advanced"
            };
            title.textContent = labels[panel] || (panel.charAt(0).toUpperCase() + panel.slice(1));
        }
    };

    buttons.forEach((button) => {
        button.addEventListener("click", () => {
            const panel = button.getAttribute("data-panel");
            if (current === "roadmap" && panel === "analysis") {
                setOpen("analysis");
                return;
            }
            if (current === panel) {
                setOpen(null);
            } else {
                setOpen(panel);
            }
        });
    });

    drawer.querySelectorAll("[data-close]").forEach((close) => {
        close.addEventListener("click", () => setOpen(null));
    });

    drawer.querySelectorAll("[data-open-roadmap]").forEach((btn) => {
        btn.addEventListener("click", () => setOpen("roadmap"));
    });

    drawer.querySelectorAll("[data-roadmap-back]").forEach((btn) => {
        btn.addEventListener("click", () => setOpen("analysis"));
    });

    document.addEventListener("keydown", (event) => {
        if (event.key !== "Escape") {
            return;
        }
        if (current === "roadmap") {
            setOpen("analysis");
        } else {
            setOpen(null);
        }
    });

    const boot = document.body.getAttribute("data-open-panel");
    if (boot) {
        setOpen(boot);
    }
})();

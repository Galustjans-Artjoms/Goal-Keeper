const canvas = document.getElementById("particles");
if (canvas) {
    const context = canvas.getContext("2d");
    const dots = Array.from({ length: 28 }, () => ({
        x: Math.random(),
        y: Math.random(),
        vx: (Math.random() - 0.5) * 0.00012,
        vy: (Math.random() - 0.5) * 0.00012,
        r: 0.6 + Math.random() * 1.1
    }));

    const resize = () => {
        canvas.width = window.innerWidth;
        canvas.height = window.innerHeight;
    };
    resize();
    window.addEventListener("resize", resize);

    const frame = () => {
        context.clearRect(0, 0, canvas.width, canvas.height);
        for (const dot of dots) {
            dot.x += dot.vx;
            dot.y += dot.vy;
            if (dot.x < 0 || dot.x > 1) {
                dot.vx *= -1;
            }
            if (dot.y < 0 || dot.y > 1) {
                dot.vy *= -1;
            }
        }

        for (const dot of dots) {
            context.fillStyle = "rgba(232, 244, 255, 0.45)";
            context.beginPath();
            context.arc(dot.x * canvas.width, dot.y * canvas.height, dot.r, 0, Math.PI * 2);
            context.fill();
        }

        requestAnimationFrame(frame);
    };

    frame();
}

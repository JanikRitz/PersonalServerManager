export function initialize(target) {
    let start = null;
    let wheelLock = 0;

    const getStage = element => element instanceof Element ? element.closest('.feed-stage') : null;
    const isInteractive = element => element instanceof Element && !!element.closest('a, button, select, input, textarea, label, [data-no-swipe]');

    const resetTransform = stage => {
        const media = stage?.querySelector('.feed-media-wrap');
        if (!media) return;
        media.style.transition = 'transform 180ms ease-out';
        media.style.transform = '';
    };

    const touchStart = event => {
        start = null;
        if (event.touches.length !== 1 || !(event.target instanceof Element)) return;
        const stage = getStage(event.target);
        if (!stage || isInteractive(event.target)) return;
        const touch = event.touches[0];
        const media = stage.querySelector('.feed-media-wrap');
        if (media) media.style.transition = 'none';
        start = { stage, x: touch.clientX, y: touch.clientY, dy: 0, moved: false };
    };

    const touchMove = event => {
        if (!start || event.touches.length !== 1) return;
        const touch = event.touches[0];
        const dx = touch.clientX - start.x;
        const dy = touch.clientY - start.y;
        if (Math.abs(dy) > 10 && Math.abs(dy) > Math.abs(dx)) {
            start.moved = true;
            start.dy = dy;
            event.preventDefault();
            const media = start.stage.querySelector('.feed-media-wrap');
            if (media) {
                const clamped = Math.max(-140, Math.min(140, dy * 0.45));
                media.style.transform = `translateY(${clamped}px)`;
            }
        }
    };

    const touchEnd = event => {
        if (!start || event.changedTouches.length !== 1) {
            if (start) resetTransform(start.stage);
            start = null;
            return;
        }
        const touch = event.changedTouches[0];
        const dx = touch.clientX - start.x;
        const dy = touch.clientY - start.y;
        const stage = start.stage;
        const valid = stage.isConnected && Math.abs(dy) >= 48 && Math.abs(dy) > Math.abs(dx) * 1.2;
        resetTransform(stage);
        start = null;
        if (valid) {
            target.invokeMethodAsync('SwipeAsync', dy < 0).catch(() => {});
        }
    };

    const touchCancel = () => {
        if (start) resetTransform(start.stage);
        start = null;
    };

    const wheel = event => {
        if (!(event.target instanceof Element)) return;
        const stage = getStage(event.target);
        if (!stage || isInteractive(event.target)) return;
        if (Math.abs(event.deltaY) < 28 || Math.abs(event.deltaY) <= Math.abs(event.deltaX)) return;
        const now = Date.now();
        if (now < wheelLock) return;
        wheelLock = now + 380;
        event.preventDefault();
        target.invokeMethodAsync('SwipeAsync', event.deltaY > 0).catch(() => {});
    };

    document.addEventListener('touchstart', touchStart, { passive: true });
    document.addEventListener('touchmove', touchMove, { passive: false });
    document.addEventListener('touchend', touchEnd, { passive: true });
    document.addEventListener('touchcancel', touchCancel, { passive: true });
    document.addEventListener('wheel', wheel, { passive: false });

    return {
        dispose() {
            document.removeEventListener('touchstart', touchStart);
            document.removeEventListener('touchmove', touchMove);
            document.removeEventListener('touchend', touchEnd);
            document.removeEventListener('touchcancel', touchCancel);
            document.removeEventListener('wheel', wheel);
        }
    };
}

export function syncVideo(video, muted) {
    if (!(video instanceof HTMLVideoElement)) return false;
    video.muted = !!muted;
    if (video.paused) {
        video.play().catch(() => {});
    }
    return video.paused;
}

export function setMuted(video, muted) {
    if (!(video instanceof HTMLVideoElement)) return;
    video.muted = !!muted;
}

export function togglePlay(video) {
    if (!(video instanceof HTMLVideoElement)) return false;
    if (video.paused) {
        video.play().catch(() => {});
        return false;
    }
    video.pause();
    return true;
}
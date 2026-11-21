export function initialize(target) {
    let start = null;
    const touchStart = event => {
        start = null;
        if (event.touches.length !== 1 || !(event.target instanceof Element)) return;
        const stage = event.target.closest('.media-stage');
        if (!stage || event.target.closest('a, button')) return;
        const touch = event.touches[0];
        const video = event.target.closest('video');
        if (video && touch.clientY >= video.getBoundingClientRect().bottom - 80) return;
        start = { stage, x: touch.clientX, y: touch.clientY };
    };
    const touchEnd = event => {
        if (!start || event.changedTouches.length !== 1) { start = null; return; }
        const horizontal = event.changedTouches[0].clientX - start.x;
        const vertical = event.changedTouches[0].clientY - start.y;
        const valid = start.stage.isConnected && Math.abs(horizontal) >= 60 && Math.abs(horizontal) > Math.abs(vertical) * 1.5;
        start = null;
        if (valid) target.invokeMethodAsync('SwipeAsync', horizontal < 0).catch(() => {});
    };
    const touchCancel = () => { start = null; };
    document.addEventListener('touchstart', touchStart, { passive: true });
    document.addEventListener('touchend', touchEnd, { passive: true });
    document.addEventListener('touchcancel', touchCancel, { passive: true });
    return { dispose() {
        document.removeEventListener('touchstart', touchStart);
        document.removeEventListener('touchend', touchEnd);
        document.removeEventListener('touchcancel', touchCancel);
    } };
}
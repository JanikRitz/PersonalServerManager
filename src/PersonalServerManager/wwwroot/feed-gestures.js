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
            clearScrubber();
            clearPreloads();
        }
    };
}

let activeScrubberCleanup = null;
const preloadedMedia = new Map();

function formatTime(seconds) {
    if (!Number.isFinite(seconds) || seconds < 0) return '0:00';
    const total = Math.floor(seconds);
    const hours = Math.floor(total / 3600);
    const minutes = Math.floor((total % 3600) / 60);
    const secs = total % 60;
    const pad = value => String(value).padStart(2, '0');
    return hours > 0 ? `${hours}:${pad(minutes)}:${pad(secs)}` : `${minutes}:${pad(secs)}`;
}

export function clearScrubber() {
    if (activeScrubberCleanup) {
        activeScrubberCleanup();
        activeScrubberCleanup = null;
    }
}

function bindScrubber(video, scrubber) {
    clearScrubber();
    if (!(video instanceof HTMLVideoElement) || !(scrubber instanceof HTMLElement)) return;

    const timeLabel = scrubber.querySelector('.scrubber-time');
    let rafId = 0;
    let scrubbing = false;
    let activePointerId = null;

    const setRatio = ratio => {
        const clamped = Math.max(0, Math.min(1, Number.isFinite(ratio) ? ratio : 0));
        scrubber.style.setProperty('--progress', `${(clamped * 100).toFixed(3)}%`);
        scrubber.setAttribute('aria-valuenow', String(Math.round(clamped * 100)));
        return clamped;
    };

    const updateTimeLabel = (current, duration) => {
        if (!timeLabel) return;
        timeLabel.textContent = Number.isFinite(duration) && duration > 0
            ? `${formatTime(current)} / ${formatTime(duration)}`
            : formatTime(current);
    };

    const updateBuffered = () => {
        const duration = video.duration;
        if (!Number.isFinite(duration) || duration <= 0 || video.buffered.length === 0) {
            scrubber.style.setProperty('--buffered', '0%');
            return;
        }
        const end = video.buffered.end(video.buffered.length - 1);
        const clamped = Math.max(0, Math.min(1, end / duration));
        scrubber.style.setProperty('--buffered', `${(clamped * 100).toFixed(2)}%`);
    };

    const updateFromVideo = () => {
        scrubber.classList.toggle('is-paused', video.paused);
        updateBuffered();
        if (scrubbing) return;
        const duration = video.duration;
        const ratio = Number.isFinite(duration) && duration > 0 ? video.currentTime / duration : 0;
        setRatio(ratio);
        updateTimeLabel(video.currentTime, duration);
    };

    const stopLoop = () => {
        if (rafId) {
            cancelAnimationFrame(rafId);
            rafId = 0;
        }
    };

    const tick = () => {
        rafId = 0;
        if (!video.isConnected || !scrubber.isConnected) return;
        updateFromVideo();
        if (!video.paused) {
            rafId = requestAnimationFrame(tick);
        }
    };

    const startLoop = () => {
        if (!rafId && !video.paused) {
            rafId = requestAnimationFrame(tick);
        }
    };

    const ratioFromClientX = clientX => {
        const rect = scrubber.getBoundingClientRect();
        if (rect.width <= 0) return 0;
        return Math.max(0, Math.min(1, (clientX - rect.left) / rect.width));
    };

    const seekToClientX = clientX => {
        const ratio = setRatio(ratioFromClientX(clientX));
        const duration = video.duration;
        if (Number.isFinite(duration) && duration > 0) {
            const targetTime = ratio * duration;
            updateTimeLabel(targetTime, duration);
            if (Math.abs(video.currentTime - targetTime) > 0.04) {
                video.currentTime = targetTime;
            }
        }
    };

    const onPlay = () => {
        scrubber.classList.remove('is-paused');
        updateFromVideo();
        startLoop();
    };

    const onPause = () => {
        stopLoop();
        scrubber.classList.add('is-paused');
        updateFromVideo();
    };

    const onPointerDown = event => {
        if (event.pointerType === 'mouse' && event.button !== 0) return;
        event.preventDefault();
        event.stopPropagation();
        scrubbing = true;
        activePointerId = event.pointerId;
        scrubber.classList.add('is-scrubbing');
        try { scrubber.setPointerCapture(event.pointerId); } catch { }
        seekToClientX(event.clientX);
    };

    const onPointerMove = event => {
        if (!scrubbing || event.pointerId !== activePointerId) return;
        event.preventDefault();
        event.stopPropagation();
        seekToClientX(event.clientX);
    };

    const endScrub = event => {
        if (!scrubbing || (activePointerId !== null && event.pointerId !== activePointerId)) return;
        event.stopPropagation();
        if (typeof event.clientX === 'number' && event.type === 'pointerup') {
            seekToClientX(event.clientX);
        }
        scrubbing = false;
        const pointerId = activePointerId;
        activePointerId = null;
        scrubber.classList.remove('is-scrubbing');
        if (pointerId !== null) {
            try { scrubber.releasePointerCapture(pointerId); } catch { }
        }
        updateFromVideo();
        if (!video.paused) startLoop();
    };

    const onClick = event => {
        event.stopPropagation();
    };

    const onKeyDown = event => {
        if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
        const duration = video.duration;
        if (!Number.isFinite(duration) || duration <= 0) return;
        event.preventDefault();
        event.stopPropagation();
        const delta = event.key === 'ArrowRight' ? 5 : -5;
        video.currentTime = Math.max(0, Math.min(duration, video.currentTime + delta));
        updateFromVideo();
    };

    video.addEventListener('play', onPlay);
    video.addEventListener('playing', onPlay);
    video.addEventListener('pause', onPause);
    video.addEventListener('ended', onPause);
    video.addEventListener('timeupdate', updateFromVideo);
    video.addEventListener('loadedmetadata', updateFromVideo);
    video.addEventListener('durationchange', updateFromVideo);
    video.addEventListener('progress', updateBuffered);
    video.addEventListener('seeked', updateFromVideo);

    scrubber.addEventListener('pointerdown', onPointerDown);
    scrubber.addEventListener('pointermove', onPointerMove);
    scrubber.addEventListener('pointerup', endScrub);
    scrubber.addEventListener('pointercancel', endScrub);
    scrubber.addEventListener('lostpointercapture', endScrub);
    scrubber.addEventListener('click', onClick);
    scrubber.addEventListener('keydown', onKeyDown);

    updateFromVideo();
    if (!video.paused) startLoop();

    activeScrubberCleanup = () => {
        stopLoop();
        video.removeEventListener('play', onPlay);
        video.removeEventListener('playing', onPlay);
        video.removeEventListener('pause', onPause);
        video.removeEventListener('ended', onPause);
        video.removeEventListener('timeupdate', updateFromVideo);
        video.removeEventListener('loadedmetadata', updateFromVideo);
        video.removeEventListener('durationchange', updateFromVideo);
        video.removeEventListener('progress', updateBuffered);
        video.removeEventListener('seeked', updateFromVideo);

        scrubber.removeEventListener('pointerdown', onPointerDown);
        scrubber.removeEventListener('pointermove', onPointerMove);
        scrubber.removeEventListener('pointerup', endScrub);
        scrubber.removeEventListener('pointercancel', endScrub);
        scrubber.removeEventListener('lostpointercapture', endScrub);
        scrubber.removeEventListener('click', onClick);
        scrubber.removeEventListener('keydown', onKeyDown);
    };
}

export function syncVideo(video, scrubber, muted) {
    if (!(video instanceof HTMLVideoElement)) {
        clearScrubber();
        return false;
    }
    video.muted = !!muted;
    bindScrubber(video, scrubber);
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

function disposePreloadEntry(entry) {
    if (!entry) return;
    if (entry.element instanceof HTMLVideoElement) {
        entry.element.pause();
        entry.element.removeAttribute('src');
        entry.element.load();
    }
}

export function clearPreloads() {
    for (const entry of preloadedMedia.values()) {
        disposePreloadEntry(entry);
    }
    preloadedMedia.clear();
}

export function preloadMedia(targets) {
    if (!Array.isArray(targets)) {
        clearPreloads();
        return;
    }

    const wanted = new Map();
    for (const target of targets) {
        if (!target || typeof target.url !== 'string' || !target.url) continue;
        const kind = target.isVideo ? (target.fullPreload ? 'video-auto' : 'video-meta') : 'image';
        if (!wanted.has(target.url) || kind === 'video-auto') {
            wanted.set(target.url, kind);
        }
        if (typeof target.posterUrl === 'string' && target.posterUrl) {
            if (!wanted.has(target.posterUrl)) {
                wanted.set(target.posterUrl, 'image');
            }
        }
    }

    for (const [url, entry] of preloadedMedia.entries()) {
        if (!wanted.has(url)) {
            disposePreloadEntry(entry);
            preloadedMedia.delete(url);
        }
    }

    for (const [url, kind] of wanted.entries()) {
        const existing = preloadedMedia.get(url);
        if (existing) {
            if (existing.kind === 'video-meta' && kind === 'video-auto' && existing.element instanceof HTMLVideoElement) {
                existing.kind = 'video-auto';
                existing.element.preload = 'auto';
                existing.element.load();
            }
            continue;
        }

        if (kind === 'image') {
            const img = new Image();
            img.decoding = 'async';
            img.src = url;
            img.decode?.().catch(() => {});
            preloadedMedia.set(url, { kind, element: img });
        } else {
            const video = document.createElement('video');
            video.muted = true;
            video.playsInline = true;
            video.preload = kind === 'video-auto' ? 'auto' : 'metadata';
            video.src = url;
            video.load();
            preloadedMedia.set(url, { kind, element: video });
        }
    }
}